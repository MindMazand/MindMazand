using System;
using System.Collections.Generic;
using System.Data.SQLite;

namespace Mazandasht.Desktop.Services
{
    public sealed class ChequeDraft
    {
        public string BankAccountCode { get; set; }
        public string Direction { get; set; }
        public string PartyAccountId { get; set; }
        public string ChequeNumber { get; set; }
        public string SaiadyId { get; set; }
        public string IssueDateIso { get; set; }
        public string DueDateIso { get; set; }
        public long AmountIrr { get; set; }
        public string ChequeBookId { get; set; }
        public string Notes { get; set; }
    }

    public sealed class ChequeRow
    {
        public string Id { get; set; }
        public string DocumentNumber { get; set; }
        public string Direction { get; set; }
        public string ChequeNumber { get; set; }
        public string BankTitle { get; set; }
        public string BeneficiaryName { get; set; }
        public string DueDateIso { get; set; }
        public long AmountIrr { get; set; }
        public string Status { get; set; }
    }

    /// <summary>
    /// Cheques (G8).
    ///
    /// Rules honoured (12_Rules_Contract.json, section "cheque"):
    ///  - states ISSUED / CIRCULATING / CLEARED / RETURNED / CANCELLED;
    ///  - unique key = (bank_account_id, cheque_number);
    ///  - clearanceCreatesBankTransaction = true: clearing a cheque creates a
    ///    BANK_TXN document with its payment_transactions row, so the bank balance
    ///    moves exactly once.
    /// A new cheque is created on a REVIEW document and is not cleared yet.
    /// </summary>
    public static class ChequeService
    {
        private const string SqlDocumentInsert =
            @"INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,
                    requires_journal,requires_stock_posting,party_account_id,reference_number,notes,created_by)
              VALUES(@id,'CHEQUE',@fiscal,'',@date,'REVIEW',1,0,@party,@reference,@notes,@user);";
        private const string SqlChequeInsert =
            @"INSERT INTO cheques(id,document_id,cheque_number,bank_account_id,direction,issue_date_iso,
                    due_date_iso,amount_irr,beneficiary_account_id,status,cheque_book_id,notes)
              VALUES(@id,@doc,@number,@bank,@direction,@issue,@due,@amount,@beneficiary,'ISSUED',
                     @book,@notes);";
        private const string SqlEventInsert =
            @"INSERT INTO cheque_events(id,cheque_id,event_type,event_date_iso,amount_irr,
                    related_document_id,notes,created_by)
              VALUES(@id,@cheque,@type,@date,@amount,@related,@notes,@user);";
        private const string SqlStatusUpdate =
            @"UPDATE cheques SET status=@status WHERE id=@cheque;";
        private const string SqlBankTxnDocument =
            @"INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,
                    requires_journal,requires_stock_posting,party_account_id,reference_number,notes,created_by)
              VALUES(@id,'BANK_TXN',@fiscal,'',@date,'REVIEW',0,0,@party,@reference,@notes,@user);";
        private const string SqlBankPaymentInsert =
            @"INSERT INTO payment_transactions(document_id,bank_account_id,party_account_id,direction,
                    amount_irr,cheque_id,notes)
              VALUES(@doc,@bank,@party,@direction,@amount,@cheque,@notes);";
        private const string SqlHistoryInsert =
            @"INSERT INTO document_status_history(id,document_id,old_status,new_status,changed_by,reason)
              VALUES(@id,@doc,NULL,'REVIEW',@user,@reason);";
        private const string SqlAuditInsert =
            @"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
              VALUES(@id,@actor,@action,@entityType,@entityId,@after,@machine);";
        private const string SqlList =
            @"SELECT c.id,d.document_number,c.direction,c.cheque_number,COALESCE(ba.display_title,'—'),
                     COALESCE(p.display_name,'—'),c.due_date_iso,c.amount_irr,c.status
              FROM cheques c
              JOIN documents d ON d.id=c.document_id
              LEFT JOIN bank_accounts ba ON ba.id=c.bank_account_id
              LEFT JOIN party_accounts pa ON pa.id=c.beneficiary_account_id
              LEFT JOIN parties p ON p.id=pa.party_id
              ORDER BY d.created_at_utc DESC LIMIT 300;";
        private const string SqlResolveBank =
            @"SELECT ba.id FROM bank_accounts ba
              LEFT JOIN ledger_accounts la ON la.id=ba.ledger_account_id
              WHERE ba.id=@code OR la.account_code=@code OR ba.display_title=@code LIMIT 1;";
        private const string SqlPermissionCheck =
            @"SELECT COUNT(*) FROM users u
              JOIN roles r ON r.id=u.role_id
              JOIN role_permissions rp ON rp.role_id=r.id
              WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
                AND rp.permission_code='cheque.manage' AND rp.granted=1;";

        public static IList<ChequeRow> List(string databasePath)
        {
            var rows = new List<ChequeRow>();
            using (var connection = PurchaseService.Open(databasePath))
            using (var command = new SQLiteCommand(SqlList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new ChequeRow {
                        Id = reader.GetString(0), DocumentNumber = reader.GetString(1),
                        Direction = reader.GetString(2), ChequeNumber = reader.GetString(3),
                        BankTitle = reader.GetString(4), BeneficiaryName = reader.GetString(5),
                        DueDateIso = reader.GetString(6), AmountIrr = reader.GetInt64(7),
                        Status = StatusLabel(reader.GetString(8))
                    });
            return rows;
        }

        public static string SaveDraft(string databasePath, ChequeDraft draft, string userId)
        {
            if (draft == null) throw new ArgumentNullException("draft");
            if (draft.Direction != "IN" && draft.Direction != "OUT")
                throw new ArgumentException("جهت چک باید IN یا OUT باشد.");
            if (String.IsNullOrWhiteSpace(draft.ChequeNumber))
                throw new ArgumentException("شماره چک الزامی است.");
            if (draft.AmountIrr <= 0) throw new ArgumentException("مبلغ چک باید بیشتر از صفر باشد.");
            var issueDate = PurchaseService.RequireDate(draft.IssueDateIso);
            var dueDate = PurchaseService.RequireDate(draft.DueDateIso);
            if (String.CompareOrdinal(dueDate, issueDate) < 0)
                throw new ArgumentException("تاریخ سررسید نمی‌تواند قبل از تاریخ صدور باشد.");

            var documentId = Guid.NewGuid().ToString("N");
            var chequeId = Guid.NewGuid().ToString("N");
            using (var connection = PurchaseService.Open(databasePath))
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                PurchaseService.RequirePermission(connection, transaction, userId, "cheque.manage");

                string bankId = null;
                if (!String.IsNullOrWhiteSpace(draft.BankAccountCode))
                {
                    bankId = BankService.ResolveBank(connection, transaction, draft.BankAccountCode);
                    if (bankId == null) throw new InvalidOperationException("حساب بانکی یافت نشد.");
                }
                if (draft.Direction == "OUT" && bankId == null)
                    throw new ArgumentException("برای چک پرداختی حساب بانکی الزامی است.");

                var fiscalYear = FiscalYearService.Resolve(databasePath, issueDate);
                using (var command = new SQLiteCommand(SqlDocumentInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", documentId);
                    command.Parameters.AddWithValue("@fiscal", fiscalYear);
                    command.Parameters.AddWithValue("@date", issueDate);
                    command.Parameters.AddWithValue("@party", (object)draft.PartyAccountId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@reference", draft.ChequeNumber);
                    command.Parameters.AddWithValue("@notes", (object)draft.Notes ?? DBNull.Value);
                    command.Parameters.AddWithValue("@user", userId);
                    command.ExecuteNonQuery();
                }
                using (var command = new SQLiteCommand(SqlChequeInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", chequeId);
                    command.Parameters.AddWithValue("@doc", documentId);
                    command.Parameters.AddWithValue("@number", draft.ChequeNumber);
                    command.Parameters.AddWithValue("@bank", (object)bankId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@direction", draft.Direction);
                    command.Parameters.AddWithValue("@issue", issueDate);
                    command.Parameters.AddWithValue("@due", dueDate);
                    command.Parameters.AddWithValue("@amount", draft.AmountIrr);
                    command.Parameters.AddWithValue("@beneficiary", (object)draft.PartyAccountId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@book", (object)draft.ChequeBookId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@notes", (object)draft.Notes ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
                InsertEvent(connection, transaction, chequeId, draft.Direction == "IN" ? "RECEIVED" : "ISSUED",
                    issueDate, draft.AmountIrr, documentId, draft.Notes, userId);

                using (var history = new SQLiteCommand(SqlHistoryInsert, connection, transaction))
                {
                    history.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    history.Parameters.AddWithValue("@doc", documentId);
                    history.Parameters.AddWithValue("@user", userId);
                    history.Parameters.AddWithValue("@reason", "ثبت چک؛ در انتظار تأیید مدیر");
                    history.ExecuteNonQuery();
                }
                using (var audit = new SQLiteCommand(SqlAuditInsert, connection, transaction))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", userId);
                    audit.Parameters.AddWithValue("@action", "CREATE_CHEQUE");
                    audit.Parameters.AddWithValue("@entityType", "DOCUMENT");
                    audit.Parameters.AddWithValue("@entityId", documentId);
                    audit.Parameters.AddWithValue("@after",
                        "{\"cheque_number\":\"" + draft.ChequeNumber + "\",\"direction\":\""
                        + draft.Direction + "\"}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                JournalService.EnsureEntry(connection, transaction, documentId, userId);
                transaction.Commit();
            }
            return chequeId;
        }

        /// <summary>
        /// Clears a cheque: status becomes CLEARED and exactly one BANK_TXN
        /// document with its payment transaction is created.
        /// </summary>
        public static string Clear(string databasePath, string chequeId, string dateIso, string userId)
        {
            var clearDate = PurchaseService.RequireDate(dateIso);
            string bankId, direction;
            long amount;
            using (var connection = PurchaseService.Open(databasePath))
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                PurchaseService.RequirePermission(connection, transaction, userId, "cheque.manage");
                using (var command = new SQLiteCommand(
                    "SELECT bank_account_id,direction,amount_irr FROM cheques WHERE id=@id;",
                    connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", chequeId);
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read()) throw new InvalidOperationException("چک پیدا نشد.");
                        bankId = reader.IsDBNull(0) ? null : reader.GetString(0);
                        direction = reader.GetString(1);
                        amount = reader.GetInt64(2);
                    }
                }
                if (bankId == null) throw new InvalidOperationException("چک بدون حساب بانکی قابل وصول نیست.");

                var bankTxnId = Guid.NewGuid().ToString("N");
                var fiscalYear = FiscalYearService.Resolve(databasePath, clearDate);
                using (var command = new SQLiteCommand(SqlBankTxnDocument, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", bankTxnId);
                    command.Parameters.AddWithValue("@fiscal", fiscalYear);
                    command.Parameters.AddWithValue("@date", clearDate);
                    command.Parameters.AddWithValue("@party", DBNull.Value);
                    command.Parameters.AddWithValue("@reference", chequeId);
                    command.Parameters.AddWithValue("@notes", "وصول چک شماره " + chequeId);
                    command.Parameters.AddWithValue("@user", userId);
                    command.ExecuteNonQuery();
                }
                using (var command = new SQLiteCommand(SqlBankPaymentInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@doc", bankTxnId);
                    command.Parameters.AddWithValue("@bank", bankId);
                    command.Parameters.AddWithValue("@party", DBNull.Value);
                    command.Parameters.AddWithValue("@direction", direction);
                    command.Parameters.AddWithValue("@amount", amount);
                    command.Parameters.AddWithValue("@cheque", chequeId);
                    command.Parameters.AddWithValue("@notes", "برگشت چک");
                    command.ExecuteNonQuery();
                }
                InsertEvent(connection, transaction, chequeId, "CLEARED", clearDate, amount,
                    bankTxnId, "وصول چک و ثبت تراکنش بانکی", userId);
                using (var status = new SQLiteCommand(SqlStatusUpdate, connection, transaction))
                {
                    status.Parameters.AddWithValue("@status", "CLEARED");
                    status.Parameters.AddWithValue("@cheque", chequeId);
                    status.ExecuteNonQuery();
                }
                using (var audit = new SQLiteCommand(SqlAuditInsert, connection, transaction))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", userId);
                    audit.Parameters.AddWithValue("@action", "CLEAR_CHEQUE");
                    audit.Parameters.AddWithValue("@entityType", "CHEQUE");
                    audit.Parameters.AddWithValue("@entityId", chequeId);
                    audit.Parameters.AddWithValue("@after",
                        "{\"bank_transaction\":\"" + bankTxnId + "\",\"amount_irr\":" + amount + "}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            return chequeId;
        }

        /// <summary>
        /// Records a status change (RETURNED / CANCELLED) with its event.
        /// </summary>
        public static void MarkStatus(string databasePath, string chequeId, string status,
            string dateIso, string userId)
        {
            if (status != "RETURNED" && status != "CANCELLED" && status != "CIRCULATING")
                throw new ArgumentException("وضعیت چک نامعتبر است.");
            var eventDate = PurchaseService.RequireDate(dateIso);
            using (var connection = PurchaseService.Open(databasePath))
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                PurchaseService.RequirePermission(connection, transaction, userId, "cheque.manage");
                long amount;
                using (var command = new SQLiteCommand(
                    "SELECT amount_irr FROM cheques WHERE id=@id;", connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", chequeId);
                    var value = command.ExecuteScalar();
                    if (value == null) throw new InvalidOperationException("چک پیدا نشد.");
                    amount = Convert.ToInt64(value);
                }
                InsertEvent(connection, transaction, chequeId, status, eventDate, amount, null,
                    "تغییر وضعیت چک به " + status, userId);
                using (var update = new SQLiteCommand(SqlStatusUpdate, connection, transaction))
                {
                    update.Parameters.AddWithValue("@status", status);
                    update.Parameters.AddWithValue("@cheque", chequeId);
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        private static void InsertEvent(SQLiteConnection connection, SQLiteTransaction transaction,
            string chequeId, string eventType, string dateIso, long amount, string relatedDocumentId,
            string notes, string userId)
        {
            using (var command = new SQLiteCommand(SqlEventInsert, connection, transaction))
            {
                command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("@cheque", chequeId);
                command.Parameters.AddWithValue("@type", eventType);
                command.Parameters.AddWithValue("@date", dateIso);
                command.Parameters.AddWithValue("@amount", amount);
                command.Parameters.AddWithValue("@related", (object)relatedDocumentId ?? DBNull.Value);
                command.Parameters.AddWithValue("@notes", (object)notes ?? DBNull.Value);
                command.Parameters.AddWithValue("@user", userId);
                command.ExecuteNonQuery();
            }
        }

        internal static string StatusLabel(string value)
        {
            switch (value)
            {
                case "ISSUED": return "صادرشده";
                case "CIRCULATING": return "در گردش";
                case "CLEARED": return "وصول‌شده";
                case "RETURNED": return "برگشتی";
                case "CANCELLED": return "باطل‌شده";
                default: return value;
            }
        }
    }
}
