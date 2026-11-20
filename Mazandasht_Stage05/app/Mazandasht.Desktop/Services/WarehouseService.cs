using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;

namespace Mazandasht.Desktop.Services
{
    public sealed class StockOnHandRow
    {
        public string LotId { get; set; }
        public string LotCode { get; set; }
        public string ProductName { get; set; }
        public string WarehouseName { get; set; }
        public long OnHandG { get; set; }
    }

    public sealed class StockTransferDraft
    {
        public string StockLotId { get; set; }
        public string FromWarehouseId { get; set; }
        public string ToWarehouseId { get; set; }
        public long QuantityGrams { get; set; }
        public string Notes { get; set; }
    }

    /// <summary>
    /// Warehouse on-hand balances and stock transfers (G7).
    ///
    /// Rules honoured (12_Rules_Contract.json, section "inventory"):
    ///  - transfers are a pair: TRANSFER_OUT on the source lot/warehouse and
    ///    TRANSFER_IN on the same lot in the destination warehouse;
    ///  - only posted stock counts as on-hand (the v_stock_on_hand view);
    ///  - a transfer creates a REVIEW document and unposted movements, so stock
    ///    only changes after manager approval.
    /// </summary>
    public static class WarehouseService
    {
        private const string SqlOnHand =
            @"SELECT stock_lot_id,lot_code,product_name,warehouse_name,on_hand_g
              FROM v_stock_on_hand ORDER BY product_name,warehouse_name,lot_code;";
        private const string SqlDocumentInsert =
            @"INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,
                    requires_journal,requires_stock_posting,reference_number,notes,created_by)
              VALUES(@id,'STOCK_TRANSFER',@fiscal,'',@date,'REVIEW',0,1,@reference,@notes,@user);";
        private const string SqlMovementInsert =
            @"INSERT INTO stock_movements(id,movement_group_id,stock_lot_id,warehouse_id,source_document_id,
                    movement_type,quantity_delta_g,is_posted,notes)
              VALUES(@id,@group,@lot,@warehouse,@doc,@type,@qty,0,@notes);";
        private const string SqlHistoryInsert =
            @"INSERT INTO document_status_history(id,document_id,old_status,new_status,changed_by,reason)
              VALUES(@id,@doc,NULL,'REVIEW',@user,@reason);";
        private const string SqlAuditInsert =
            @"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
              VALUES(@id,@actor,@action,@entityType,@entityId,@after,@machine);";
        private const string SqlPermissionCheck =
            @"SELECT COUNT(*) FROM users u
              JOIN roles r ON r.id=u.role_id
              JOIN role_permissions rp ON rp.role_id=r.id
              WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
                AND rp.permission_code='stock.manage' AND rp.granted=1;";

        public static IList<StockOnHandRow> OnHand(string databasePath)
        {
            var rows = new List<StockOnHandRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlOnHand, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new StockOnHandRow {
                        LotId = reader.GetString(0), LotCode = reader.GetString(1),
                        ProductName = reader.GetString(2), WarehouseName = reader.GetString(3),
                        OnHandG = reader.GetInt64(4)
                    });
            return rows;
        }

        /// <summary>
        /// Confirmed on-hand of one lot in one warehouse (posted movements only).
        /// </summary>
        public static long ConfirmedOnHand(string databasePath, string lotId, string warehouseId)
        {
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(
                "SELECT COALESCE(SUM(quantity_delta_g),0) FROM stock_movements "
                + "WHERE stock_lot_id=@lot AND warehouse_id=@warehouse AND is_posted=1;", connection))
            {
                command.Parameters.AddWithValue("@lot", lotId);
                command.Parameters.AddWithValue("@warehouse", warehouseId);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        public static string Transfer(string databasePath, StockTransferDraft draft, string userId)
        {
            if (draft == null) throw new ArgumentNullException("draft");
            if (String.IsNullOrWhiteSpace(draft.StockLotId) || String.IsNullOrWhiteSpace(draft.FromWarehouseId) ||
                String.IsNullOrWhiteSpace(draft.ToWarehouseId))
                throw new ArgumentException("خط، انبار مبدأ و انبار مقصد الزامی است.");
            if (String.Equals(draft.FromWarehouseId, draft.ToWarehouseId))
                throw new ArgumentException("انبار مبدأ و مقصد نمی‌توانند یکی باشند.");
            if (draft.QuantityGrams <= 0) throw new ArgumentException("وزن انتقال باید بیشتر از صفر باشد.");

            var documentId = Guid.NewGuid().ToString("N");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                RequirePermission(connection, transaction, userId);
                var available = ConfirmedOnHand(connection, transaction, draft.StockLotId, draft.FromWarehouseId);
                if (draft.QuantityGrams > available)
                    throw new InvalidOperationException("موجودی تأییدشده این خط در انبار مبدأ کافی نیست.");

                var fiscalYear = FiscalYearService.Resolve(databasePath,
                    DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                var reference = "TRF-" + documentId.Substring(0, 8);

                using (var command = new SQLiteCommand(SqlDocumentInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", documentId);
                    command.Parameters.AddWithValue("@fiscal", fiscalYear);
                    command.Parameters.AddWithValue("@date",
                        DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    command.Parameters.AddWithValue("@reference", reference);
                    command.Parameters.AddWithValue("@notes", (object)draft.Notes ?? DBNull.Value);
                    command.Parameters.AddWithValue("@user", userId);
                    command.ExecuteNonQuery();
                }

                // The pair: out of the source warehouse, into the destination.
                InsertMovement(connection, transaction, documentId, draft.StockLotId,
                    draft.FromWarehouseId, "TRANSFER_OUT", -draft.QuantityGrams, "انتقال خروج در انتظار تأیید");
                InsertMovement(connection, transaction, documentId, draft.StockLotId,
                    draft.ToWarehouseId, "TRANSFER_IN", draft.QuantityGrams, "انتقال ورود در انتظار تأیید");

                using (var history = new SQLiteCommand(SqlHistoryInsert, connection, transaction))
                {
                    history.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    history.Parameters.AddWithValue("@doc", documentId);
                    history.Parameters.AddWithValue("@user", userId);
                    history.Parameters.AddWithValue("@reason", "ثبت انتقال انبار؛ در انتظار تأیید مدیر");
                    history.ExecuteNonQuery();
                }
                using (var audit = new SQLiteCommand(SqlAuditInsert, connection, transaction))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", userId);
                    audit.Parameters.AddWithValue("@action", "CREATE_STOCK_TRANSFER");
                    audit.Parameters.AddWithValue("@entityType", "DOCUMENT");
                    audit.Parameters.AddWithValue("@entityId", documentId);
                    audit.Parameters.AddWithValue("@after",
                        "{\"quantity_g\":" + draft.QuantityGrams + ",\"pair\":\"TRANSFER_OUT/TRANSFER_IN\"}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            return documentId;
        }

        private static void InsertMovement(SQLiteConnection connection, SQLiteTransaction transaction,
            string documentId, string lotId, string warehouseId, string movementType,
            long quantityDeltaG, string notes)
        {
            using (var command = new SQLiteCommand(SqlMovementInsert, connection, transaction))
            {
                command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("@group", documentId);
                command.Parameters.AddWithValue("@lot", lotId);
                command.Parameters.AddWithValue("@warehouse", warehouseId);
                command.Parameters.AddWithValue("@doc", documentId);
                command.Parameters.AddWithValue("@type", movementType);
                command.Parameters.AddWithValue("@qty", quantityDeltaG);
                command.Parameters.AddWithValue("@notes", notes);
                command.ExecuteNonQuery();
            }
        }

        private static long ConfirmedOnHand(SQLiteConnection connection, SQLiteTransaction transaction,
            string lotId, string warehouseId)
        {
            using (var command = new SQLiteCommand(
                "SELECT COALESCE(SUM(quantity_delta_g),0) FROM stock_movements "
                + "WHERE stock_lot_id=@lot AND warehouse_id=@warehouse AND is_posted=1;",
                connection, transaction))
            {
                command.Parameters.AddWithValue("@lot", lotId);
                command.Parameters.AddWithValue("@warehouse", warehouseId);
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
                        "ثبت انتقال انبار فقط برای کاربر دارای مجوز «مدیریت موجودی» مجاز است.");
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
