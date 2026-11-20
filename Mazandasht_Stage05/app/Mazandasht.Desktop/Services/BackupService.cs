using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Mazandasht.Desktop.Services
{
    public sealed class BackupProfileRow
    {
        public string Id { get; set; }
        public string ProfileName { get; set; }
        public string DestinationPath { get; set; }
        public int KeepLastN { get; set; }
        public int KeepMonthlyN { get; set; }
        public bool BackupOnLogout { get; set; }
        public bool BackupOnExit { get; set; }
        public bool IsActive { get; set; }
        public string CreatedBy { get; set; }
    }

    public sealed class BackupRunRow
    {
        public string Id { get; set; }
        public string ProfileId { get; set; }
        public string Reason { get; set; }
        public string FilePath { get; set; }
        public long FileSizeBytes { get; set; }
        public string Sha256 { get; set; }
        public string Status { get; set; }
        public string CreatedBy { get; set; }
        public string StartedAtUtc { get; set; }
        public string CompletedAtUtc { get; set; }
        public string ErrorText { get; set; }
    }

    /// <summary>
    /// Backup profiles, runs, retention and verification (G11).
    ///
    /// A backup is a real SQLite online backup (not a file copy of a possibly
    /// mid-write database), so it is consistent even while the application is
    /// running. Every run is recorded in backup_runs with its SHA-256, and the
    /// file is verified again after it is written.
    ///
    /// Writing a backup needs the backup.manage permission; the service checks
    /// it on the caller's behalf so the UI cannot bypass it.
    /// </summary>
    public static class BackupService
    {
        private const string SqlPermissionCheck =
            @"SELECT COUNT(*) FROM users u
              JOIN roles r ON r.id=u.role_id
              JOIN role_permissions rp ON rp.role_id=r.id
              WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
                AND rp.permission_code='backup.manage' AND rp.granted=1;";

        private const string SqlProfileInsert =
            @"INSERT INTO backup_profiles(id,profile_name,destination_path,keep_last_n,keep_monthly_n,
                    backup_on_logout,backup_on_exit,is_active,created_by)
              VALUES(@id,@name,@path,@keepLast,@keepMonthly,@onLogout,@onExit,1,@user);";
        private const string SqlProfileUpdate =
            @"UPDATE backup_profiles SET destination_path=@path,keep_last_n=@keepLast,
                    keep_monthly_n=@keepMonthly,backup_on_logout=@onLogout,backup_on_exit=@onExit
              WHERE id=@id;";
        private const string SqlProfileActive =
            @"UPDATE backup_profiles SET is_active=@active WHERE id=@id;";
        private const string SqlProfiles =
            @"SELECT id,profile_name,destination_path,keep_last_n,keep_monthly_n,
                     backup_on_logout,backup_on_exit,is_active,created_by
              FROM backup_profiles ORDER BY profile_name;";
        private const string SqlProfileById =
            @"SELECT id,profile_name,destination_path,keep_last_n,keep_monthly_n,
                     backup_on_logout,backup_on_exit,is_active,created_by
              FROM backup_profiles WHERE id=@id;";
        private const string SqlRunInsert =
            @"INSERT INTO backup_runs(id,profile_id,reason,file_path,status,created_by)
              VALUES(@id,@profile,@reason,@path,'STARTED',@user);";
        private const string SqlRunSuccess =
            @"UPDATE backup_runs SET status='SUCCESS',file_size_bytes=@size,sha256=@sha,
                     completed_at_utc=@completed WHERE id=@id;";
        private const string SqlRunFailed =
            @"UPDATE backup_runs SET status='FAILED',completed_at_utc=@completed,
                     error_text=@error WHERE id=@id;";
        private const string SqlRunVerified =
            @"UPDATE backup_runs SET status='VERIFIED' WHERE id=@id;";
        private const string SqlRuns =
            @"SELECT id,profile_id,reason,file_path,file_size_bytes,sha256,status,
                     created_by,started_at_utc,completed_at_utc,error_text
              FROM backup_runs ORDER BY started_at_utc DESC LIMIT 500;";
        private const string SqlActiveExitProfiles =
            @"SELECT id FROM backup_profiles WHERE is_active=1 AND backup_on_exit=1;";
        private const string SqlActiveLogoutProfiles =
            @"SELECT id FROM backup_profiles WHERE is_active=1 AND backup_on_logout=1;";

        public static IList<BackupProfileRow> ListProfiles(string databasePath)
        {
            var rows = new List<BackupProfileRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlProfiles, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) rows.Add(ReadProfile(reader));
            return rows;
        }

        public static BackupProfileRow GetProfile(string databasePath, string profileId)
        {
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlProfileById, connection))
            {
                command.Parameters.AddWithValue("@id", profileId ?? String.Empty);
                using (var reader = command.ExecuteReader())
                    return reader.Read() ? ReadProfile(reader) : null;
            }
        }

        public static void CreateProfile(string databasePath, string profileName,
            string destinationPath, int keepLastN, int keepMonthlyN,
            bool backupOnLogout, bool backupOnExit, string userId)
        {
            profileName = (profileName ?? String.Empty).Trim();
            if (profileName.Length < 2)
                throw new ArgumentException("نام پروفایل پشتیبان باید حداقل ۲ نویسه باشد.");
            if (String.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("مسیر نگهداری نسخه پشتیبان الزامی است.");
            if (keepLastN < 1) keepLastN = 1;
            if (keepMonthlyN < 0) keepMonthlyN = 0;

            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequirePermission(connection, transaction, userId);
                using (var insert = new SQLiteCommand(SqlProfileInsert, connection, transaction))
                {
                    insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    insert.Parameters.AddWithValue("@name", profileName);
                    insert.Parameters.AddWithValue("@path", destinationPath);
                    insert.Parameters.AddWithValue("@keepLast", keepLastN);
                    insert.Parameters.AddWithValue("@keepMonthly", keepMonthlyN);
                    insert.Parameters.AddWithValue("@onLogout", backupOnLogout ? 1 : 0);
                    insert.Parameters.AddWithValue("@onExit", backupOnExit ? 1 : 0);
                    insert.Parameters.AddWithValue("@user", userId);
                    insert.ExecuteNonQuery();
                }
                Audit(connection, transaction, userId, "BACKUP_PROFILE_CREATED", "BACKUP_PROFILE", null);
                transaction.Commit();
            }
        }

        public static void UpdateProfile(string databasePath, string profileId,
            string destinationPath, int keepLastN, int keepMonthlyN,
            bool backupOnLogout, bool backupOnExit, string userId)
        {
            if (String.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("مسیر نگهداری نسخه پشتیبان الزامی است.");
            if (keepLastN < 1) keepLastN = 1;
            if (keepMonthlyN < 0) keepMonthlyN = 0;

            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequirePermission(connection, transaction, userId);
                using (var update = new SQLiteCommand(SqlProfileUpdate, connection, transaction))
                {
                    update.Parameters.AddWithValue("@id", profileId);
                    update.Parameters.AddWithValue("@path", destinationPath);
                    update.Parameters.AddWithValue("@keepLast", keepLastN);
                    update.Parameters.AddWithValue("@keepMonthly", keepMonthlyN);
                    update.Parameters.AddWithValue("@onLogout", backupOnLogout ? 1 : 0);
                    update.Parameters.AddWithValue("@onExit", backupOnExit ? 1 : 0);
                    update.ExecuteNonQuery();
                }
                Audit(connection, transaction, userId, "BACKUP_PROFILE_UPDATED", "BACKUP_PROFILE", profileId);
                transaction.Commit();
            }
        }

        public static void SetActive(string databasePath, string profileId, bool isActive, string userId)
        {
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequirePermission(connection, transaction, userId);
                using (var update = new SQLiteCommand(SqlProfileActive, connection, transaction))
                {
                    update.Parameters.AddWithValue("@id", profileId);
                    update.Parameters.AddWithValue("@active", isActive ? 1 : 0);
                    update.ExecuteNonQuery();
                }
                Audit(connection, transaction, userId,
                    isActive ? "BACKUP_PROFILE_ENABLED" : "BACKUP_PROFILE_DISABLED",
                    "BACKUP_PROFILE", profileId);
                transaction.Commit();
            }
        }

        /// <summary>
        /// Runs one profile now and applies its retention rules.
        /// </summary>
        public static BackupRunRow Run(string databasePath, string profileId, string reason, string userId)
        {
            var profile = GetProfile(databasePath, profileId);
            if (profile == null) throw new InvalidOperationException("پروفایل پشتیبان پیدا نشد.");
            if (!profile.IsActive) throw new InvalidOperationException("پروفایل پشتیبان غیرفعال است.");
            var directory = ProfileDirectory(profile);
            Directory.CreateDirectory(directory);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var fileName = "mazandasht-" + reason + "-" + stamp + "-" +
                Guid.NewGuid().ToString("N").Substring(0, 8) + ".db";
            var target = Path.Combine(directory, fileName);

            var runId = Guid.NewGuid().ToString("N");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequirePermission(connection, transaction, userId);
                using (var insert = new SQLiteCommand(SqlRunInsert, connection, transaction))
                {
                    insert.Parameters.AddWithValue("@id", runId);
                    insert.Parameters.AddWithValue("@profile", profileId);
                    insert.Parameters.AddWithValue("@reason", reason);
                    insert.Parameters.AddWithValue("@path", target);
                    insert.Parameters.AddWithValue("@user", userId);
                    insert.ExecuteNonQuery();
                }
                Audit(connection, transaction, userId, "BACKUP_STARTED", "BACKUP_RUN", runId);
                transaction.Commit();
            }

            string failure = null;
            try
            {
                CreateConsistentBackup(databasePath, target);
                var hash = Sha256File(target);
                var size = new FileInfo(target).Length;

                using (var connection = Open(databasePath))
                using (var transaction = connection.BeginTransaction())
                using (var success = new SQLiteCommand(SqlRunSuccess, connection, transaction))
                {
                    success.Parameters.AddWithValue("@id", runId);
                    success.Parameters.AddWithValue("@size", size);
                    success.Parameters.AddWithValue("@sha", hash);
                    success.Parameters.AddWithValue("@completed", UtcNow());
                    success.ExecuteNonQuery();
                    transaction.Commit();
                }

                ApplyRetention(directory, profile.KeepLastN, profile.KeepMonthlyN, target);
                return FindRun(databasePath, runId);
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                }
                catch { /* the run record is the useful outcome */ }
                using (var connection = Open(databasePath))
                using (var transaction = connection.BeginTransaction())
                using (var failed = new SQLiteCommand(SqlRunFailed, connection, transaction))
                {
                    failed.Parameters.AddWithValue("@id", runId);
                    failed.Parameters.AddWithValue("@completed", UtcNow());
                    failed.Parameters.AddWithValue("@error", failure);
                    failed.ExecuteNonQuery();
                    transaction.Commit();
                }
                throw new InvalidOperationException("ساخت نسخه پشتیبان ناموفق بود: " + failure);
            }
        }

        /// <summary>
        /// Re-runs the verification queries against a backup file and marks the
        /// run VERIFIED. Returns false when the file is damaged.
        /// </summary>
        public static bool Verify(string databasePath, string runId, string userId)
        {
            var run = FindRun(databasePath, runId);
            if (run == null) throw new InvalidOperationException("رکورد پشتیبان پیدا نشد.");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequirePermission(connection, transaction, userId);
                transaction.Commit();
            }
            if (!File.Exists(run.FilePath))
                throw new FileNotFoundException("فایل نسخه پشتیبان پیدا نشد.", run.FilePath);
            if (!DatabaseIsHealthy(run.FilePath))
                return false;
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            using (var verified = new SQLiteCommand(SqlRunVerified, connection, transaction))
            {
                verified.Parameters.AddWithValue("@id", runId);
                verified.ExecuteNonQuery();
                transaction.Commit();
            }
            return true;
        }

        public static IList<BackupRunRow> History(string databasePath)
        {
            var rows = new List<BackupRunRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlRuns, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    rows.Add(new BackupRunRow {
                        Id = reader.GetString(0),
                        ProfileId = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Reason = reader.GetString(2),
                        FilePath = reader.GetString(3),
                        FileSizeBytes = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                        Sha256 = reader.IsDBNull(5) ? null : reader.GetString(5),
                        Status = reader.GetString(6),
                        CreatedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
                        StartedAtUtc = reader.GetString(8),
                        CompletedAtUtc = reader.IsDBNull(9) ? null : reader.GetString(9),
                        ErrorText = reader.IsDBNull(10) ? null : reader.GetString(10)
                    });
                }
            return rows;
        }

        /// <summary>
        /// Called when the operator logs out: runs every active profile that asks
        /// for a logout backup. Failures are collected, never thrown, because a
        /// logout must not be blocked by a full disk.
        /// </summary>
        public static IList<string> RunLogoutBackups(string databasePath, string userId)
        {
            return RunFlagged(databasePath, SqlActiveLogoutProfiles, "LOGOUT", userId);
        }

        /// <summary>
        /// Called when the application exits: runs every active profile that asks
        /// for an exit backup.
        /// </summary>
        public static IList<string> RunExitBackups(string databasePath, string userId)
        {
            return RunFlagged(databasePath, SqlActiveExitProfiles, "APP_EXIT", userId);
        }

        // ------------------------------------------------------------------
        private static IList<string> RunFlagged(string databasePath, string sql,
            string reason, string userId)
        {
            var failures = new List<string>();
            IList<string> ids;
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(sql, connection))
            using (var reader = command.ExecuteReader())
            {
                ids = new List<string>();
                while (reader.Read()) ids.Add(reader.GetString(0));
            }
            foreach (var id in ids)
            {
                try { Run(databasePath, id, reason, userId); }
                catch (Exception ex) { failures.Add(ex.Message); }
            }
            return failures;
        }

        private static BackupRunRow FindRun(string databasePath, string runId)
        {
            foreach (var row in History(databasePath))
                if (row.Id == runId) return row;
            return null;
        }

        /// <summary>
        /// Keeps the newest keepLastN files and, independently, the newest file of
        /// each of the newest keepMonthlyN months. The file just written is never
        /// deleted.
        /// </summary>
        private static void ApplyRetention(string directory, int keepLastN, int keepMonthlyN,
            string protectedFile)
        {
            var parsed = new List<FileInfo>();
            foreach (var path in Directory.GetFiles(directory, "*.db"))
            {
                DateTime stamp;
                if (TryStamp(Path.GetFileName(path), out stamp)) parsed.Add(new FileInfo(path));
            }
            if (parsed.Count <= keepLastN) return;

            var ordered = parsed.OrderByDescending(f => StampOf(f.Name)).ToList();
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < ordered.Count && i < keepLastN; i++) keep.Add(ordered[i].FullName);
            if (keepMonthlyN > 0)
            {
                var months = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in ordered)
                {
                    var month = StampOf(file.Name).ToString("yyyy-MM", CultureInfo.InvariantCulture);
                    if (months.Contains(month)) continue;
                    months.Add(month);
                    keep.Add(file.FullName);
                    if (months.Count >= keepMonthlyN) break;
                }
            }
            keep.Add(Path.GetFullPath(protectedFile));

            foreach (var file in parsed)
                if (!keep.Contains(file.FullName)) SafeDelete(file.FullName);
        }

        private static DateTime StampOf(string fileName)
        {
            DateTime stamp;
            TryStamp(fileName, out stamp);
            return stamp;
        }

        private static bool TryStamp(string fileName, out DateTime stamp)
        {
            // mazandasht-<reason>-yyyyMMdd-HHmmss-<8 hex>.db
            var parts = (fileName ?? String.Empty).Split('-');
            stamp = DateTime.MinValue;
            if (parts.Length < 4) return false;
            var datePart = parts[parts.Length - 3];
            var timePart = parts[parts.Length - 2];
            if (datePart.Length != 8 || timePart.Length != 6) return false;
            int year, month, day, hour, minute, second;
            if (!Int32.TryParse(datePart.Substring(0, 4), out year) ||
                !Int32.TryParse(datePart.Substring(4, 2), out month) ||
                !Int32.TryParse(datePart.Substring(6, 2), out day) ||
                !Int32.TryParse(timePart.Substring(0, 2), out hour) ||
                !Int32.TryParse(timePart.Substring(2, 2), out minute) ||
                !Int32.TryParse(timePart.Substring(4, 2), out second))
                return false;
            stamp = new DateTime(year, month, day, hour, minute, second,
                DateTimeKind.Unspecified);
            return true;
        }

        private static void SafeDelete(string path)
        {
            try { File.Delete(path); }
            catch { /* retention is best effort; the next run retries */ }
        }

        private static string ProfileDirectory(BackupProfileRow profile)
        {
            var safe = profile.ProfileName;
            foreach (var invalid in Path.GetInvalidFileNameChars())
                safe = safe.Replace(invalid, '-');
            return Path.Combine(profile.DestinationPath, safe);
        }

        /// <summary>
        /// Writes a consistent backup of sourcePath to targetPath and returns its
        /// SHA-256. Used by the restore path to keep a safety copy of the live
        /// database before it is swapped. No permission check here: the caller
        /// (RestoreService) has already authorised the operation.
        /// </summary>
        public static string WriteBackup(string sourcePath, string targetPath)
        {
            if (String.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new FileNotFoundException("پایگاه داده مبدأ پیدا نشد.", sourcePath);
            if (String.IsNullOrWhiteSpace(targetPath))
                throw new ArgumentException("مسیر نسخه پشتیبان الزامی است.");
            CreateConsistentBackup(sourcePath, targetPath);
            return Sha256File(targetPath);
        }

        /// <summary>
        /// SQLite online backup: consistent even while the source is in use.
        /// </summary>
        private static void CreateConsistentBackup(string sourcePath, string targetPath)
        {
            if (File.Exists(targetPath)) File.Delete(targetPath);
            SQLiteConnection.CreateFile(targetPath);
            try
            {
                using (var source = Open(sourcePath))
                using (var destination = Open(targetPath))
                    source.BackupDatabase(destination, "main", "main", -1, null, 0);
                if (!DatabaseIsHealthy(targetPath))
                    throw new InvalidOperationException("کنترل سلامت نسخه پشتیبان ناموفق بود.");
            }
            catch
            {
                if (File.Exists(targetPath)) File.Delete(targetPath);
                throw;
            }
        }

        /// <summary>
        /// integrity_check + foreign_key_check + a readable schema version.
        /// </summary>
        public static bool DatabaseIsHealthy(string databasePath)
        {
            try
            {
                using (var connection = Open(databasePath))
                {
                    var check = Convert.ToString(Scalar(connection, "PRAGMA integrity_check;"));
                    if (!String.Equals(check, "ok", StringComparison.OrdinalIgnoreCase)) return false;
                    using (var command = new SQLiteCommand("PRAGMA foreign_key_check;", connection))
                    using (var reader = command.ExecuteReader())
                        if (reader.Read()) return false;
                    var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
                    return version >= 1;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string Sha256File(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream))
                    .Replace("-", String.Empty).ToLowerInvariant();
        }

        private static BackupProfileRow ReadProfile(SQLiteDataReader reader)
        {
            return new BackupProfileRow {
                Id = reader.GetString(0),
                ProfileName = reader.GetString(1),
                DestinationPath = reader.GetString(2),
                KeepLastN = reader.GetInt32(3),
                KeepMonthlyN = reader.GetInt32(4),
                BackupOnLogout = reader.GetInt32(5) == 1,
                BackupOnExit = reader.GetInt32(6) == 1,
                IsActive = reader.GetInt32(7) == 1,
                CreatedBy = reader.IsDBNull(8) ? null : reader.GetString(8)
            };
        }

        private static void RequirePermission(SQLiteConnection connection,
            SQLiteTransaction transaction, string userId)
        {
            using (var command = new SQLiteCommand(SqlPermissionCheck, connection, transaction))
            {
                command.Parameters.AddWithValue("@user", userId ?? String.Empty);
                if (Convert.ToInt32(command.ExecuteScalar()) != 1)
                    throw new UnauthorizedAccessException(
                        "پشتیبان‌گیری فقط برای کاربر دارای مجوز «مدیریت پشتیبان» مجاز است.");
            }
        }

        private static void Audit(SQLiteConnection connection, SQLiteTransaction transaction,
            string userId, string action, string entityType, string entityId)
        {
            using (var command = new SQLiteCommand(
                @"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,machine_name)
                  VALUES(@id,@actor,@action,@entityType,@entityId,@machine);",
                connection, transaction))
            {
                command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("@actor", userId);
                command.Parameters.AddWithValue("@action", action);
                command.Parameters.AddWithValue("@entityType", entityType);
                command.Parameters.AddWithValue("@entityId", (object)entityId ?? DBNull.Value);
                command.Parameters.AddWithValue("@machine", Environment.MachineName);
                command.ExecuteNonQuery();
            }
        }

        private static string UtcNow()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        private static object Scalar(SQLiteConnection connection, string sql)
        {
            using (var command = new SQLiteCommand(sql, connection)) return command.ExecuteScalar();
        }

        private static SQLiteConnection Open(string databasePath)
        {
            var connection = new SQLiteConnection(
                "Data Source=" + databasePath + ";Version=3;Pooling=True;");
            connection.Open();
            using (var command = new SQLiteCommand(
                "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000; PRAGMA synchronous=FULL;", connection))
                command.ExecuteNonQuery();
            return connection;
        }
    }
}
