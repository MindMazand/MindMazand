using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;

namespace Mazandasht.Desktop.Services
{
    public sealed class SortingOutputLine
    {
        public string ProductId { get; set; }
        public string GradeId { get; set; }
        public string PackagingTypeId { get; set; }
        public int PackageCount { get; set; }
        public long QuantityGrams { get; set; }
    }

    public sealed class SortingDraft
    {
        public string WarehouseId { get; set; }
        public string SourceWarehouseId { get; set; }
        public string PartyAccountId { get; set; }
        public long InputWeightGrams { get; set; }
        public long WasteWeightGrams { get; set; }
        public long ProcessLossWeightGrams { get; set; }
        public long VarianceToleranceGrams { get; set; }
        public string Notes { get; set; }
        /// <summary>
        /// Hard rejection limit in grams. The contract defines only MATCHED /
        /// WARNING / REJECTED and no numeric threshold, so the operator decides
        /// when a variance is not merely a warning but a rejected batch.
        /// Defaults to zero (no hard rejection).
        /// </summary>
        public long HardRejectionLimitGrams { get; set; }
        public List<SortingOutputLine> Outputs { get; set; }
    }

    public sealed class SortingBatchRow
    {
        public string DocumentId { get; set; }
        public string DocumentNumber { get; set; }
        public string DateIso { get; set; }
        public string Status { get; set; }
        public long InputWeightG { get; set; }
        public long OutputWeightG { get; set; }
        public long WasteWeightG { get; set; }
        public long ProcessLossWeightG { get; set; }
        public long VarianceWeightG { get; set; }
        public long ToleranceG { get; set; }
        public string ReconciliationStatus { get; set; }
    }

    /// <summary>
    /// Sorting batches (G7).
    ///
    /// Rules honoured (12_Rules_Contract.json, section "sorting"):
    ///  variance_weight_g = input - (output + waste + process_loss)
    ///  MATCHED   when abs(variance) &lt;= tolerance
    ///  WARNING   when abs(variance) &gt;  tolerance (manager approval required)
    ///  REJECTED  when abs(variance) exceeds the hard limit supplied by the caller
    /// The database triggers recompute output/variance/status; this service only
    /// supplies the inputs and the waste/loss adjustments, then reads back the
    /// computed status. A commit creates a REVIEW document and unposted stock
    /// movements, so nothing touches inventory until a manager approves it.
    /// </summary>
    public static class SortingService
    {
        private const string SqlDocumentInsert =
            @"INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,
                    requires_journal,requires_stock_posting,party_account_id,reference_number,notes,created_by)
              VALUES(@id,'SORTING',@fiscal,'',@date,'REVIEW',0,1,@party,@reference,@notes,@user);";
        private const string SqlBatchInsert =
            @"INSERT INTO sorting_batches(document_id,input_weight_g,output_weight_g,waste_weight_g,
                    process_loss_weight_g,variance_weight_g,variance_tolerance_g,reconciliation_status,notes)
              VALUES(@doc,@input,0,@waste,@loss,@variance,@tolerance,'PENDING',@notes);";
        private const string SqlInputInsert =
            @"INSERT INTO sorting_batch_inputs(id,sorting_document_id,stock_lot_id,quantity_g)
              VALUES(@id,@doc,@lot,@qty);";
        private const string SqlOutputInsert =
            @"INSERT INTO sorting_outputs(id,sorting_document_id,product_id,grade_id,packaging_type_id,
                    stock_lot_id,quantity_g,package_count,line_number)
              VALUES(@id,@doc,@product,@grade,@packaging,@lot,@qty,@count,@line);";
        private const string SqlLotInsert =
            @"INSERT INTO stock_lots(id,lot_code,product_id,grade_id,packaging_type_id,origin_document_id,
                    initial_quantity_g,notes) VALUES(@id,@code,@product,@grade,@packaging,@doc,@qty,@notes);";
        private const string SqlMovementInsert =
            @"INSERT INTO stock_movements(id,movement_group_id,stock_lot_id,warehouse_id,source_document_id,
                    movement_type,quantity_delta_g,is_posted,notes)
              VALUES(@id,@group,@lot,@warehouse,@doc,@type,@qty,0,@notes);";
        private const string SqlAllocationInsert =
            @"INSERT INTO stock_allocations(id,document_id,stock_lot_id,warehouse_id,quantity_g,allocation_kind)
              VALUES(@id,@doc,@lot,@warehouse,@qty,'SORTING_INPUT');";
        private const string SqlHistoryInsert =
            @"INSERT INTO document_status_history(id,document_id,old_status,new_status,changed_by,reason)
              VALUES(@id,@doc,NULL,'REVIEW',@user,@reason);";
        private const string SqlAuditInsert =
            @"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
              VALUES(@id,@actor,@action,@entityType,@entityId,@after,@machine);";
        private const string SqlList =
            @"SELECT d.id,d.document_number,d.date_iso,d.status,b.input_weight_g,b.output_weight_g,
                     b.waste_weight_g,b.process_loss_weight_g,b.variance_weight_g,
                     b.variance_tolerance_g,b.reconciliation_status
              FROM sorting_batches b JOIN documents d ON d.id=b.document_id
              ORDER BY d.created_at_utc DESC LIMIT 300;";
        private const string SqlPermissionCheck =
            @"SELECT COUNT(*) FROM users u
              JOIN roles r ON r.id=u.role_id
              JOIN role_permissions rp ON rp.role_id=r.id
              WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
                AND rp.permission_code='sorting.manage' AND rp.granted=1;";

        public static IList<SortingBatchRow> List(string databasePath)
        {
            var rows = new List<SortingBatchRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new SortingBatchRow {
                        DocumentId = reader.GetString(0), DocumentNumber = reader.GetString(1),
                        DateIso = reader.GetString(2), Status = reader.GetString(3),
                        InputWeightG = reader.GetInt64(4), OutputWeightG = reader.GetInt64(5),
                        WasteWeightG = reader.GetInt64(6), ProcessLossWeightG = reader.GetInt64(7),
                        VarianceWeightG = reader.GetInt64(8), ToleranceG = reader.GetInt64(9),
                        ReconciliationStatus = reader.GetString(10)
                    });
            return rows;
        }

        /// <summary>
        /// Creates a REVIEW sorting batch. Input is taken from posted stock lots of
        /// the source warehouse in FIFO order; outputs become new lots. No movement
        /// is posted here.
        /// </summary>
        public static string SaveDraft(string databasePath, SortingDraft draft, string userId)
        {
            if (draft == null) throw new ArgumentNullException("draft");
            if (String.IsNullOrWhiteSpace(draft.WarehouseId))
                throw new ArgumentException("انبار مقصد سورتینگ الزامی است.");
            if (draft.InputWeightGrams <= 0) throw new ArgumentException("وزن ورودی باید بیشتر از صفر باشد.");
            if (draft.WasteWeightGrams < 0 || draft.ProcessLossWeightGrams < 0)
                throw new ArgumentException("ضایات و افت فرآیند نمی‌تواند منفی باشد.");
            if (draft.VarianceToleranceGrams < 0) throw new ArgumentException("تلورانس نمی‌تواند منفی باشد.");
            if (draft.Outputs == null || draft.Outputs.Count == 0)
                throw new ArgumentException("حداقل یک خروجی درجه‌بندی الزامی است.");
            long outputTotal = 0;
            foreach (var line in draft.Outputs)
            {
                if (String.IsNullOrWhiteSpace(line.ProductId))
                    throw new ArgumentException("محصول خروجی سورتینگ الزامی است.");
                if (line.QuantityGrams <= 0) throw new ArgumentException("وزن خروجی باید بیشتر از صفر باشد.");
                if (line.PackageCount < 0) throw new ArgumentException("تعداد بسته نمی‌تواند منفی باشد.");
                outputTotal += line.QuantityGrams;
            }
            if (outputTotal + draft.WasteWeightGrams + draft.ProcessLossWeightGrams > draft.InputWeightGrams)
                throw new ArgumentException("مجموع خروجی، ضایات و افت فرآیند از وزن ورودی بیشتر است.");

            var documentId = Guid.NewGuid().ToString("N");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                RequirePermission(connection, transaction, userId);

                // FIFO allocation of the input across posted lots in the source warehouse.
                var remaining = draft.InputWeightGrams;
                var allocations = new List<Tuple<string, long>>();
                using (var command = new SQLiteCommand(
                    "SELECT l.id, SUM(m.quantity_delta_g) AS available_g "
                    + "FROM stock_lots l JOIN stock_movements m ON m.stock_lot_id=l.id "
                    + "WHERE m.warehouse_id=@warehouse AND m.is_posted=1 "
                    + "GROUP BY l.id HAVING SUM(m.quantity_delta_g)>0 "
                    + "ORDER BY l.created_at_utc,l.id;", connection, transaction))
                {
                    command.Parameters.AddWithValue("@warehouse",
                        String.IsNullOrWhiteSpace(draft.SourceWarehouseId)
                            ? (object)draft.WarehouseId : draft.SourceWarehouseId);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read() && remaining > 0)
                        {
                            var available = reader.GetInt64(1);
                            var take = Math.Min(available, remaining);
                            allocations.Add(Tuple.Create(reader.GetString(0), take));
                            remaining -= take;
                        }
                }
                if (remaining > 0)
                    throw new InvalidOperationException("موجودی تأییدشده در انبار برای ورودی سورتینگ کافی نیست.");

                var fiscalYear = FiscalYearService.Resolve(databasePath,
                    DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                var reference = "SORT-" + documentId.Substring(0, 8);

                using (var command = new SQLiteCommand(SqlDocumentInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", documentId);
                    command.Parameters.AddWithValue("@fiscal", fiscalYear);
                    command.Parameters.AddWithValue("@date",
                        DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    command.Parameters.AddWithValue("@party", (object)draft.PartyAccountId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@reference", reference);
                    command.Parameters.AddWithValue("@notes", (object)draft.Notes ?? DBNull.Value);
                    command.Parameters.AddWithValue("@user", userId);
                    command.ExecuteNonQuery();
                }

                var variance = draft.InputWeightGrams - outputTotal
                    - draft.WasteWeightGrams - draft.ProcessLossWeightGrams;
                using (var command = new SQLiteCommand(SqlBatchInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@doc", documentId);
                    command.Parameters.AddWithValue("@input", draft.InputWeightGrams);
                    command.Parameters.AddWithValue("@waste", draft.WasteWeightGrams);
                    command.Parameters.AddWithValue("@loss", draft.ProcessLossWeightGrams);
                    command.Parameters.AddWithValue("@variance", variance);
                    command.Parameters.AddWithValue("@tolerance", draft.VarianceToleranceGrams);
                    command.Parameters.AddWithValue("@notes", (object)draft.Notes ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }

                foreach (var allocation in allocations)
                {
                    using (var input = new SQLiteCommand(SqlInputInsert, connection, transaction))
                    {
                        input.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                        input.Parameters.AddWithValue("@doc", documentId);
                        input.Parameters.AddWithValue("@lot", allocation.Item1);
                        input.Parameters.AddWithValue("@qty", allocation.Item2);
                        input.ExecuteNonQuery();
                    }
                    using (var movement = new SQLiteCommand(SqlMovementInsert, connection, transaction))
                    {
                        movement.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                        movement.Parameters.AddWithValue("@group", documentId);
                        movement.Parameters.AddWithValue("@lot", allocation.Item1);
                        movement.Parameters.AddWithValue("@warehouse",
                            String.IsNullOrWhiteSpace(draft.SourceWarehouseId)
                                ? (object)draft.WarehouseId : draft.SourceWarehouseId);
                        movement.Parameters.AddWithValue("@doc", documentId);
                        movement.Parameters.AddWithValue("@type", "SORTING_INPUT");
                        movement.Parameters.AddWithValue("@qty", -allocation.Item2);
                        movement.Parameters.AddWithValue("@notes", "ورودی سورتینگ در انتظار تأیید");
                        movement.ExecuteNonQuery();
                    }
                    using (var alloc = new SQLiteCommand(SqlAllocationInsert, connection, transaction))
                    {
                        alloc.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                        alloc.Parameters.AddWithValue("@doc", documentId);
                        alloc.Parameters.AddWithValue("@lot", allocation.Item1);
                        alloc.Parameters.AddWithValue("@warehouse",
                            String.IsNullOrWhiteSpace(draft.SourceWarehouseId)
                                ? (object)draft.WarehouseId : draft.SourceWarehouseId);
                        alloc.Parameters.AddWithValue("@qty", allocation.Item2);
                        alloc.ExecuteNonQuery();
                    }
                }

                var lineNumber = 0;
                foreach (var line in draft.Outputs)
                {
                    lineNumber++;
                    var lotId = Guid.NewGuid().ToString("N");
                    using (var lot = new SQLiteCommand(SqlLotInsert, connection, transaction))
                    {
                        lot.Parameters.AddWithValue("@id", lotId);
                        lot.Parameters.AddWithValue("@code", reference + "-O" + lineNumber.ToString("00"));
                        lot.Parameters.AddWithValue("@product", line.ProductId);
                        lot.Parameters.AddWithValue("@grade", (object)line.GradeId ?? DBNull.Value);
                        lot.Parameters.AddWithValue("@packaging", (object)line.PackagingTypeId ?? DBNull.Value);
                        lot.Parameters.AddWithValue("@doc", documentId);
                        lot.Parameters.AddWithValue("@qty", line.QuantityGrams);
                        lot.Parameters.AddWithValue("@notes", "خروجی سورتینگ در انتظار تأیید");
                        lot.ExecuteNonQuery();
                    }
                    using (var output = new SQLiteCommand(SqlOutputInsert, connection, transaction))
                    {
                        output.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                        output.Parameters.AddWithValue("@doc", documentId);
                        output.Parameters.AddWithValue("@product", line.ProductId);
                        output.Parameters.AddWithValue("@grade", (object)line.GradeId ?? DBNull.Value);
                        output.Parameters.AddWithValue("@packaging", (object)line.PackagingTypeId ?? DBNull.Value);
                        output.Parameters.AddWithValue("@lot", lotId);
                        output.Parameters.AddWithValue("@qty", line.QuantityGrams);
                        output.Parameters.AddWithValue("@count", line.PackageCount);
                        output.Parameters.AddWithValue("@line", lineNumber);
                        output.ExecuteNonQuery();
                    }
                    using (var movement = new SQLiteCommand(SqlMovementInsert, connection, transaction))
                    {
                        movement.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                        movement.Parameters.AddWithValue("@group", documentId);
                        movement.Parameters.AddWithValue("@lot", lotId);
                        movement.Parameters.AddWithValue("@warehouse", draft.WarehouseId);
                        movement.Parameters.AddWithValue("@doc", documentId);
                        movement.Parameters.AddWithValue("@type", "SORTING_OUTPUT");
                        movement.Parameters.AddWithValue("@qty", line.QuantityGrams);
                        movement.Parameters.AddWithValue("@notes", "خروجی سورتینگ در انتظار تأیید");
                        movement.ExecuteNonQuery();
                    }
                }

                // Apply the caller's waste/loss adjustment so the recompute trigger runs.
                using (var adjust = new SQLiteCommand(
                    "UPDATE sorting_batches SET waste_weight_g=@waste, process_loss_weight_g=@loss "
                    + "WHERE document_id=@doc;", connection, transaction))
                {
                    adjust.Parameters.AddWithValue("@waste", draft.WasteWeightGrams);
                    adjust.Parameters.AddWithValue("@loss", draft.ProcessLossWeightGrams);
                    adjust.Parameters.AddWithValue("@doc", documentId);
                    adjust.ExecuteNonQuery();
                }

                // Hard-limit rejection is explicit; the contract defines no numeric limit.
                var computedVariance = ReadVariance(connection, transaction, documentId);
                if (draft.HardRejectionLimitGrams > 0 &&
                    Math.Abs((double)computedVariance) > draft.HardRejectionLimitGrams)
                {
                    using (var reject = new SQLiteCommand(
                        "UPDATE sorting_batches SET reconciliation_status='REJECTED' WHERE document_id=@doc;",
                        connection, transaction))
                    {
                        reject.Parameters.AddWithValue("@doc", documentId);
                        reject.ExecuteNonQuery();
                    }
                }

                using (var history = new SQLiteCommand(SqlHistoryInsert, connection, transaction))
                {
                    history.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    history.Parameters.AddWithValue("@doc", documentId);
                    history.Parameters.AddWithValue("@user", userId);
                    history.Parameters.AddWithValue("@reason", "ثبت سورتینگ و تخصیص FIFO ورودی؛ در انتظار تأیید مدیر");
                    history.ExecuteNonQuery();
                }
                using (var audit = new SQLiteCommand(SqlAuditInsert, connection, transaction))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", userId);
                    audit.Parameters.AddWithValue("@action", "CREATE_SORTING");
                    audit.Parameters.AddWithValue("@entityType", "DOCUMENT");
                    audit.Parameters.AddWithValue("@entityId", documentId);
                    audit.Parameters.AddWithValue("@after",
                        "{\"input_weight_g\":" + draft.InputWeightGrams +
                        ",\"output_weight_g\":" + outputTotal +
                        ",\"waste_weight_g\":" + draft.WasteWeightGrams + "}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            return documentId;
        }

        /// <summary>
        /// Recomputes nothing itself: the triggers own the variance formula. This
        /// only surfaces the stored status so the UI can warn about WARNING rows.
        /// </summary>
        public static string ReadReconciliationStatus(string databasePath, string documentId)
        {
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(
                "SELECT reconciliation_status FROM sorting_batches WHERE document_id=@doc;", connection))
            {
                command.Parameters.AddWithValue("@doc", documentId);
                return Convert.ToString(command.ExecuteScalar());
            }
        }

        private static long ReadVariance(SQLiteConnection connection, SQLiteTransaction transaction,
            string documentId)
        {
            using (var command = new SQLiteCommand(
                "SELECT variance_weight_g FROM sorting_batches WHERE document_id=@doc;",
                connection, transaction))
            {
                command.Parameters.AddWithValue("@doc", documentId);
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
                        "ثبت سورتینگ فقط برای کاربر دارای مجوز «مدیریت سورتینگ» مجاز است.");
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
