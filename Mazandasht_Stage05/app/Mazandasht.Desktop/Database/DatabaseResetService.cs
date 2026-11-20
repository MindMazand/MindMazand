using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Mazandasht.Desktop.Database
{
    public sealed class DatabaseTableCount
    {
        public string TableName { get; set; }
        public long RowCount { get; set; }
    }

    public sealed class DatabaseResetPreview
    {
        public int SchemaVersion { get; set; }
        public long DatabaseBytes { get; set; }
        public long TotalRows { get; set; }
        public IList<DatabaseTableCount> Tables { get; set; }
    }

    public static class DatabaseResetService
    {
        public const string ConfirmationPhrase = "پاکسازی کامل مازندشت";

        public static DatabaseResetPreview GetPreview(string databasePath)
        {
            if (String.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
                throw new FileNotFoundException("پایگاه داده عملیاتی پیدا نشد.", databasePath);

            var preview = new DatabaseResetPreview
            {
                SchemaVersion = 0,
                DatabaseBytes = new FileInfo(databasePath).Length,
                TotalRows = 0,
                Tables = new List<DatabaseTableCount>()
            };

            using (var connection = Open(databasePath))
            {
                preview.SchemaVersion = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
                using (var command = new SQLiteCommand(@"SELECT name FROM sqlite_master
                    WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;", connection))
                using (var reader = command.ExecuteReader())
                {
                    var names = new List<string>();
                    while (reader.Read()) names.Add(reader.GetString(0));
                    foreach (var name in names)
                    {
                        var count = Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM \"" + name.Replace("\"", "\"\"") + "\";"));
                        preview.Tables.Add(new DatabaseTableCount { TableName = name, RowCount = count });
                        preview.TotalRows = checked(preview.TotalRows + count);
                    }
                }
            }
            return preview;
        }

        public static string ResetToCleanState(string databasePath, string backupPath, string userId, string confirmation)
        {
            if (!String.Equals(confirmation, ConfirmationPhrase, StringComparison.Ordinal))
                throw new InvalidOperationException("عبارت تأیید پاکسازی دقیقاً وارد نشده است.");
            if (String.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
                throw new FileNotFoundException("پایگاه داده عملیاتی پیدا نشد.", databasePath);
            if (String.IsNullOrWhiteSpace(backupPath))
                throw new ArgumentException("مسیر فایل پشتیبان الزامی است.");
            var fullDatabasePath = Path.GetFullPath(databasePath);
            var fullBackupPath = Path.GetFullPath(backupPath);
            if (String.Equals(fullDatabasePath, fullBackupPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("مسیر پشتیبان نمی‌تواند همان مسیر دیتابیس فعال باشد.");
            if (File.Exists(fullBackupPath))
                throw new IOException("فایل پشتیبان از قبل وجود دارد؛ نام دیگری انتخاب کنید تا نسخه قبلی بازنویسی نشود.");

            RequireActiveAdmin(fullDatabasePath, userId);
            var directory = Path.GetDirectoryName(fullBackupPath);
            if (!String.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var tempDatabase = fullDatabasePath + ".clean-" + Guid.NewGuid().ToString("N") + ".tmp";
            var oldDatabase = fullDatabasePath + ".before-reset-" + Guid.NewGuid().ToString("N") + ".tmp";
            var backupCreated = false;
            try
            {
                CreateConsistentBackup(fullDatabasePath, fullBackupPath);
                backupCreated = true;
                VerifyDatabase(fullBackupPath, false);

                BuildCleanDatabase(tempDatabase);
                VerifyDatabase(tempDatabase, true);

                // Ensure no pooled SQLite handle keeps the old file or its WAL sidecars open.
                SQLiteConnection.ClearAllPools();
                using (var connection = Open(fullDatabasePath))
                    Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                SQLiteConnection.ClearAllPools();
                DeleteSidecars(fullDatabasePath);

                File.Move(fullDatabasePath, oldDatabase);
                try
                {
                    File.Move(tempDatabase, fullDatabasePath);
                }
                catch
                {
                    if (File.Exists(fullDatabasePath)) File.Delete(fullDatabasePath);
                    if (File.Exists(oldDatabase)) File.Move(oldDatabase, fullDatabasePath);
                    throw;
                }

                SQLiteConnection.ClearAllPools();
                VerifyDatabase(fullDatabasePath, true);

                var reportPath = fullBackupPath + ".reset-report.txt";
                File.WriteAllText(reportPath, BuildResetReport(fullBackupPath, userId), new UTF8Encoding(false));
                if (File.Exists(oldDatabase)) File.Delete(oldDatabase);
                return fullBackupPath;
            }
            catch
            {
                SQLiteConnection.ClearAllPools();
                if (File.Exists(oldDatabase))
                {
                    if (File.Exists(fullDatabasePath)) File.Delete(fullDatabasePath);
                    File.Move(oldDatabase, fullDatabasePath);
                }
                if (File.Exists(tempDatabase)) File.Delete(tempDatabase);
                DeleteSidecars(tempDatabase);
                if (!backupCreated && File.Exists(fullBackupPath)) File.Delete(fullBackupPath);
                throw;
            }
        }

        private static void RequireActiveAdmin(string databasePath, string userId)
        {
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(@"SELECT COUNT(*) FROM users u
                JOIN roles r ON r.id=u.role_id
                WHERE u.id=@user AND u.is_active=1 AND r.role_code='ADMIN' AND r.is_active=1;", connection))
            {
                command.Parameters.AddWithValue("@user", userId ?? String.Empty);
                if (Convert.ToInt32(command.ExecuteScalar()) != 1)
                    throw new UnauthorizedAccessException("پاکسازی کامل فقط توسط مدیر سیستم فعال مجاز است.");
            }
        }

        private static void CreateConsistentBackup(string sourcePath, string backupPath)
        {
            SQLiteConnection.CreateFile(backupPath);
            try
            {
                using (var source = Open(sourcePath))
                using (var destination = Open(backupPath))
                    source.BackupDatabase(destination, "main", "main", -1, null, 0);
                VerifyDatabase(backupPath, false);
            }
            catch
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
                throw;
            }
        }

        private static void BuildCleanDatabase(string path)
        {
            if (File.Exists(path)) File.Delete(path);
            SQLiteConnection.CreateFile(path);
            using (var connection = Open(path))
            {
                Execute(connection, "PRAGMA journal_mode=DELETE;");
                Execute(connection, "PRAGMA synchronous=FULL;");
                ExecuteScript(connection, ReadSql("Database/schema_v1.sql"));
                ExecuteScript(connection, ReadSql("Database/system_seed.sql"));
                ExecuteScript(connection, ReadSql("Database/migration_002.sql"));
                ExecuteScript(connection, ReadSql("Database/migration_003.sql"));
                ExecuteScript(connection, ReadSql("Database/migration_004.sql"));
                ExecuteScript(connection, ReadSql("Database/migration_005.sql"));
                ExecuteScript(connection, ReadSql("Database/migration_006.sql"));
                ExecuteScript(connection, ReadSql("Database/migration_007.sql"));
                var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
                if (version != 7) throw new InvalidOperationException("ساخت دیتابیس پاک با نسخه مورد انتظار ۷ انجام نشد.");
                var users = Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM users;"));
                var parties = Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM parties;"));
                if (users != 0 || parties != 0)
                    throw new InvalidOperationException("دیتابیس پاک نباید شامل کاربر یا شخص واقعی باشد.");
            }
        }

        private static void VerifyDatabase(string path, bool mustBeClean)
        {
            using (var connection = Open(path))
            {
                var check = Convert.ToString(Scalar(connection, "PRAGMA integrity_check;"));
                if (!String.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("کنترل سلامت دیتابیس ناموفق بود: " + check);
                using (var command = new SQLiteCommand("PRAGMA foreign_key_check;", connection))
                using (var reader = command.ExecuteReader())
                    if (reader.Read()) throw new InvalidOperationException("کنترل کلیدهای خارجی دیتابیس ناموفق بود.");
                if (mustBeClean)
                {
                    var systemTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "schema_migrations", "app_metadata", "roles", "permissions", "role_permissions",
                        "units", "main_groups", "operations", "bank_catalog", "ledger_accounts"
                    };
                    var tableNames = new List<string>();
                    using (var tables = new SQLiteCommand(@"SELECT name FROM sqlite_master
                        WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;", connection))
                    using (var reader = tables.ExecuteReader())
                        while (reader.Read()) tableNames.Add(reader.GetString(0));

                    foreach (var tableName in tableNames)
                    {
                        if (systemTables.Contains(tableName)) continue;
                        var count = Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM \"" + tableName.Replace("\"", "\"\"") + "\";"));
                        if (count != 0)
                            throw new InvalidOperationException("دیتابیس جایگزین در جدول «" + tableName + "» دارای داده غیرسیستمی است.");
                    }
                }
            }
        }

        private static string BuildResetReport(string backupPath, string userId)
        {
            var info = new FileInfo(backupPath);
            using (var stream = File.OpenRead(backupPath))
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", String.Empty).ToLowerInvariant();
                return "Mazandasht database reset report\r\n" +
                    "CreatedAtUtc=" + DateTime.UtcNow.ToString("o") + "\r\n" +
                    "PerformedByUserId=" + (userId ?? String.Empty) + "\r\n" +
                    "BackupFile=" + info.Name + "\r\n" +
                    "BackupSizeBytes=" + info.Length + "\r\n" +
                    "BackupSha256=" + hash + "\r\n" +
                    "NewDatabaseSchemaVersion=7\r\n" +
                    "NewDatabaseBusinessData=EMPTY\r\n" +
                    "Note=Schema, system roles/permissions/default units/operations and sample bank directory are retained; users, real bank accounts and all business data are removed.\r\n";
            }
        }

        private static string ReadSql(string relativePath)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
            if (!File.Exists(path)) throw new FileNotFoundException("فایل ساخت دیتابیس پیدا نشد.", path);
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private static void DeleteSidecars(string databasePath)
        {
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                var sidecar = databasePath + suffix;
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
        }

        private static SQLiteConnection Open(string path)
        {
            var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;Pooling=False;");
            connection.Open();
            using (var command = new SQLiteCommand("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000; PRAGMA synchronous=FULL;", connection))
                command.ExecuteNonQuery();
            return connection;
        }

        private static void ExecuteScript(SQLiteConnection connection, string sql)
        {
            using (var command = new SQLiteCommand(sql, connection)) command.ExecuteNonQuery();
        }

        private static void Execute(SQLiteConnection connection, string sql)
        {
            using (var command = new SQLiteCommand(sql, connection)) command.ExecuteNonQuery();
        }

        private static object Scalar(SQLiteConnection connection, string sql)
        {
            using (var command = new SQLiteCommand(sql, connection)) return command.ExecuteScalar();
        }
    }
}
