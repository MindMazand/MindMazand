using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;

namespace Mazandasht.Desktop.Services
{
    public sealed class SalesInvoiceLine
    {
        public string ProductId { get; set; }
        public string GradeId { get; set; }
        public string PackagingTypeId { get; set; }
        public string SourceWeighDocumentId { get; set; }
        public string SourceSortingOutputId { get; set; }
        public long QuantityGrams { get; set; }
        public long UnitPriceIrrPerKg { get; set; }
        public long DiscountIrr { get; set; }
        public long FreightIrr { get; set; }
        public long CommissionIrr { get; set; }
        public long OtherCostsIrr { get; set; }
        public string Notes { get; set; }
    }

    public sealed class SalesInvoiceDraft
    {
        public string PartyAccountId { get; set; }
        public string DateIso { get; set; }
        public string ReferenceNumber { get; set; }
        public string Notes { get; set; }
        public List<SalesInvoiceLine> Lines { get; set; }
    }

    public sealed class SalesInvoiceRow
    {
        public string DocumentNumber { get; set; }
        public string DateIso { get; set; }
        public string PartyName { get; set; }
        public long NetReceivableIrr { get; set; }
        public string Status { get; set; }
    }

    public sealed class SalesAllocationRow
    {
        public string LotCode { get; set; }
        public string WarehouseName { get; set; }
        public long AllocatedGrams { get; set; }
    }

    /// <summary>
    /// Sales invoices (G8).
    ///
    /// Rules honoured (12_Rules_Contract.json, section "invoices"):
    ///  - net_receivable_irr = quantity_g * unit_price_irr_per_kg / 1000
    ///                         - discount_irr + freight_irr + commission_irr
    ///                         + other_costs_irr      (half-up rounding)
    ///  - linkage = source_weigh_document_id / source_sorting_output_id
    ///  - allocationRequired = true, oversellAllowed = false.
    /// Allocation is FIFO over posted lots of the same product; a line can never
    /// reserve more than the confirmed on-hand, and every line must be fully
    /// allocated. Allocations are reservations only: no stock movement is posted
    /// here (posting lands in G9 with the double-entry engine).
    /// </summary>
    public static class SalesService
    {
        private const string SqlDocumentInsert =
            @"INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,
                    requires_journal,requires_stock_posting,party_account_id,reference_number,notes,created_by)
              VALUES(@id,'SALE',@fiscal,'',@date,'REVIEW',1,0,@party,@reference,@notes,@user);";
        private const string SqlDocumentLineInsert =
            @"INSERT INTO document_lines(id,document_id,line_number,product_id,grade_id,packaging_type_id,
                    quantity_g,amount_irr,description)
              VALUES(@id,@doc,@line,@product,@grade,@packaging,@qty,@amount,@description);";
        private const string SqlLineInsert =
            @"INSERT INTO sales_invoice_lines(id,document_id,line_number,source_weigh_document_id,
                    source_sorting_output_id,product_id,grade_id,packaging_type_id,quantity_g,
                    unit_price_irr_per_kg,gross_amount_irr,discount_irr,freight_irr,commission_irr,
                    other_costs_irr,net_receivable_irr,notes)
              VALUES(@id,@doc,@line,@weigh,@sorting,@product,@grade,@packaging,@qty,@price,
                     @gross,@discount,@freight,@commission,@other,@net,@notes);";
        private const string SqlRelationInsert =
            @"INSERT INTO document_relations(id,source_document_id,target_document_id,relation_type,notes)
              VALUES(@id,@source,@target,@relation,@notes);";
        private const string SqlAllocationInsert =
            @"INSERT INTO stock_allocations(id,document_id,document_line_id,stock_lot_id,warehouse_id,
                    quantity_g,allocation_kind)
              VALUES(@id,@doc,@line,@lot,@warehouse,@qty,'SALE');";
        private const string SqlHistoryInsert =
            @"INSERT INTO document_status_history(id,document_id,old_status,new_status,changed_by,reason)
              VALUES(@id,@doc,NULL,'REVIEW',@user,@reason);";
        private const string SqlAuditInsert =
            @"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
              VALUES(@id,@actor,@action,@entityType,@entityId,@after,@machine);";
        private const string SqlAvailable =
            @"SELECT l.id, m.warehouse_id, SUM(m.quantity_delta_g) AS available_g
              FROM stock_lots l JOIN stock_movements m ON m.stock_lot_id=l.id
              WHERE m.is_posted=1 AND l.product_id=@product
              GROUP BY l.id, m.warehouse_id
              HAVING SUM(m.quantity_delta_g) > 0
              ORDER BY l.created_at_utc, l.id;";
        private const string SqlReserved =
            @"SELECT COALESCE(SUM(quantity_g),0) FROM stock_allocations
              WHERE stock_lot_id=@lot AND allocation_kind='SALE';";
        private const string SqlList =
            @"SELECT d.document_number,d.date_iso,COALESCE(p.display_name,'—'),
                     COALESCE(SUM(l.net_receivable_irr),0),d.status
              FROM documents d
              LEFT JOIN party_accounts pa ON pa.id=d.party_account_id
              LEFT JOIN parties p ON p.id=pa.party_id
              LEFT JOIN sales_invoice_lines l ON l.document_id=d.id
              WHERE d.document_type='SALE'
              GROUP BY d.id ORDER BY d.created_at_utc DESC LIMIT 300;";
        private const string SqlLotInfo =
            @"SELECT sl.lot_code, COALESCE(w.display_name,'—') FROM stock_lots sl
              LEFT JOIN warehouses w ON w.id=@warehouse WHERE sl.id=@lot;";
        private const string SqlPermissionCheck =
            @"SELECT COUNT(*) FROM users u
              JOIN roles r ON r.id=u.role_id
              JOIN role_permissions rp ON rp.role_id=r.id
              WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
                AND rp.permission_code='sale.manage' AND rp.granted=1;";

        public static IList<SalesInvoiceRow> List(string databasePath)
        {
            var rows = new List<SalesInvoiceRow>();
            using (var connection = PurchaseService.Open(databasePath))
            using (var command = new SQLiteCommand(SqlList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new SalesInvoiceRow {
                        DocumentNumber = reader.GetString(0), DateIso = reader.GetString(1),
                        PartyName = reader.GetString(2), NetReceivableIrr = reader.GetInt64(3),
                        Status = PurchaseService.StatusLabel(reader.GetString(4))
                    });
            return rows;
        }

        /// <summary>
        /// FIFO allocation preview for one product: confirmed on-hand minus what is
        /// already reserved for other sales documents.
        /// </summary>
        public static IList<SalesAllocationRow> AvailableLots(string databasePath, string productId)
        {
            var rows = new List<SalesAllocationRow>();
            using (var connection = PurchaseService.Open(databasePath))
            {
                // Materialise the FIFO lots first: a nested command cannot run on the
                // same connection while this reader is still open.
                var lots = new List<Tuple<string, string, long>>();
                using (var command = new SQLiteCommand(SqlAvailable, connection))
                {
                    command.Parameters.AddWithValue("@product", productId);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            lots.Add(Tuple.Create(reader.GetString(0), reader.GetString(1),
                                                  reader.GetInt64(2)));
                }

                foreach (var lot in lots)
                {
                    long reserved;
                    using (var reserve = new SQLiteCommand(SqlReserved, connection))
                    {
                        reserve.Parameters.AddWithValue("@lot", lot.Item1);
                        reserved = Convert.ToInt64(reserve.ExecuteScalar());
                    }
                    var free = lot.Item3 - reserved;
                    if (free <= 0) continue;

                    var lotCode = lot.Item1;
                    var warehouseName = "—";
                    using (var info = new SQLiteCommand(SqlLotInfo, connection))
                    {
                        info.Parameters.AddWithValue("@lot", lot.Item1);
                        info.Parameters.AddWithValue("@warehouse", lot.Item2);
                        using (var reader = info.ExecuteReader())
                            if (reader.Read())
                            {
                                lotCode = reader.GetString(0);
                                warehouseName = reader.GetString(1);
                            }
                    }
                    rows.Add(new SalesAllocationRow {
                        LotCode = lotCode, WarehouseName = warehouseName, AllocatedGrams = free
                    });
                }
            }
            return rows;
        }

        public static string SaveDraft(string databasePath, SalesInvoiceDraft draft, string userId)
        {
            if (draft == null) throw new ArgumentNullException("draft");
            if (String.IsNullOrWhiteSpace(draft.PartyAccountId))
                throw new ArgumentException("انتخاب خریدار برای فاکتور فروش الزامی است.");
            var dateIso = PurchaseService.RequireDate(draft.DateIso);
            if (draft.Lines == null || draft.Lines.Count == 0)
                throw new ArgumentException("حداقل یک خط فاکتور فروش الزامی است.");

            var documentId = Guid.NewGuid().ToString("N");
            using (var connection = PurchaseService.Open(databasePath))
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                PurchaseService.RequirePermission(connection, transaction, userId, "sale.manage");
                if (!PurchaseService.Exists(connection, transaction, "party_accounts", draft.PartyAccountId))
                    throw new InvalidOperationException("طرف حساب خریدار معتبر نیست.");

                var fiscalYear = FiscalYearService.Resolve(databasePath, dateIso);
                using (var command = new SQLiteCommand(SqlDocumentInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", documentId);
                    command.Parameters.AddWithValue("@fiscal", fiscalYear);
                    command.Parameters.AddWithValue("@date", dateIso);
                    command.Parameters.AddWithValue("@party", draft.PartyAccountId);
                    command.Parameters.AddWithValue("@reference",
                        (object)draft.ReferenceNumber ?? DBNull.Value);
                    command.Parameters.AddWithValue("@notes", (object)draft.Notes ?? DBNull.Value);
                    command.Parameters.AddWithValue("@user", userId);
                    command.ExecuteNonQuery();
                }

                var lineNumber = 0;
                long total = 0;
                foreach (var line in draft.Lines)
                {
                    lineNumber++;
                    if (String.IsNullOrWhiteSpace(line.ProductId))
                        throw new ArgumentException("محصول خط " + lineNumber + " فاکتور فروش الزامی است.");
                    if (!PurchaseService.Exists(connection, transaction, "products", line.ProductId))
                        throw new InvalidOperationException("محصول خط " + lineNumber + " معتبر نیست.");
                    if (line.QuantityGrams <= 0) throw new ArgumentException("وزن خط فروش باید بیشتر از صفر باشد.");
                    if (line.UnitPriceIrrPerKg < 0) throw new ArgumentException("فی فروش نمی‌تواند منفی باشد.");
                    if (line.DiscountIrr < 0 || line.FreightIrr < 0 || line.CommissionIrr < 0
                        || line.OtherCostsIrr < 0)
                        throw new ArgumentException("تخفیف، کرایه، کمیسیون و سایر هزینه‌ها نمی‌توانند منفی باشند.");

                    var gross = PurchaseService.HalfUp(line.QuantityGrams * line.UnitPriceIrrPerKg);
                    var net = gross - line.DiscountIrr + line.FreightIrr
                              + line.CommissionIrr + line.OtherCostsIrr;
                    if (net < 0) throw new ArgumentException("صافی خط فروش نمی‌تواند منفی باشد.");
                    total += net;

                    var genericLineId = Guid.NewGuid().ToString("N");
                    using (var command = new SQLiteCommand(SqlDocumentLineInsert, connection, transaction))
                    {
                        command.Parameters.AddWithValue("@id", genericLineId);
                        command.Parameters.AddWithValue("@doc", documentId);
                        command.Parameters.AddWithValue("@line", lineNumber);
                        command.Parameters.AddWithValue("@product", line.ProductId);
                        command.Parameters.AddWithValue("@grade", (object)line.GradeId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@packaging", (object)line.PackagingTypeId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@qty", line.QuantityGrams);
                        command.Parameters.AddWithValue("@amount", net);
                        command.Parameters.AddWithValue("@description",
                            (object)line.Notes ?? DBNull.Value);
                        command.ExecuteNonQuery();
                    }

                    var weighId = line.SourceWeighDocumentId == null
                        ? null as string
                        : PurchaseService.ResolveWeighDocument(connection, transaction, line.SourceWeighDocumentId);

                    string sortingId = null;
                    if (!String.IsNullOrWhiteSpace(line.SourceSortingOutputId))
                    {
                        using (var command = new SQLiteCommand(
                            "SELECT id FROM sorting_outputs WHERE id=@id;", connection, transaction))
                        {
                            command.Parameters.AddWithValue("@id", line.SourceSortingOutputId);
                            sortingId = Convert.ToString(command.ExecuteScalar());
                        }
                        if (sortingId == null)
                            throw new InvalidOperationException("خروجی سورت خط " + lineNumber + " معتبر نیست.");
                    }

                    using (var command = new SQLiteCommand(SqlLineInsert, connection, transaction))
                    {
                        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                        command.Parameters.AddWithValue("@doc", documentId);
                        command.Parameters.AddWithValue("@line", lineNumber);
                        command.Parameters.AddWithValue("@weigh", (object)weighId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@sorting", (object)sortingId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@product", line.ProductId);
                        command.Parameters.AddWithValue("@grade", (object)line.GradeId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@packaging", (object)line.PackagingTypeId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@qty", line.QuantityGrams);
                        command.Parameters.AddWithValue("@price", line.UnitPriceIrrPerKg);
                        command.Parameters.AddWithValue("@gross", gross);
                        command.Parameters.AddWithValue("@discount", line.DiscountIrr);
                        command.Parameters.AddWithValue("@freight", line.FreightIrr);
                        command.Parameters.AddWithValue("@commission", line.CommissionIrr);
                        command.Parameters.AddWithValue("@other", line.OtherCostsIrr);
                        command.Parameters.AddWithValue("@net", net);
                        command.Parameters.AddWithValue("@notes", (object)line.Notes ?? DBNull.Value);
                        command.ExecuteNonQuery();
                    }

                    if (weighId != null)
                        AddRelation(connection, transaction, weighId, documentId, "INVOICE_FOR", "فاکتور فروش مرتبط با باسکول");
                    if (sortingId != null)
                        AddRelation(connection, transaction, sortingId, documentId, "SOURCE_OF", "فاکتور فروش مرتبط با خروجی سورت");

                    Allocate(connection, transaction, documentId, genericLineId, line);
                }

                using (var history = new SQLiteCommand(SqlHistoryInsert, connection, transaction))
                {
                    history.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    history.Parameters.AddWithValue("@doc", documentId);
                    history.Parameters.AddWithValue("@user", userId);
                    history.Parameters.AddWithValue("@reason", "ثبت فاکتور فروش و تخصیص FIFO موجودی؛ در انتظار تأیید مدیر");
                    history.ExecuteNonQuery();
                }
                using (var audit = new SQLiteCommand(SqlAuditInsert, connection, transaction))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", userId);
                    audit.Parameters.AddWithValue("@action", "CREATE_SALES_INVOICE");
                    audit.Parameters.AddWithValue("@entityType", "DOCUMENT");
                    audit.Parameters.AddWithValue("@entityId", documentId);
                    audit.Parameters.AddWithValue("@after",
                        "{\"lines\":" + draft.Lines.Count + ",\"net_receivable_irr\":" + total + "}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                JournalService.EnsureEntry(connection, transaction, documentId, userId);
                transaction.Commit();
            }
            return documentId;
        }

        /// <summary>
        /// FIFO reservation of posted lots for one sales line. Refuses oversell.
        /// </summary>
        private static void Allocate(SQLiteConnection connection, SQLiteTransaction transaction,
            string documentId, string genericLineId, SalesInvoiceLine line)
        {
            var remaining = line.QuantityGrams;
            // Materialise the FIFO lots first: a nested command cannot run on the same
            // connection while this reader is still open.
            var availableLots = new List<Tuple<string, string, long>>();
            using (var command = new SQLiteCommand(SqlAvailable, connection, transaction))
            {
                command.Parameters.AddWithValue("@product", line.ProductId);
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        availableLots.Add(Tuple.Create(reader.GetString(0), reader.GetString(1),
                                                       reader.GetInt64(2)));
            }

            var reservations = new List<Tuple<string, string, long>>();
            foreach (var lot in availableLots)
            {
                if (remaining <= 0) break;
                long reserved;
                using (var reserve = new SQLiteCommand(SqlReserved, connection, transaction))
                {
                    reserve.Parameters.AddWithValue("@lot", lot.Item1);
                    reserved = Convert.ToInt64(reserve.ExecuteScalar());
                }
                var free = lot.Item3 - reserved;
                if (free <= 0) continue;
                var take = Math.Min(free, remaining);
                reservations.Add(Tuple.Create(lot.Item1, lot.Item2, take));
                remaining -= take;
            }
            if (remaining > 0)
                throw new InvalidOperationException(
                    "موجودی تأییدشده برای فروش کافی نیست؛ فروش بیش از موجودی مجاز نیست.");

            foreach (var lot in reservations)
            {
                using (var allocation = new SQLiteCommand(SqlAllocationInsert, connection, transaction))
                {
                    allocation.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    allocation.Parameters.AddWithValue("@doc", documentId);
                    allocation.Parameters.AddWithValue("@line", genericLineId);
                    allocation.Parameters.AddWithValue("@lot", lot.Item1);
                    allocation.Parameters.AddWithValue("@warehouse", lot.Item2);
                    allocation.Parameters.AddWithValue("@qty", lot.Item3);
                    allocation.ExecuteNonQuery();
                }
            }
        }

        private static void AddRelation(SQLiteConnection connection, SQLiteTransaction transaction,
            string sourceId, string targetId, string relationType, string notes)
        {
            using (var relation = new SQLiteCommand(SqlRelationInsert, connection, transaction))
            {
                relation.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                relation.Parameters.AddWithValue("@source", sourceId);
                relation.Parameters.AddWithValue("@target", targetId);
                relation.Parameters.AddWithValue("@relation", relationType);
                relation.Parameters.AddWithValue("@notes", notes);
                relation.ExecuteNonQuery();
            }
        }
    }
}
