using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;

namespace Mazandasht.Desktop.Services
{
    public sealed class BasketMovementDraft
    {
        public string PackagingTypeId { get; set; }
        public string MovementType { get; set; }
        public int Quantity { get; set; }
        public string FromPartyAccountId { get; set; }
        public string ToPartyAccountId { get; set; }
        public string FromWarehouseId { get; set; }
        public string ToWarehouseId { get; set; }
        public long UnitPriceIrr { get; set; }
        public string Notes { get; set; }
    }

    public sealed class BasketCustodyRow
    {
        public string PartyAccountId { get; set; }
        public string PartyName { get; set; }
        public long CustodyBalance { get; set; }
        public long CustodyInTotal { get; set; }
        public long CustodyOutTotal { get; set; }
    }

    public sealed class BasketMovementRow
    {
        public string DocumentNumber { get; set; }
        public string DateIso { get; set; }
        public string MovementType { get; set; }
        public string PackagingName { get; set; }
        public int Quantity { get; set; }
        public string FromParty { get; set; }
        public string ToParty { get; set; }
        public string Status { get; set; }
    }

    /// <summary>
    /// Basket custody movements (G7).
    ///
    /// Rules honoured (12_Rules_Contract.json, section "basket"):
    ///  - basket is independent of money: a movement never creates a journal line;
    ///  - custody balance = CUSTODY_IN - CUSTODY_OUT;
    ///  - a return may not exceed the confirmed custody balance of the party.
    /// Movements are recorded on a REVIEW document so a manager sees them before
    /// they affect any report.
    /// </summary>
    public static class BasketService
    {
        private static readonly string[] AllowedTypes = {
            "PURCHASE", "ISSUE", "RETURN", "CUSTODY_IN", "CUSTODY_OUT",
            "TRANSFER_IN", "TRANSFER_OUT", "ADJUSTMENT" };

        private const string SqlDocumentInsert =
            @"INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,
                    requires_journal,requires_stock_posting,party_account_id,reference_number,notes,created_by)
              VALUES(@id,'BASKET',@fiscal,'',@date,'REVIEW',0,0,@party,@reference,@notes,@user);";
        private const string SqlMovementInsert =
            @"INSERT INTO basket_movements(document_id,packaging_type_id,movement_type,quantity,
                    from_party_account_id,to_party_account_id,from_warehouse_id,to_warehouse_id,
                    unit_price_irr,amount_irr,notes)
              VALUES(@doc,@packaging,@type,@qty,@fromParty,@toParty,@fromWarehouse,@toWarehouse,
                     @price,@amount,@notes);";
        private const string SqlHistoryInsert =
            @"INSERT INTO document_status_history(id,document_id,old_status,new_status,changed_by,reason)
              VALUES(@id,@doc,NULL,'REVIEW',@user,@reason);";
        private const string SqlAuditInsert =
            @"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
              VALUES(@id,@actor,@action,@entityType,@entityId,@after,@machine);";
        private const string SqlCustody =
            @"SELECT pa.id,p.display_name,
                     COALESCE(SUM(CASE WHEN bm.movement_type='CUSTODY_IN' THEN bm.quantity END),0)
                   - COALESCE(SUM(CASE WHEN bm.movement_type='CUSTODY_OUT' THEN bm.quantity END),0),
                     COALESCE(SUM(CASE WHEN bm.movement_type='CUSTODY_IN' THEN bm.quantity END),0),
                     COALESCE(SUM(CASE WHEN bm.movement_type='CUSTODY_OUT' THEN bm.quantity END),0)
              FROM basket_movements bm
              JOIN party_accounts pa ON pa.id=bm.to_party_account_id
              JOIN parties p ON p.id=pa.party_id
              GROUP BY pa.id ORDER BY p.display_name;";
        private const string SqlPartyCustodyBalance =
            @"SELECT COALESCE(SUM(CASE WHEN movement_type='CUSTODY_IN' THEN quantity END),0)
                   - COALESCE(SUM(CASE WHEN movement_type='CUSTODY_OUT' THEN quantity END),0)
              FROM basket_movements
              WHERE to_party_account_id=@party AND document_id IN
                    (SELECT id FROM documents WHERE status='APPROVED');";
        private const string SqlMovements =
            @"SELECT d.document_number,d.date_iso,bm.movement_type,COALESCE(pk.display_name,'—'),
                     bm.quantity,COALESCE(fp.display_name,'—'),COALESCE(tp.display_name,'—'),d.status
              FROM basket_movements bm
              JOIN documents d ON d.id=bm.document_id
              LEFT JOIN packaging_types pk ON pk.id=bm.packaging_type_id
              LEFT JOIN party_accounts fpa ON fpa.id=bm.from_party_account_id
              LEFT JOIN parties fp ON fp.id=fpa.party_id
              LEFT JOIN party_accounts tpa ON tpa.id=bm.to_party_account_id
              LEFT JOIN parties tp ON tp.id=tpa.party_id
              ORDER BY d.created_at_utc DESC LIMIT 300;";
        private const string SqlPermissionCheck =
            @"SELECT COUNT(*) FROM users u
              JOIN roles r ON r.id=u.role_id
              JOIN role_permissions rp ON rp.role_id=r.id
              WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
                AND rp.permission_code='basket.manage' AND rp.granted=1;";

        public static IList<BasketCustodyRow> CustodyBalances(string databasePath)
        {
            var rows = new List<BasketCustodyRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlCustody, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new BasketCustodyRow {
                        PartyAccountId = reader.GetString(0), PartyName = reader.GetString(1),
                        CustodyBalance = reader.GetInt64(2), CustodyInTotal = reader.GetInt64(3),
                        CustodyOutTotal = reader.GetInt64(4)
                    });
            return rows;
        }

        public static IList<BasketMovementRow> Movements(string databasePath)
        {
            var rows = new List<BasketMovementRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlMovements, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new BasketMovementRow {
                        DocumentNumber = reader.GetString(0), DateIso = reader.GetString(1),
                        MovementType = reader.GetString(2), PackagingName = reader.GetString(3),
                        Quantity = reader.GetInt32(4), FromParty = reader.GetString(5),
                        ToParty = reader.GetString(6), Status = StatusLabel(reader.GetString(7))
                    });
            return rows;
        }

        public static string SaveDraft(string databasePath, BasketMovementDraft draft, string userId)
        {
            if (draft == null) throw new ArgumentNullException("draft");
            if (String.IsNullOrWhiteSpace(draft.PackagingTypeId))
                throw new ArgumentException("نوع سبد الزامی است.");
            if (!Array.Exists(AllowedTypes, t => String.Equals(t, draft.MovementType, StringComparison.Ordinal)))
                throw new ArgumentException("نوع عملیات سبد نامعتبر است.");
            if (draft.Quantity <= 0) throw new ArgumentException("تعداد سبد باید بیشتر از صفر باشد.");
            if (String.IsNullOrWhiteSpace(draft.ToPartyAccountId) && String.IsNullOrWhiteSpace(draft.FromPartyAccountId))
                throw new ArgumentException("حداقل یک طرف (مبدأ یا مقصد) برای گردش سبد الزامی است.");
            if (draft.MovementType == "CUSTODY_IN" && String.IsNullOrWhiteSpace(draft.ToPartyAccountId))
                throw new ArgumentException("تحویل امانی به طرف مقصد متصل می‌شود؛ طرف مقصد الزامی است.");
            if (draft.MovementType == "CUSTODY_OUT" && String.IsNullOrWhiteSpace(draft.FromPartyAccountId))
                throw new ArgumentException("برگشت امانی از طرف مبدأ خارج می‌شود؛ طرف مبدأ الزامی است.");
            if (draft.UnitPriceIrr < 0) throw new ArgumentException("قیمت واحد نمی‌تواند منفی باشد.");

            var documentId = Guid.NewGuid().ToString("N");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                RequirePermission(connection, transaction, userId);

                if (draft.MovementType == "RETURN" && !String.IsNullOrWhiteSpace(draft.FromPartyAccountId))
                {
                    var confirmed = ConfirmedCustody(connection, transaction, draft.FromPartyAccountId);
                    if (draft.Quantity > confirmed)
                        throw new InvalidOperationException(
                            "تعداد برگشت از مانده امانی تأییدشده این طرف بیشتر است.");
                }

                var fiscalYear = FiscalYearService.Resolve(databasePath,
                    DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                var reference = "BASKET-" + documentId.Substring(0, 8);

                using (var command = new SQLiteCommand(SqlDocumentInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", documentId);
                    command.Parameters.AddWithValue("@fiscal", fiscalYear);
                    command.Parameters.AddWithValue("@date",
                        DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    command.Parameters.AddWithValue("@party",
                        (object)draft.ToPartyAccountId ?? draft.FromPartyAccountId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@reference", reference);
                    command.Parameters.AddWithValue("@notes", (object)draft.Notes ?? DBNull.Value);
                    command.Parameters.AddWithValue("@user", userId);
                    command.ExecuteNonQuery();
                }
                using (var command = new SQLiteCommand(SqlMovementInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@doc", documentId);
                    command.Parameters.AddWithValue("@packaging", draft.PackagingTypeId);
                    command.Parameters.AddWithValue("@type", draft.MovementType);
                    command.Parameters.AddWithValue("@qty", draft.Quantity);
                    command.Parameters.AddWithValue("@fromParty", (object)draft.FromPartyAccountId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@toParty", (object)draft.ToPartyAccountId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@fromWarehouse", (object)draft.FromWarehouseId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@toWarehouse", (object)draft.ToWarehouseId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@price", draft.UnitPriceIrr);
                    command.Parameters.AddWithValue("@amount", draft.UnitPriceIrr * draft.Quantity);
                    command.Parameters.AddWithValue("@notes", (object)draft.Notes ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
                using (var history = new SQLiteCommand(SqlHistoryInsert, connection, transaction))
                {
                    history.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    history.Parameters.AddWithValue("@doc", documentId);
                    history.Parameters.AddWithValue("@user", userId);
                    history.Parameters.AddWithValue("@reason", "ثبت گردش سبد؛ در انتظار تأیید مدیر");
                    history.ExecuteNonQuery();
                }
                using (var audit = new SQLiteCommand(SqlAuditInsert, connection, transaction))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", userId);
                    audit.Parameters.AddWithValue("@action", "CREATE_BASKET_MOVEMENT");
                    audit.Parameters.AddWithValue("@entityType", "DOCUMENT");
                    audit.Parameters.AddWithValue("@entityId", documentId);
                    audit.Parameters.AddWithValue("@after",
                        "{\"movement_type\":\"" + draft.MovementType + "\",\"quantity\":" + draft.Quantity + "}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            return documentId;
        }

        private static long ConfirmedCustody(SQLiteConnection connection, SQLiteTransaction transaction,
            string partyAccountId)
        {
            using (var command = new SQLiteCommand(SqlPartyCustodyBalance, connection, transaction))
            {
                command.Parameters.AddWithValue("@party", partyAccountId);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static void RequirePermission(SQLiteConnection connection,
            SQLiteTransaction transaction, string userId)
        {
            using (var command = new SQLiteCommand(SqlPermissionCheck, connection, transaction))
            {
                command.Parameters.AddWithValue("@user", userId ?? String.Empty);
                if (Convert.ToInt32(command.ExecuteScalar()) != 1)
                    throw new UnauthorizedAccessException(
                        "ثبت گردش سبد فقط برای کاربر دارای مجوز «مدیریت سبد» مجاز است.");
            }
        }

        private static string StatusLabel(string value)
        {
            switch (value)
            {
                case "DRAFT": return "پیش‌نویس";
                case "REVIEW": return "در بررسی";
                case "APPROVED": return "تأییدشده";
                case "VOIDED": return "باطل‌شده";
                default: return value;
            }
        }

        private static SQLiteConnection Open(string databasePath)
        {
            var connection = new SQLiteConnection(
                "Data Source=" + databasePath + ";Version=3;Pooling=True;");
            connection.Open();
            using (var command = new SQLiteCommand(
                "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;", connection))
                command.ExecuteNonQuery();
            return connection;
        }
    }
}
