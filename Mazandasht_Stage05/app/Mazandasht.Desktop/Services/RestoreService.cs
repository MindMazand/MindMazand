using System;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace Mazandasht.Desktop.Services
{
    /// <summary>
    /// Restore the operational database from a backup file (G11).
    ///
    /// Rules honoured:
    ///  - the caller must hold backup.manage and be an active ADMIN or MANAGER
    ///    (owner/manager approval, never a clerk);
    ///  - the exact confirmation phrase must be typed;
    ///  - the backup file is verified before anything is touched;
    ///  - the current database is backed up first (reason PRE_RESTORE) so a wrong
    ///    restore is itself recoverable;
    ///  - the restore is atomic: the backup is copied to a temporary file, the
    ///    temporary file is verified, and only then swapped in. The original
    ///    backup file is never moved or deleted.
    /// </summary>
    public static class RestoreService
    {
        public const string ConfirmationPhrase = "بازیابی نسخه پشتیبان مازندشت";

        private const string SqlApproverCheck =
            @"SELECT COUNT(*) FROM users u
              JOIN roles r ON r.id=u.role_id
              WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
                AND r.role_code IN ('ADMIN','MANAGER');";

        /// <summary>
        /// Returns the path of the safety backup taken before the restore.
        /// </summary>
        public static string Restore(string databasePath, string backupPath,
            string userId, string confirmation)
        {
            if (!String.Equals(confirmation, ConfirmationPhrase, StringComparison.Ordinal))
                throw new InvalidOperationException("عبارت تأیید بازیابی دقیقاً وارد نشده است.");
            if (String.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
                throw new FileNotFoundException("پایگاه داده عملیاتی پیدا نشد.", databasePath);
            if (String.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
                throw new FileNotFoundException("فایل نسخه پشتیبان پیدا نشد.", backupPath);

            var fullDatabasePath = Path.GetFullPath(databasePath);
            var fullBackupPath = Path.GetFullPath(backupPath);
            if (String.Equals(fullDatabasePath, fullBackupPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("مسیر بازیابی نمی‌تواند همان مسیر دیتابیس فعال باشد.");

            RequireApprover(fullDatabasePath, userId);

            // The backup must be readable and internally consistent before we
            // touch the live database.
            if (!BackupService.DatabaseIsHealthy(fullBackupPath))
                throw new InvalidOperationException(
                    "نسخه پشتیبان سالم نیست (integrity_check / foreign_key_check ناموفق).");

            var safetyPath = Path.Combine(
                Path.GetDirectoryName(fullBackupPath) ?? String.Empty,
                "mazandasht-PRE_RESTORE-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
                Guid.NewGuid().ToString("N").Substring(0, 8) + ".db");

            var staged = fullDatabasePath + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            var oldDatabase = fullDatabasePath + ".before-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            var safetyCreated = false;
            try
            {
                BackupService.WriteBackup(fullDatabasePath, safetyPath);
                safetyCreated = true;

                // Stage a copy so the original backup file stays untouched.
                File.Copy(fullBackupPath, staged, false);
                if (!BackupService.DatabaseIsHealthy(staged))
                    throw new InvalidOperationException("نسخه پشتیبان کپی‌شده سالم نیست.");

                SQLiteConnection.ClearAllPools();
                using (var connection = Open(fullDatabasePath))
                    Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                SQLiteConnection.ClearAllPools();
                DeleteSidecars(fullDatabasePath);

                File.Move(fullDatabasePath, oldDatabase);
                try
                {
                    File.Move(staged, fullDatabasePath);
                }
                catch
                {
                    if (File.Exists(fullDatabasePath)) File.Delete(fullDatabasePath);
                    if (File.Exists(oldDatabase)) File.Move(oldDatabase, fullDatabasePath);
                    throw;
                }

                SQLiteConnection.ClearAllPools();
                if (!BackupService.DatabaseIsHealthy(fullDatabasePath))
                    throw new InvalidOperationException("دیتابیس بازیابی‌شده سالم نیست.");

                RecordRestore(fullDatabasePath, userId, fullBackupPath, safetyPath);
                if (File.Exists(oldDatabase)) File.Delete(oldDatabase);
                return safetyPath;
            }
            catch
            {
                SQLiteConnection.ClearAllPools();
                if (File.Exists(oldDatabase))
                {
                    if (File.Exists(fullDatabasePath)) File.Delete(fullDatabasePath);
                    File.Move(oldDatabase, fullDatabasePath);
                }
                if (File.Exists(staged)) File.Delete(staged);
                DeleteSidecars(staged);
                if (safetyCreated && File.Exists(safetyPath)) File.Delete(safetyPath);
                throw;
            }
        }

        /// <summary>
        /// True when the user may approve a restore (active ADMIN or MANAGER).
        /// </summary>
        public static bool CanApprove(string databasePath, string userId)
        {
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlApproverCheck, connection))
            {
                command.Parameters.AddWithValue("@user", userId ?? String.Empty);
                return Convert.ToInt32(command.ExecuteScalar()) == 1;
            }
        }

        // ------------------------------------------------------------------
        private static void RequireApprover(string databasePath, string userId)
        {
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlApproverCheck, connection))
            {
                command.Parameters.AddWithValue("@user", userId ?? String.Empty);
                if (Convert.ToInt32(command.ExecuteScalar()) != 1)
                    throw new UnauthorizedAccessException(
                        "بازیابی نسخه پشتیبان فقط توسط مدیر سیستم یا مدیر مجموعهٔ فعال مجاز است.");
            }
        }

        private static void RecordRestore(string databasePath, string userId,
            string backupPath, string safetyPath)
        {
            var info = new FileInfo(backupPath);
            using (var stream = File.OpenRead(backupPath))
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(stream))
                    .Replace("-", String.Empty).ToLowerInvariant();
                using (var connection = Open(databasePath))
                using (var transaction = connection.BeginTransaction())
                {
                    Execute(connection, transaction,
                        @"INSERT INTO backup_runs(id,profile_id,reason,file_path,file_size_bytes,sha256,
                                status,created_by,started_at_utc,completed_at_utc)
                          VALUES(@id,NULL,'PRE_RESTORE',@path,@size,@sha,'SUCCESS',@user,@now,@now);",
                        new[] { "@id", "@path", "@size", "@sha", "@user", "@now" },
                        new object[] { Guid.NewGuid().ToString("N"), backupPath, info.Length,
                            hash, userId, UtcNow() });
                    Execute(connection, transaction,
                        @"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
                          VALUES(@id,@actor,'DATABASE_RESTORED','BACKUP_RUN',NULL,@after,@machine);",
                        new[] { "@id", "@actor", "@after", "@machine" },
                        new object[] { Guid.NewGuid().ToString("N"), userId,
                            "{\"backup\":\"" + backupPath.Replace("\\", "\\\\") +
                            "\",\"safetyBackup\":\"" + safetyPath.Replace("\\", "\\\\") + "\"}",
                            Environment.MachineName });
                    transaction.Commit();
                }
            }
        }

        private static void DeleteSidecars(string databasePath)
        {
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                var sidecar = databasePath + suffix;
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
        }

        private static string UtcNow()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        private static void Execute(SQLiteConnection connection, string sql)
        {
            using (var command = new SQLiteCommand(sql, connection)) command.ExecuteNonQuery();
        }

        private static void Execute(SQLiteConnection connection, SQLiteTransaction transaction,
            string sql, string[] names, object[] values)
        {
            using (var command = new SQLiteCommand(sql, connection, transaction))
            {
                for (var i = 0; i < names.Length; i++)
                    command.Parameters.AddWithValue(names[i], values[i] ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
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
