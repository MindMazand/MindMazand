using System;
using System.Data.SQLite;
using System.IO;

namespace Mazandasht.Desktop.Database
{
    public static class DatabaseBootstrapper
    {
        public static string DatabasePath
        {
            get
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Mazandasht", "Data");
                Directory.CreateDirectory(root);
                return Path.Combine(root, "mazandasht.db");
            }
        }

        public static string Initialize()
        {
            var path = DatabasePath;
            var isNew = !File.Exists(path);

            if (isNew)
                SQLiteConnection.CreateFile(path);

            var connectionString = "Data Source=" + path + ";Version=3;Pooling=True;";
            using (var connection = new SQLiteConnection(connectionString))
            {
                connection.Open();
                Execute(connection, "PRAGMA foreign_keys=ON;");
                Execute(connection, "PRAGMA busy_timeout=5000;");
                Execute(connection, "PRAGMA journal_mode=WAL;");
                Execute(connection, "PRAGMA synchronous=FULL;");

                var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
                if (version == 0)
                {
                    ExecuteScript(connection, ReadAppFile("Database/schema_v1.sql"));
                    ExecuteScript(connection, ReadAppFile("Database/system_seed.sql"));
                    version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
                }

                if (version < 2)
                {
                    ExecuteScript(connection, ReadAppFile("Database/migration_002.sql"));
                    version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
                }

                if (version < 3)
                {
                    ExecuteScript(connection, ReadAppFile("Database/migration_003.sql"));
                    version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
                }

                if (version < 4)
                {
                    ExecuteScript(connection, ReadAppFile("Database/migration_004.sql"));
                    version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
                }

                if (version < 5)
                {
                    ExecuteScript(connection, ReadAppFile("Database/migration_005.sql"));
                    version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
                }

                if (version < 6)
                {
                    ExecuteScript(connection, ReadAppFile("Database/migration_006.sql"));
                    version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
                }

                if (version < 7)
                {
                    ExecuteScript(connection, ReadAppFile("Database/migration_007.sql"));
                    version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
                }

                if (version != 7)
                    throw new InvalidOperationException("نسخه ساختار دیتابیس پشتیبانی نمی‌شود: " + version);

                Execute(connection, "PRAGMA foreign_keys=ON;");
                var integrity = Convert.ToString(Scalar(connection, "PRAGMA quick_check;"));
                if (!String.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("کنترل سلامت دیتابیس موفق نبود: " + integrity);
            }

            return path;
        }

        private static string ReadAppFile(string relativePath)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException("فایل لازم برنامه پیدا نشد.", path);
            return File.ReadAllText(path);
        }

        private static void ExecuteScript(SQLiteConnection connection, string sql)
        {
            using (var command = new SQLiteCommand(sql, connection))
                command.ExecuteNonQuery();
        }

        private static void Execute(SQLiteConnection connection, string sql)
        {
            using (var command = new SQLiteCommand(sql, connection))
                command.ExecuteNonQuery();
        }

        private static object Scalar(SQLiteConnection connection, string sql)
        {
            using (var command = new SQLiteCommand(sql, connection))
                return command.ExecuteScalar();
        }
    }
}