using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;

namespace Mazandasht.Desktop.Services
{
    public sealed class PurchaseInvoiceLine
    {
        public string ProductId { get; set; }
        public string SourceWeighDocumentId { get; set; }
        public long QuantityGrams { get; set; }
        public long UnitPriceIrrPerKg { get; set; }
        public long DiscountIrr { get; set; }
        public long FreightIrr { get; set; }
        public long CommissionIrr { get; set; }
        public long OtherAdjustmentsIrr { get; set; }
        public string Notes { get; set; }
    }

    public sealed class PurchaseInvoiceDraft
    {
        public string PartyAccountId { get; set; }
        public string DateIso { get; set; }
        public string SourceWeighDocumentId { get; set; }
        public string ReferenceNumber { get; set; }
        public string Notes { get; set; }
        public List<PurchaseInvoiceLine> Lines { get; set; }
    }

    public sealed class PurchaseInvoiceRow
    {
        public string DocumentNumber { get; set; }
        public string DateIso { get; set; }
        public string PartyName { get; set; }
        public long NetAmountIrr { get; set; }
        public string Status { get; set; }
    }

    /// <summary>
    /// Purchase invoices (G8).
    ///
    /// Rules honoured (12_Rules_Contract.json, section "invoices"):
    ///  - net_amount_irr = quantity_g * unit_price_irr_per_kg / 1000
    ///                    - discount_irr + freight_irr + commission_irr
    ///                    + other_adjustments_irr        (half-up rounding)
    ///  - the invoice is linked to its source weighing document
    ///    (linkage = source_weigh_document_id).
    /// The line amount is validated by the database trigger
    /// trg_purchase_line_amount_check, so the service computes it with the exact
    /// same integer expression and never trusts the caller.
    /// A purchase invoice creates a REVIEW document; no journal line is written in
    /// G8 (double-entry posting is G9).
    /// </summary>
    public static class PurchaseService
    {
        private const string SqlDocumentInsert =
            @"INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,
                    requires_journal,requires_stock_posting,party_account_id,reference_number,notes,created_by)
              VALUES(@id,'PURCHASE',@fiscal,'',@date,'REVIEW',1,0,@party,@reference,@notes,@user);";
        private const string SqlLineInsert =
            @"INSERT INTO purchase_invoice_lines(id,document_id,line_number,source_weigh_document_id,
                    product_id,quantity_g,unit_price_irr_per_kg,gross_amount_irr,discount_irr,freight_irr,
                    commission_irr,other_adjustments_irr,net_amount_irr,notes)
              VALUES(@id,@doc,@line,@source,@product,@qty,@price,@gross,@discount,@freight,
                     @commission,@other,@net,@notes);";
        private const string SqlRelationInsert =
            @"INSERT INTO document_relations(id,source_document_id,target_document_id,relation_type,notes)
              VALUES(@id,@source,@target,'INVOICE_FOR',@notes);";
        private const string SqlHistoryInsert =
            @"INSERT INTO document_status_history(id,document_id,old_status,new_status,changed_by,reason)
              VALUES(@id,@doc,NULL,'REVIEW',@user,@reason);";
        private const string SqlAuditInsert =
            @"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
              VALUES(@id,@actor,@action,@entityType,@entityId,@after,@machine);";
        private const string SqlList =
            @"SELECT d.document_number,d.date_iso,COALESCE(p.display_name,'—'),
                     COALESCE(SUM(l.net_amount_irr),0),d.status
              FROM documents d
              LEFT JOIN party_accounts pa ON pa.id=d.party_account_id
              LEFT JOIN parties p ON p.id=pa.party_id
              LEFT JOIN purchase_invoice_lines l ON l.document_id=d.id
              WHERE d.document_type='PURCHASE'
              GROUP BY d.id ORDER BY d.created_at_utc DESC LIMIT 300;";
        private const string SqlPermissionCheck =
            @"SELECT COUNT(*) FROM users u
              JOIN roles r ON r.id=u.role_id
              JOIN role_permissions rp ON rp.role_id=r.id
              WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
                AND rp.permission_code='purchase.manage' AND rp.granted=1;";

        public static IList<PurchaseInvoiceRow> List(string databasePath)
        {
            var rows = new List<PurchaseInvoiceRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new PurchaseInvoiceRow {
                        DocumentNumber = reader.GetString(0), DateIso = reader.GetString(1),
                        PartyName = reader.GetString(2), NetAmountIrr = reader.GetInt64(3),
                        Status = StatusLabel(reader.GetString(4))
                    });
            return rows;
        }

        public static string SaveDraft(string databasePath, PurchaseInvoiceDraft draft, string userId)
        {
            if (draft == null) throw new ArgumentNullException("draft");
            if (String.IsNullOrWhiteSpace(draft.PartyAccountId))
                throw new ArgumentException("انتخاب فروشنده برای فاکتور خرید الزامی است.");
            var dateIso = RequireDate(draft.DateIso);
            if (draft.Lines == null || draft.Lines.Count == 0)
                throw new ArgumentException("حداقل یک خط فاکتور خرید الزامی است.");

            var documentId = Guid.NewGuid().ToString("N");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                RequirePermission(connection, transaction, userId);
                if (!Exists(connection, transaction, "party_accounts", draft.PartyAccountId))
                    throw new InvalidOperationException("طرف حساب فروشنده معتبر نیست.");

                var sourceWeighId = null as string;
                if (!String.IsNullOrWhiteSpace(draft.SourceWeighDocumentId))
                {
                    sourceWeighId = ResolveWeighDocument(connection, transaction, draft.SourceWeighDocumentId);
                }

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
                        throw new ArgumentException("محصول خط " + lineNumber + " فاکتور خرید الزامی است.");
                    if (!Exists(connection, transaction, "products", line.ProductId))
                        throw new InvalidOperationException("محصول خط " + lineNumber + " معتبر نیست.");
                    if (line.QuantityGrams < 0) throw new ArgumentException("وزن خط خرید نمی‌تواند منفی باشد.");
                    if (line.UnitPriceIrrPerKg < 0) throw new ArgumentException("فی خرید نمی‌تواند منفی باشد.");
                    if (line.DiscountIrr < 0 || line.FreightIrr < 0 || line.CommissionIrr < 0)
                        throw new ArgumentException("تخفیف، کرایه و کمیسیون نمی‌توانند منفی باشند.");

                    var gross = HalfUp(line.QuantityGrams * line.UnitPriceIrrPerKg);
                    var net = gross - line.DiscountIrr + line.FreightIrr
                              + line.CommissionIrr + line.OtherAdjustmentsIrr;
                    total += net;

                    using (var command = new SQLiteCommand(SqlLineInsert, connection, transaction))
                    {
                        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                        command.Parameters.AddWithValue("@doc", documentId);
                        command.Parameters.AddWithValue("@line", lineNumber);
                        command.Parameters.AddWithValue("@source",
                            (object)line.SourceWeighDocumentId ?? sourceWeighId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@product", line.ProductId);
                        command.Parameters.AddWithValue("@qty", line.QuantityGrams);
                        command.Parameters.AddWithValue("@price", line.UnitPriceIrrPerKg);
                        command.Parameters.AddWithValue("@gross", gross);
                        command.Parameters.AddWithValue("@discount", line.DiscountIrr);
                        command.Parameters.AddWithValue("@freight", line.FreightIrr);
                        command.Parameters.AddWithValue("@commission", line.CommissionIrr);
                        command.Parameters.AddWithValue("@other", line.OtherAdjustmentsIrr);
                        command.Parameters.AddWithValue("@net", net);
                        command.Parameters.AddWithValue("@notes", (object)line.Notes ?? DBNull.Value);
                        command.ExecuteNonQuery();
                    }
                }

                if (sourceWeighId != null)
                {
                    using (var relation = new SQLiteCommand(SqlRelationInsert, connection, transaction))
                    {
                        relation.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                        relation.Parameters.AddWithValue("@source", sourceWeighId);
                        relation.Parameters.AddWithValue("@target", documentId);
                        relation.Parameters.AddWithValue("@notes", "فاکتور خرید مرتبط با باسکول ورودی");
                        relation.ExecuteNonQuery();
                    }
                }

                using (var history = new SQLiteCommand(SqlHistoryInsert, connection, transaction))
                {
                    history.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    history.Parameters.AddWithValue("@doc", documentId);
                    history.Parameters.AddWithValue("@user", userId);
                    history.Parameters.AddWithValue("@reason", "ثبت فاکتور خرید؛ در انتظار تأیید مدیر");
                    history.ExecuteNonQuery();
                }
                using (var audit = new SQLiteCommand(SqlAuditInsert, connection, transaction))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", userId);
                    audit.Parameters.AddWithValue("@action", "CREATE_PURCHASE_INVOICE");
                    audit.Parameters.AddWithValue("@entityType", "DOCUMENT");
                    audit.Parameters.AddWithValue("@entityId", documentId);
                    audit.Parameters.AddWithValue("@after",
                        "{\"lines\":" + draft.Lines.Count + ",\"net_amount_irr\":" + total + "}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                JournalService.EnsureEntry(connection, transaction, documentId, userId);
                transaction.Commit();
            }
            return documentId;
        }

        /// <summary>
        /// Half-up rounding of grams × price-per-kg into Rial, matching the
        /// database trigger expression ((quantity_g * unit_price + 500) / 1000).
        /// </summary>
        public static long HalfUp(long value)
        {
            return (value + 500) / 1000;
        }

        internal static string ResolveWeighDocument(SQLiteConnection connection,
            SQLiteTransaction transaction, string documentId)
        {
            using (var command = new SQLiteCommand(
                "SELECT id FROM documents WHERE id=@id AND document_type='WEIGHING';",
                connection, transaction))
            {
                command.Parameters.AddWithValue("@id", documentId);
                var value = Convert.ToString(command.ExecuteScalar());
                if (value == null)
                    throw new InvalidOperationException("سند باسکول ورودی برای این فاکتور پیدا نشد.");
                return value;
            }
        }

        internal static bool Exists(SQLiteConnection connection, SQLiteTransaction transaction,
            string table, string id)
        {
            using (var command = new SQLiteCommand(
                "SELECT COUNT(*) FROM " + table + " WHERE id=@id;", connection, transaction))
            {
                command.Parameters.AddWithValue("@id", id);
                return Convert.ToInt32(command.ExecuteScalar()) == 1;
            }
        }

        internal static string RequireDate(string dateIso)
        {
            if (String.IsNullOrWhiteSpace(dateIso)) throw new ArgumentException("تاریخ فاکتور الزامی است.");
            DateTime value;
            if (!DateTime.TryParseExact(dateIso.Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
                throw new ArgumentException("تاریخ باید به‌صورت yyyy-MM-dd باشد.");
            return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        internal static string StatusLabel(string value)
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

        internal static void RequirePermission(SQLiteConnection connection,
            SQLiteTransaction transaction, string userId, string permissionCode)
        {
            using (var command = new SQLiteCommand(
                "SELECT COUNT(*) FROM users u JOIN roles r ON r.id=u.role_id "
                + "JOIN role_permissions rp ON rp.role_id=r.id "
                + "WHERE u.id=@user AND u.is_active=1 AND r.is_active=1 "
                + "AND rp.permission_code=@permission AND rp.granted=1;", connection, transaction))
            {
                command.Parameters.AddWithValue("@user", userId ?? String.Empty);
                command.Parameters.AddWithValue("@permission", permissionCode);
                if (Convert.ToInt32(command.ExecuteScalar()) != 1)
                    throw new UnauthorizedAccessException(
                        "این عملیات فقط برای کاربر دارای مجوز مربوطه مجاز است.");
            }
        }

        internal static SQLiteConnection Open(string databasePath)
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
