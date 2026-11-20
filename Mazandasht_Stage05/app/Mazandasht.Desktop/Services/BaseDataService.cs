using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Text;
using System.Text.RegularExpressions;

namespace Mazandasht.Desktop.Services
{
    /// <summary>
    /// A single catalog row for the base-data screens. Kept deliberately small so
    /// the code-built grids can bind to it without a DataGrid column per entity.
    /// </summary>
    public sealed class CatalogRow
    {
        public string Id { get; set; }
        public string Code { get; set; }
        public string DisplayName { get; set; }
        public string Detail { get; set; }
        public string Status { get; set; }
    }

    /// <summary>
    /// An operational parameter (fiscal year convention, currency, VAT rate, ...).
    /// </summary>
    public sealed class OperationalParameterRow
    {
        public string ParameterCode { get; set; }
        public string DisplayName { get; set; }
        public string ValueText { get; set; }
        public string ValueKind { get; set; }
        public string UnitCode { get; set; }
        public string Notes { get; set; }
    }

    /// <summary>
    /// Base-data management for the catalog entities that the operational modules
    /// depend on but that the original 0.5.1-alpha2 build did not expose:
    /// main/sub groups, transaction types, product grades and packaging links,
    /// work groups and employees, operational parameters, accounting natures and
    /// groups, and real bank accounts.
    ///
    /// Rules honoured here (G2 contract):
    ///  - reference codes are never rewritten; an unwanted record is deactivated
    ///    (is_active=0) so existing documents keep resolving to it;
    ///  - every write happens in one transaction together with an audit_log row;
    ///  - all writes require the base.manage permission;
    ///  - no real business data is created by this class.
    /// </summary>
    public static class BaseDataService
    {
        // ------------------------------------------------------------------
        // Statements. Kept as named consts so the automated tests can extract
        // and execute the exact SQL that ships in the product.
        // ------------------------------------------------------------------
        private const string SqlMainGroupInsert =
            @"INSERT INTO main_groups(id,group_code,display_name,notes,is_active)
              VALUES(@id,@code,@name,@notes,1);";
        private const string SqlMainGroupUpdate =
            @"UPDATE main_groups SET display_name=@name, notes=@notes
              WHERE id=@id AND group_code=@code;";
        private const string SqlMainGroupList =
            @"SELECT id,group_code,display_name,COALESCE(notes,''),is_active
              FROM main_groups ORDER BY display_name;";

        private const string SqlSubGroupInsert =
            @"INSERT INTO subgroups(id,subgroup_code,main_group_id,display_name,ledger_account_id,notes,is_active)
              VALUES(@id,@code,@mainGroup,@name,@ledger,@notes,1);";
        private const string SqlSubGroupList =
            @"SELECT s.id,s.subgroup_code,s.display_name,COALESCE(g.display_name,'—'),
                     COALESCE(la.account_code,'—'),s.is_active
              FROM subgroups s
              JOIN main_groups g ON g.id=s.main_group_id
              LEFT JOIN ledger_accounts la ON la.id=s.ledger_account_id
              ORDER BY g.display_name,s.display_name;";

        private const string SqlTransactionTypeInsert =
            @"INSERT INTO transaction_types(id,type_code,display_name,balance_effect,notes,is_active)
              VALUES(@id,@code,@name,@effect,@notes,1);";
        private const string SqlTransactionTypeList =
            @"SELECT id,type_code,display_name,balance_effect,COALESCE(notes,''),is_active
              FROM transaction_types ORDER BY display_name;";

        private const string SqlProductGradeInsert =
            @"INSERT INTO product_grades(id,product_id,grade_code,display_name,sort_order,is_active)
              VALUES(@id,@product,@code,@name,@sort,1);";
        private const string SqlProductGradeList =
            @"SELECT g.id,g.grade_code,g.display_name,g.sort_order,g.is_active
              FROM product_grades g WHERE g.product_id=@product ORDER BY g.sort_order,g.display_name;";

        private const string SqlProductPackagingLink =
            @"INSERT INTO product_packaging(product_id,packaging_type_id,is_default)
              VALUES(@product,@packaging,@default);";
        private const string SqlProductPackagingDefault =
            @"UPDATE product_packaging SET is_default=(product_id=@product AND packaging_type_id=@packaging);";
        private const string SqlProductPackagingList =
            @"SELECT pp.product_id,pp.packaging_type_id,p.packaging_code,p.display_name,pp.is_default
              FROM product_packaging pp JOIN packaging_types p ON p.id=pp.packaging_type_id
              WHERE pp.product_id=@product ORDER BY pp.is_default DESC,p.display_name;";

        private const string SqlWorkGroupInsert =
            @"INSERT INTO work_groups(id,work_group_code,display_name,notes,is_active)
              VALUES(@id,@code,@name,@notes,1);";
        private const string SqlWorkGroupList =
            @"SELECT id,work_group_code,display_name,COALESCE(notes,''),is_active
              FROM work_groups ORDER BY display_name;";

        private const string SqlEmployeeInsert =
            @"INSERT INTO employees(id,party_account_id,work_group_id,job_title,notes,is_active)
              VALUES(@id,@partyAccount,@workGroup,@job,@notes,1);";
        private const string SqlEmployeeList =
            @"SELECT e.id,COALESCE(la.account_code,'—'),p.display_name,
                     COALESCE(w.display_name,'—'),COALESCE(e.job_title,'—'),e.is_active
              FROM employees e
              JOIN party_accounts pa ON pa.id=e.party_account_id
              JOIN parties p ON p.id=pa.party_id
              LEFT JOIN ledger_accounts la ON la.id=pa.ledger_account_id
              LEFT JOIN work_groups w ON w.id=e.work_group_id
              ORDER BY p.display_name;";

        private const string SqlParameterList =
            @"SELECT parameter_code,display_name,COALESCE(value_integer,''),COALESCE(value_text,''),
                     value_kind,COALESCE(unit_code,''),COALESCE(notes,''),is_active
              FROM operational_parameters WHERE is_active=1 ORDER BY parameter_code;";
        private const string SqlParameterUpsert =
            @"INSERT INTO operational_parameters(id,parameter_code,display_name,value_integer,value_text,
                    value_kind,unit_code,effective_from_iso,notes,is_active)
              VALUES(@id,@code,@name,@intValue,@textValue,@kind,@unit,@effectiveFrom,@notes,1)
              ON CONFLICT(parameter_code) DO UPDATE SET
                    display_name=@name, value_integer=@intValue, value_text=@textValue,
                    value_kind=@kind, unit_code=@unit, effective_from_iso=@effectiveFrom,
                    notes=@notes, is_active=1;";

        private const string SqlAccountNatureInsert =
            @"INSERT INTO account_natures(id,nature_code,display_name,code_prefix,is_active)
              VALUES(@id,@code,@name,@prefix,1);";
        private const string SqlAccountNatureList =
            @"SELECT id,nature_code,display_name,COALESCE(code_prefix,''),is_active
              FROM account_natures ORDER BY display_name;";

        private const string SqlAccountSubNatureInsert =
            @"INSERT INTO account_subnatures(id,nature_id,display_name,accounting_effect,is_active)
              VALUES(@id,@nature,@name,@effect,1);";
        private const string SqlAccountSubNatureList =
            @"SELECT s.id,s.display_name,s.accounting_effect,s.is_active
              FROM account_subnatures s WHERE s.nature_id=@nature ORDER BY s.display_name;";

        private const string SqlAccountingGroupInsert =
            @"INSERT INTO accounting_groups(id,group_code,display_name,is_active)
              VALUES(@id,@code,@name,1);";
        private const string SqlAccountingGroupList =
            @"SELECT id,COALESCE(group_code,''),display_name,is_active
              FROM accounting_groups ORDER BY display_name;";

        private const string SqlAuditGroupInsert =
            @"INSERT INTO audit_groups(id,group_code,display_name,is_active)
              VALUES(@id,@code,@name,1);";
        private const string SqlAuditGroupList =
            @"SELECT id,COALESCE(group_code,''),display_name,is_active
              FROM audit_groups ORDER BY display_name;";

        private const string SqlBankAccountInsert =
            @"INSERT INTO bank_accounts(id,ledger_account_id,display_title,bank_name,branch_name,
                    account_number_enc,iban_enc,owner_name,is_active,notes)
              VALUES(@id,@ledger,@title,@bank,@branch,@number,@iban,@owner,1,@notes);";
        private const string SqlBankAccountList =
            @"SELECT b.id,b.display_title,COALESCE(b.bank_name,'—'),COALESCE(b.branch_name,'—'),
                     COALESCE(b.owner_name,'—'),CASE WHEN b.account_number_enc IS NULL THEN 0 ELSE 1 END,
                     CASE WHEN b.iban_enc IS NULL THEN 0 ELSE 1 END,b.is_active
              FROM bank_accounts b ORDER BY b.display_title;";

        private const string SqlBankLedgerInsert =
            @"INSERT INTO ledger_accounts(id,account_code,display_name,account_class,normal_side,is_system_account)
              VALUES(@id,@code,@name,'ASSET','DEBIT',0);";

        // ------------------------------------------------------------------
        // Shared helpers
        // ------------------------------------------------------------------
        private static SQLiteConnection Open(string databasePath)
        {
            var connection = new SQLiteConnection(
                "Data Source=" + databasePath + ";Version=3;Pooling=True;");
            connection.Open();
            using (var command = new SQLiteCommand(
                "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", connection))
                command.ExecuteNonQuery();
            return connection;
        }

        private static string NormalizeCode(string value, string message)
        {
            var code = (value ?? String.Empty).Trim().ToUpperInvariant();
            if (!Regex.IsMatch(code, @"^[A-Z0-9][A-Z0-9_-]{1,39}$"))
                throw new ArgumentException(message);
            return code;
        }

        private static string RequireText(string value, int minLength, string message)
        {
            var text = (value ?? String.Empty).Trim();
            if (text.Length < minLength) throw new ArgumentException(message);
            return text;
        }

        private static string RequireId(string value, string message)
        {
            var id = (value ?? String.Empty).Trim();
            if (id.Length != 32) throw new ArgumentException(message);
            return id;
        }

        private static void RequireBaseManager(SQLiteConnection connection,
            SQLiteTransaction transaction, string userId)
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
                    throw new UnauthorizedAccessException(
                        "این تغییر فقط برای کاربر دارای مجوز «مدیریت اطلاعات پایه» مجاز است.");
            }
        }

        private static void WriteAudit(SQLiteConnection connection, SQLiteTransaction transaction,
            string userId, string actionCode, string entityType, string entityId, string afterJson)
        {
            using (var audit = new SQLiteCommand(@"
                INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
                VALUES(@id,@actor,@action,@entityType,@entityId,@after,@machine);", connection, transaction))
            {
                audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                audit.Parameters.AddWithValue("@actor", (object)userId ?? DBNull.Value);
                audit.Parameters.AddWithValue("@action", actionCode);
                audit.Parameters.AddWithValue("@entityType", entityType);
                audit.Parameters.AddWithValue("@entityId", entityId);
                audit.Parameters.AddWithValue("@after", afterJson);
                audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                audit.ExecuteNonQuery();
            }
        }

        private static string Json(params string[] pairs)
        {
            var builder = new StringBuilder("{");
            for (var i = 0; i < pairs.Length; i += 2)
            {
                if (i > 0) builder.Append(',');
                builder.Append('"').Append(pairs[i]).Append("\":\"")
                       .Append(pairs[i + 1].Replace("\\", "\\\\").Replace("\"", "\\\""))
                       .Append('"');
            }
            builder.Append('}');
            return builder.ToString();
        }

        private static string StatusLabel(bool isActive)
        {
            return isActive ? "فعال" : "غیرفعال";
        }

        private static string ActiveFlag(object value)
        {
            return value != null && value != DBNull.Value && Convert.ToInt32(value) == 1;
        }

        // ------------------------------------------------------------------
        // Main groups / sub groups
        // ------------------------------------------------------------------
        public static IList<CatalogRow> MainGroups(string databasePath)
        {
            var rows = new List<CatalogRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlMainGroupList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new CatalogRow {
                        Id = reader.GetString(0), Code = reader.GetString(1),
                        DisplayName = reader.GetString(2), Detail = reader.GetString(3),
                        Status = StatusLabel(ActiveFlag(reader.GetValue(4)))
                    });
            return rows;
        }

        public static void CreateMainGroup(string databasePath, string code, string displayName,
            string notes, string userId)
        {
            var normalized = NormalizeCode(code, "کد گروه اصلی باید فقط حروف انگلیسی، عدد، خط تیره یا زیرخط باشد.");
            var name = RequireText(displayName, 2, "نام گروه اصلی معتبر نیست.");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                using (var command = new SQLiteCommand(SqlMainGroupInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@code", normalized);
                    command.Parameters.AddWithValue("@name", name);
                    command.Parameters.AddWithValue("@notes", (object)notes ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
                WriteAudit(connection, transaction, userId, "CREATE_MAIN_GROUP", "MAIN_GROUP",
                    normalized, Json("group_code", normalized, "display_name", name));
                transaction.Commit();
            }
        }

        /// <summary>
        /// Renames a main group. The reference code never changes (G2 rule), so
        /// documents and operations that already point at it keep resolving.
        /// </summary>
        public static void RenameMainGroup(string databasePath, string id, string code,
            string displayName, string notes, string userId)
        {
            var normalized = NormalizeCode(code, "کد گروه اصلی نامعتبر است.");
            var name = RequireText(displayName, 2, "نام گروه اصلی معتبر نیست.");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                using (var command = new SQLiteCommand(SqlMainGroupUpdate, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", RequireId(id, "شناسه گروه اصلی نامعتبر است."));
                    command.Parameters.AddWithValue("@code", normalized);
                    command.Parameters.AddWithValue("@name", name);
                    command.Parameters.AddWithValue("@notes", (object)notes ?? DBNull.Value);
                    if (command.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("گروه اصلی یافت نشد.");
                }
                WriteAudit(connection, transaction, userId, "RENAME_MAIN_GROUP", "MAIN_GROUP",
                    id, Json("group_code", normalized, "display_name", name));
                transaction.Commit();
            }
        }

        public static IList<CatalogRow> SubGroups(string databasePath)
        {
            var rows = new List<CatalogRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlSubGroupList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new CatalogRow {
                        Id = reader.GetString(0), Code = reader.GetString(1),
                        DisplayName = reader.GetString(2),
                        Detail = reader.GetString(3) + " · " + reader.GetString(4),
                        Status = StatusLabel(ActiveFlag(reader.GetValue(5)))
                    });
            return rows;
        }

        public static void CreateSubGroup(string databasePath, string code, string mainGroupId,
            string displayName, string ledgerAccountId, string notes, string userId)
        {
            var normalized = NormalizeCode(code, "کد زیرگروه نامعتبر است.");
            var name = RequireText(displayName, 2, "نام زیرگروه معتبر نیست.");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                using (var command = new SQLiteCommand(SqlSubGroupInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@code", normalized);
                    command.Parameters.AddWithValue("@mainGroup", RequireId(mainGroupId, "گروه اصلی نامعتبر است."));
                    command.Parameters.AddWithValue("@name", name);
                    // The screen accepts either an account id or an account code.
                    command.Parameters.AddWithValue("@ledger",
                        (object)ResolveLedgerAccount(connection, transaction, ledgerAccountId) ?? DBNull.Value);
                    command.Parameters.AddWithValue("@notes", (object)notes ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
                WriteAudit(connection, transaction, userId, "CREATE_SUBGROUP", "SUBGROUP",
                    normalized, Json("subgroup_code", normalized, "display_name", name));
                transaction.Commit();
            }
        }

        // ------------------------------------------------------------------
        // Transaction types
        // ------------------------------------------------------------------
        public static IList<CatalogRow> TransactionTypes(string databasePath)
        {
            var rows = new List<CatalogRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlTransactionTypeList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new CatalogRow {
                        Id = reader.GetString(0), Code = reader.GetString(1),
                        DisplayName = reader.GetString(2),
                        Detail = EffectLabel(reader.GetString(3)),
                        Status = StatusLabel(ActiveFlag(reader.GetValue(5)))
                    });
            return rows;
        }

        public static void CreateTransactionType(string databasePath, string code, string displayName,
            string balanceEffect, string notes, string userId)
        {
            var normalized = NormalizeCode(code, "کد نوع تراکنش نامعتبر است.");
            var name = RequireText(displayName, 2, "عنوان نوع تراکنش معتبر نیست.");
            var effect = (balanceEffect ?? String.Empty).Trim().ToUpperInvariant();
            if (!new[] { "INCREASE", "DECREASE", "NEUTRAL", "BY_DIRECTION", "CUSTOM" }.Contains(effect))
                throw new ArgumentException("اثر تراکنش بر مانده نامعتبر است.");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                using (var command = new SQLiteCommand(SqlTransactionTypeInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@code", normalized);
                    command.Parameters.AddWithValue("@name", name);
                    command.Parameters.AddWithValue("@effect", effect);
                    command.Parameters.AddWithValue("@notes", (object)notes ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
                WriteAudit(connection, transaction, userId, "CREATE_TRANSACTION_TYPE", "TRANSACTION_TYPE",
                    normalized, Json("type_code", normalized, "display_name", name));
                transaction.Commit();
            }
        }

        // ------------------------------------------------------------------
        // Product grades and packaging links
        // ------------------------------------------------------------------
        public static IList<CatalogRow> ProductGrades(string databasePath, string productId)
        {
            var rows = new List<CatalogRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlProductGradeList, connection))
            {
                command.Parameters.AddWithValue("@product", RequireId(productId, "محصول نامعتبر است."));
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        rows.Add(new CatalogRow {
                            Id = reader.GetString(0), Code = reader.GetString(1),
                            DisplayName = reader.GetString(2), Detail = "ترتیب " + reader.GetString(3),
                            Status = StatusLabel(ActiveFlag(reader.GetValue(4)))
                        });
            }
            return rows;
        }

        public static void CreateProductGrade(string databasePath, string productId, string code,
            string displayName, int sortOrder, string userId)
        {
            var normalized = NormalizeCode(code, "کد درجه محصول نامعتبر است.");
            var name = RequireText(displayName, 2, "عنوان درجه محصول معتبر نیست.");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                using (var command = new SQLiteCommand(SqlProductGradeInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@product", RequireId(productId, "محصول نامعتبر است."));
                    command.Parameters.AddWithValue("@code", normalized);
                    command.Parameters.AddWithValue("@name", name);
                    command.Parameters.AddWithValue("@sort", sortOrder);
                    command.ExecuteNonQuery();
                }
                WriteAudit(connection, transaction, userId, "CREATE_PRODUCT_GRADE", "PRODUCT_GRADE",
                    normalized, Json("grade_code", normalized, "product_id", productId));
                transaction.Commit();
            }
        }

        public static IList<CatalogRow> ProductPackaging(string databasePath, string productId)
        {
            var rows = new List<CatalogRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlProductPackagingList, connection))
            {
                command.Parameters.AddWithValue("@product", RequireId(productId, "محصول نامعتبر است."));
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        rows.Add(new CatalogRow {
                            Id = reader.GetString(1), Code = reader.GetString(2),
                            DisplayName = reader.GetString(3),
                            Detail = ActiveFlag(reader.GetValue(4)) ? "پیش‌فرض" : "جایگزین",
                            Status = "فعال"
                        });
            }
            return rows;
        }

        /// <summary>
        /// Links a packaging type to a product and makes it the product default.
        /// The default flag is reassigned in one statement so at most one default
        /// can exist per product.
        /// </summary>
        public static void SetProductPackaging(string databasePath, string productId,
            string packagingTypeId, bool makeDefault, string userId)
        {
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                using (var exists = new SQLiteCommand(
                    "SELECT COUNT(*) FROM product_packaging WHERE product_id=@product AND packaging_type_id=@packaging;",
                    connection, transaction))
                {
                    exists.Parameters.AddWithValue("@product", RequireId(productId, "محصول نامعتبر است."));
                    exists.Parameters.AddWithValue("@packaging", RequireId(packagingTypeId, "بسته‌بندی نامعتبر است."));
                    if (Convert.ToInt32(exists.ExecuteScalar()) == 0)
                    {
                        using (var link = new SQLiteCommand(SqlProductPackagingLink, connection, transaction))
                        {
                            link.Parameters.AddWithValue("@product", productId);
                            link.Parameters.AddWithValue("@packaging", packagingTypeId);
                            link.Parameters.AddWithValue("@default", makeDefault ? 1 : 0);
                            link.ExecuteNonQuery();
                        }
                    }
                    else if (makeDefault)
                    {
                        using (var flag = new SQLiteCommand(
                            "UPDATE product_packaging SET is_default=1 WHERE product_id=@product AND packaging_type_id=@packaging;",
                            connection, transaction))
                        {
                            flag.Parameters.AddWithValue("@product", productId);
                            flag.Parameters.AddWithValue("@packaging", packagingTypeId);
                            flag.ExecuteNonQuery();
                        }
                    }
                }
                if (makeDefault)
                {
                    using (var clear = new SQLiteCommand(SqlProductPackagingDefault, connection, transaction))
                    {
                        clear.Parameters.AddWithValue("@product", productId);
                        clear.Parameters.AddWithValue("@packaging", packagingTypeId);
                        clear.ExecuteNonQuery();
                    }
                }
                WriteAudit(connection, transaction, userId, "SET_PRODUCT_PACKAGING", "PRODUCT",
                    productId, Json("product_id", productId, "packaging_type_id", packagingTypeId,
                                    "is_default", makeDefault ? "1" : "0"));
                transaction.Commit();
            }
        }

        // ------------------------------------------------------------------
        // Work groups and employees
        // ------------------------------------------------------------------
        public static IList<CatalogRow> WorkGroups(string databasePath)
        {
            var rows = new List<CatalogRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlWorkGroupList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new CatalogRow {
                        Id = reader.GetString(0), Code = reader.GetString(1),
                        DisplayName = reader.GetString(2), Detail = reader.GetString(3),
                        Status = StatusLabel(ActiveFlag(reader.GetValue(4)))
                    });
            return rows;
        }

        public static void CreateWorkGroup(string databasePath, string code, string displayName,
            string notes, string userId)
        {
            var normalized = NormalizeCode(code, "کد گروه کاری نامعتبر است.");
            var name = RequireText(displayName, 2, "نام گروه کاری معتبر نیست.");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                using (var command = new SQLiteCommand(SqlWorkGroupInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@code", normalized);
                    command.Parameters.AddWithValue("@name", name);
                    command.Parameters.AddWithValue("@notes", (object)notes ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
                WriteAudit(connection, transaction, userId, "CREATE_WORK_GROUP", "WORK_GROUP",
                    normalized, Json("work_group_code", normalized, "display_name", name));
                transaction.Commit();
            }
        }

        public static IList<CatalogRow> Employees(string databasePath)
        {
            var rows = new List<CatalogRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlEmployeeList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new CatalogRow {
                        Id = reader.GetString(0), Code = reader.GetString(1),
                        DisplayName = reader.GetString(2),
                        Detail = reader.GetString(3) + " · " + reader.GetString(4),
                        Status = StatusLabel(ActiveFlag(reader.GetValue(5)))
                    });
            return rows;
        }

        public static void CreateEmployee(string databasePath, string partyAccountId,
            string workGroupId, string jobTitle, string notes, string userId)
        {
            var title = (jobTitle ?? String.Empty).Trim();
            if (title.Length < 2) throw new ArgumentException("عنوان شغلی معتبر نیست.");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                using (var command = new SQLiteCommand(SqlEmployeeInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@partyAccount",
                        RequireId(partyAccountId, "طرف حساب کارکن نامعتبر است."));
                    command.Parameters.AddWithValue("@workGroup", (object)workGroupId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@job", title);
                    command.Parameters.AddWithValue("@notes", (object)notes ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
                WriteAudit(connection, transaction, userId, "CREATE_EMPLOYEE", "EMPLOYEE",
                    partyAccountId, Json("party_account_id", partyAccountId, "job_title", title));
                transaction.Commit();
            }
        }

        // ------------------------------------------------------------------
        // Operational parameters
        // ------------------------------------------------------------------
        public static IList<OperationalParameterRow> Parameters(string databasePath)
        {
            var rows = new List<OperationalParameterRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlParameterList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new OperationalParameterRow {
                        ParameterCode = reader.GetString(0), DisplayName = reader.GetString(1),
                        ValueText = Coalesce(reader.GetValue(2)) + Coalesce(reader.GetValue(3)),
                        ValueKind = reader.GetString(4), UnitCode = reader.GetString(5),
                        Notes = reader.GetString(6)
                    });
            return rows;
        }

        public static string GetParameter(string databasePath, string parameterCode)
        {
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(@"
                SELECT COALESCE(CAST(value_integer AS TEXT),''),COALESCE(value_text,'')
                FROM operational_parameters WHERE parameter_code=@code AND is_active=1;", connection))
            {
                command.Parameters.AddWithValue("@code", parameterCode);
                using (var reader = command.ExecuteReader())
                    if (reader.Read()) return Coalesce(reader.GetValue(0)) + Coalesce(reader.GetValue(1));
            }
            return null;
        }

        /// <summary>
        /// Creates or updates an operational parameter. The parameter code is the
        /// stable identifier and never changes; only the value, its kind and the
        /// effective-from date are updated.
        /// </summary>
        public static void SetParameter(string databasePath, string parameterCode, string displayName,
            string value, string valueKind, string unitCode, string effectiveFromIso, string notes,
            string userId)
        {
            var code = NormalizeCode(parameterCode, "کد پارامتر عملیاتی نامعتبر است.");
            var name = RequireText(displayName, 2, "عنوان پارامتر معتبر نیست.");
            var kind = (valueKind ?? String.Empty).Trim().ToUpperInvariant();
            if (kind != "INTEGER" && kind != "TEXT" && kind != "BOOLEAN")
                throw new ArgumentException("نوع مقدار پارامتر باید INTEGER، TEXT یا BOOLEAN باشد.");
            if (String.IsNullOrWhiteSpace(value))
                throw new ArgumentException("مقدار پارامتر الزامی است.");

            object intValue = null;
            object textValue = null;
            if (kind == "INTEGER")
            {
                long parsed;
                if (!Int64.TryParse(value.Trim(), out parsed))
                    throw new ArgumentException("مقدار پارامتر باید عدد صحیح باشد.");
                intValue = parsed;
            }
            else if (kind == "BOOLEAN")
            {
                var flag = value.Trim();
                if (flag != "0" && flag != "1" && !flag.Equals("true", StringComparison.OrdinalIgnoreCase)
                    && !flag.Equals("false", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("مقدار بولین باید true/false یا ۰/۱ باشد.");
                intValue = (flag == "1" || flag.Equals("true", StringComparison.OrdinalIgnoreCase)) ? 1L : 0L;
            }
            else
            {
                textValue = value.Trim();
            }

            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                using (var command = new SQLiteCommand(SqlParameterUpsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@code", code);
                    command.Parameters.AddWithValue("@name", name);
                    command.Parameters.AddWithValue("@intValue", intValue ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@textValue", textValue ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@kind", kind);
                    command.Parameters.AddWithValue("@unit", (object)unitCode ?? DBNull.Value);
                    command.Parameters.AddWithValue("@effectiveFrom", (object)effectiveFromIso ?? DBNull.Value);
                    command.Parameters.AddWithValue("@notes", (object)notes ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
                WriteAudit(connection, transaction, userId, "SET_PARAMETER", "PARAMETER", code,
                    Json("parameter_code", code, "value", value.Trim()));
                transaction.Commit();
            }
        }

        // ------------------------------------------------------------------
        // Accounting natures / sub-natures / groups (G9 support)
        // ------------------------------------------------------------------
        public static IList<CatalogRow> AccountNatures(string databasePath)
        {
            var rows = new List<CatalogRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlAccountNatureList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new CatalogRow {
                        Id = reader.GetString(0), Code = reader.GetString(1),
                        DisplayName = reader.GetString(2), Detail = reader.GetString(3),
                        Status = StatusLabel(ActiveFlag(reader.GetValue(4)))
                    });
            return rows;
        }

        public static void CreateAccountNature(string databasePath, string code, string displayName,
            string codePrefix, string userId)
        {
            var normalized = NormalizeCode(code, "کد ماهیت حساب نامعتبر است.");
            var name = RequireText(displayName, 2, "عنوان ماهیت حساب معتبر نیست.");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                using (var command = new SQLiteCommand(SqlAccountNatureInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@code", normalized);
                    command.Parameters.AddWithValue("@name", name);
                    command.Parameters.AddWithValue("@prefix", (object)codePrefix ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
                WriteAudit(connection, transaction, userId, "CREATE_ACCOUNT_NATURE", "ACCOUNT_NATURE",
                    normalized, Json("nature_code", normalized, "display_name", name));
                transaction.Commit();
            }
        }

        public static IList<CatalogRow> AccountSubNatures(string databasePath, string natureId)
        {
            var rows = new List<CatalogRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlAccountSubNatureList, connection))
            {
                command.Parameters.AddWithValue("@nature", RequireId(natureId, "ماهیت حساب نامعتبر است."));
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        rows.Add(new CatalogRow {
                            Id = reader.GetString(0), DisplayName = reader.GetString(1),
                            Detail = EffectLabel(reader.GetString(2)),
                            Status = StatusLabel(ActiveFlag(reader.GetValue(3)))
                        });
            }
            return rows;
        }

        public static void CreateAccountSubNature(string databasePath, string natureId,
            string displayName, string accountingEffect, string userId)
        {
            var name = RequireText(displayName, 2, "عنوان زیرماهیت معتبر نیست.");
            var effect = (accountingEffect ?? String.Empty).Trim().ToUpperInvariant();
            if (!new[] { "ASSET", "LIABILITY", "EQUITY", "REVENUE", "EXPENSE", "CONTROL" }.Contains(effect))
                throw new ArgumentException("اثر حسابداری نامعتبر است.");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                using (var command = new SQLiteCommand(SqlAccountSubNatureInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@nature", RequireId(natureId, "ماهیت حساب نامعتبر است."));
                    command.Parameters.AddWithValue("@name", name);
                    command.Parameters.AddWithValue("@effect", effect);
                    command.ExecuteNonQuery();
                }
                WriteAudit(connection, transaction, userId, "CREATE_ACCOUNT_SUBNATURE", "ACCOUNT_SUBNATURE",
                    name, Json("nature_id", natureId, "display_name", name));
                transaction.Commit();
            }
        }

        public static IList<CatalogRow> AccountingGroups(string databasePath)
        {
            var rows = new List<CatalogRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlAccountingGroupList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new CatalogRow {
                        Id = reader.GetString(0), Code = reader.GetString(1),
                        DisplayName = reader.GetString(2),
                        Status = StatusLabel(ActiveFlag(reader.GetValue(3)))
                    });
            return rows;
        }

        public static void CreateAccountingGroup(string databasePath, string code, string displayName,
            string userId)
        {
            var normalized = NormalizeCode(code, "کد گروه حسابداری نامعتبر است.");
            var name = RequireText(displayName, 2, "عنوان گروه حسابداری معتبر نیست.");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                using (var command = new SQLiteCommand(SqlAccountingGroupInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@code", normalized);
                    command.Parameters.AddWithValue("@name", name);
                    command.ExecuteNonQuery();
                }
                WriteAudit(connection, transaction, userId, "CREATE_ACCOUNTING_GROUP", "ACCOUNTING_GROUP",
                    normalized, Json("group_code", normalized, "display_name", name));
                transaction.Commit();
            }
        }

        public static IList<CatalogRow> AuditGroups(string databasePath)
        {
            var rows = new List<CatalogRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlAuditGroupList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new CatalogRow {
                        Id = reader.GetString(0), Code = reader.GetString(1),
                        DisplayName = reader.GetString(2),
                        Status = StatusLabel(ActiveFlag(reader.GetValue(3)))
                    });
            return rows;
        }

        public static void CreateAuditGroup(string databasePath, string code, string displayName,
            string userId)
        {
            var normalized = NormalizeCode(code, "کد گروه ممیزی نامعتبر است.");
            var name = RequireText(displayName, 2, "عنوان گروه ممیزی معتبر نیست.");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                using (var command = new SQLiteCommand(SqlAuditGroupInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@code", normalized);
                    command.Parameters.AddWithValue("@name", name);
                    command.ExecuteNonQuery();
                }
                WriteAudit(connection, transaction, userId, "CREATE_AUDIT_GROUP", "AUDIT_GROUP",
                    normalized, Json("group_code", normalized, "display_name", name));
                transaction.Commit();
            }
        }

        // ------------------------------------------------------------------
        // Bank accounts
        // ------------------------------------------------------------------
        public static IList<CatalogRow> BankAccounts(string databasePath)
        {
            var rows = new List<CatalogRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlBankAccountList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new CatalogRow {
                        Id = reader.GetString(0), DisplayName = reader.GetString(1),
                        Detail = reader.GetString(2) + " · " + reader.GetString(3) + " · " + reader.GetString(4),
                        Status = (ActiveFlag(reader.GetValue(7)) ? "فعال" : "غیرفعال")
                    });
            return rows;
        }

        /// <summary>
        /// Creates a real bank account. The account number and IBAN are encrypted
        /// with DPAPI before storage; only the presence of the encrypted payloads
        /// is reported back to the screen, never the clear values.
        /// </summary>
        public static string CreateBankAccount(string databasePath, string displayTitle, string bankName,
            string branchName, string accountNumber, string iban, string ownerName, string notes,
            string userId)
        {
            var title = RequireText(displayTitle, 3, "عنوان حساب بانکی معتبر نیست.");
            var ledgerId = Guid.NewGuid().ToString("N");
            var id = Guid.NewGuid().ToString("N");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireBaseManager(connection, transaction, userId);
                var next = Convert.ToInt32(new SQLiteCommand(
                    "SELECT COALESCE(MAX(CAST(SUBSTR(account_code,4) AS INTEGER)),0)+1 FROM ledger_accounts WHERE account_code LIKE 'BNK%';",
                    connection, transaction).ExecuteScalar());
                using (var ledger = new SQLiteCommand(SqlBankLedgerInsert, connection, transaction))
                {
                    ledger.Parameters.AddWithValue("@id", ledgerId);
                    ledger.Parameters.AddWithValue("@code", "BNK" + next.ToString("000000"));
                    ledger.Parameters.AddWithValue("@name", title);
                    ledger.ExecuteNonQuery();
                }
                using (var command = new SQLiteCommand(SqlBankAccountInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", id);
                    command.Parameters.AddWithValue("@ledger", ledgerId);
                    command.Parameters.AddWithValue("@title", title);
                    command.Parameters.AddWithValue("@bank", (object)bankName ?? DBNull.Value);
                    command.Parameters.AddWithValue("@branch", (object)branchName ?? DBNull.Value);
                    command.Parameters.AddWithValue("@number", (object)MasterDataService.ProtectText(accountNumber) ?? DBNull.Value);
                    command.Parameters.AddWithValue("@iban", (object)MasterDataService.ProtectText(iban) ?? DBNull.Value);
                    command.Parameters.AddWithValue("@owner", (object)ownerName ?? DBNull.Value);
                    command.Parameters.AddWithValue("@notes", (object)notes ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
                WriteAudit(connection, transaction, userId, "CREATE_BANK_ACCOUNT", "BANK_ACCOUNT", id,
                    Json("display_title", title, "bank_name", bankName ?? ""));
                transaction.Commit();
            }
            return id;
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// Resolves a ledger account from either its 32-character id or its
        /// account code. Returns null when the input is empty.
        /// </summary>
        private static string ResolveLedgerAccount(SQLiteConnection connection,
            SQLiteTransaction transaction, string value)
        {
            var candidate = (value ?? String.Empty).Trim();
            if (candidate.Length == 0) return null;
            if (candidate.Length == 32) return candidate;
            using (var command = new SQLiteCommand(
                "SELECT id FROM ledger_accounts WHERE account_code=@code AND is_active=1;",
                connection, transaction))
            {
                command.Parameters.AddWithValue("@code", candidate.ToUpperInvariant());
                return Convert.ToString(command.ExecuteScalar());
            }
        }

        private static string Coalesce(object value)
        {
            return value == null || value == DBNull.Value ? String.Empty : Convert.ToString(value);
        }

        private static string EffectLabel(string value)
        {
            switch (value)
            {
                case "INCREASE": return "افزایش مانده";
                case "DECREASE": return "کاهش مانده";
                case "NEUTRAL": return "خنثی";
                case "BY_DIRECTION": return "بر اساس جهت";
                case "ASSET": return "دارایی";
                case "LIABILITY": return "بدهی";
                case "EQUITY": return "سرمایه";
                case "REVENUE": return "درآمد";
                case "EXPENSE": return "هزینه";
                case "CONTROL": return "کنترلی";
                default: return "سایر";
            }
        }
    }
}
