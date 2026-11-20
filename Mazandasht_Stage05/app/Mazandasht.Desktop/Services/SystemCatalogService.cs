using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Text.RegularExpressions;

namespace Mazandasht.Desktop.Services
{
    public sealed class BankCatalogRow
    {
        public string BankCode { get; set; }
        public string DisplayName { get; set; }
        public string RecordKind { get; set; }
        public string Status { get; set; }
    }

    public sealed class OperationCatalogRow
    {
        public string OperationCode { get; set; }
        public string DisplayName { get; set; }
        public string Direction { get; set; }
        public string MainGroup { get; set; }
        public string RecordKind { get; set; }
    }

    public static class SystemCatalogService
    {
        public static IList<BankCatalogRow> GetBanks(string databasePath)
        {
            var rows = new List<BankCatalogRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(@"
                SELECT bank_code,display_name,
                       CASE WHEN is_system=1 THEN 'پیش‌فرض سیستم' ELSE 'تعریف کاربر' END,
                       CASE WHEN is_active=1 THEN 'فعال' ELSE 'غیرفعال' END
                FROM bank_catalog ORDER BY is_system DESC,display_name;", connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new BankCatalogRow {
                        BankCode=reader.GetString(0), DisplayName=reader.GetString(1),
                        RecordKind=reader.GetString(2), Status=reader.GetString(3)
                    });
            return rows;
        }

        public static IList<OperationCatalogRow> GetOperations(string databasePath)
        {
            var rows = new List<OperationCatalogRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(@"
                SELECT o.operation_code,o.display_name,o.direction,COALESCE(g.display_name,'—'),
                       CASE WHEN o.operation_code LIKE 'SYS-%' THEN 'پیش‌فرض سیستم' ELSE 'تعریف کاربر' END
                FROM operations o LEFT JOIN main_groups g ON g.id=o.main_group_id
                WHERE o.is_active=1 ORDER BY g.display_name,o.display_name;", connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new OperationCatalogRow {
                        OperationCode=reader.GetString(0), DisplayName=reader.GetString(1),
                        Direction=DirectionLabel(reader.GetString(2)), MainGroup=reader.GetString(3),
                        RecordKind=reader.GetString(4)
                    });
            return rows;
        }

        public static IList<LookupItem> GetMainGroups(string databasePath)
        {
            var rows = new List<LookupItem>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(
                "SELECT id,group_code,display_name FROM main_groups WHERE is_active=1 ORDER BY display_name;", connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new LookupItem { Id=reader.GetString(0), Code=reader.GetString(1), Name=reader.GetString(2) });
            return rows;
        }

        public static void AddBank(string databasePath, string bankCode, string displayName, string userId)
        {
            bankCode = NormalizeCode(bankCode);
            displayName = (displayName ?? String.Empty).Trim();
            if (displayName.Length < 2) throw new ArgumentException("نام بانک باید حداقل دو نویسه باشد.");
            if (bankCode.Length < 2) throw new ArgumentException("کد بانک باید حداقل دو نویسه باشد.");

            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                using (var command = new SQLiteCommand(@"
                    INSERT INTO bank_catalog(id,bank_code,display_name,is_system,is_active)
                    VALUES(@id,@code,@name,0,1);", connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@code", bankCode);
                    command.Parameters.AddWithValue("@name", displayName);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public static void AddOperation(string databasePath, string operationCode, string displayName,
            string direction, string mainGroupId, string userId)
        {
            operationCode = NormalizeCode(operationCode);
            displayName = (displayName ?? String.Empty).Trim();
            direction = (direction ?? String.Empty).Trim().ToUpperInvariant();
            if (operationCode.Length < 3) throw new ArgumentException("کد عملیات باید حداقل سه نویسه باشد.");
            if (displayName.Length < 2) throw new ArgumentException("عنوان عملیات معتبر نیست.");
            if (direction != "IN" && direction != "OUT" && direction != "NEUTRAL")
                throw new ArgumentException("ماهیت عملیات باید ورود، خروج یا خنثی باشد.");

            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                using (var command = new SQLiteCommand(@"
                    INSERT INTO operations(id,operation_code,display_name,direction,main_group_id,notes,is_active)
                    VALUES(@id,@code,@name,@direction,@group,'تعریف‌شده توسط مدیر از فهرست عملیات',1);", connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@code", operationCode);
                    command.Parameters.AddWithValue("@name", displayName);
                    command.Parameters.AddWithValue("@direction", direction);
                    command.Parameters.AddWithValue("@group", String.IsNullOrWhiteSpace(mainGroupId) ? (object)DBNull.Value : mainGroupId);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        private static void RequireBaseManager(SQLiteConnection connection, SQLiteTransaction transaction, string userId)
        {
            using (var command = new SQLiteCommand(@"
                SELECT COUNT(*) FROM users u
                JOIN roles r ON r.id=u.role_id
                JOIN role_permissions rp ON rp.role_id=r.id
                WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
                  AND rp.permission_code='base.manage' AND rp.granted=1;", connection, transaction))
            {
                command.Parameters.AddWithValue("@user", userId ?? String.Empty);
                if (Convert.ToInt32(command.ExecuteScalar()) != 1)
                    throw new UnauthorizedAccessException("تعریف بانک یا عملیات فقط برای مدیر دارای مجوز اطلاعات پایه مجاز است.");
            }
        }

        private static string NormalizeCode(string value)
        {
            var code = (value ?? String.Empty).Trim().ToUpperInvariant();
            if (!Regex.IsMatch(code, @"^[A-Z0-9][A-Z0-9_-]{1,39}$"))
                throw new ArgumentException("کد باید فقط شامل حروف انگلیسی، عدد، خط تیره یا زیرخط باشد.");
            return code;
        }

        private static string DirectionLabel(string value)
        {
            if (value == "IN") return "ورودی";
            if (value == "OUT") return "خروجی";
            return "خنثی";
        }

        private static SQLiteConnection Open(string path)
        {
            var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;Pooling=True;");
            connection.Open();
            using (var command = new SQLiteCommand("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", connection))
                command.ExecuteNonQuery();
            return connection;
        }
    }
}
