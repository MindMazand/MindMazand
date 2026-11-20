using System;
using System.Collections.Generic;
using System.Data.SQLite;

namespace Mazandasht.Desktop.Services
{
    public sealed class BankTransactionDraft
    {
        /// <summary>Either a bank_accounts.id, a ledger account code, or the account title.</summary>
        public string BankAccountCode { get; set; }
        public string Direction { get; set; }
        public string PartyAccountId { get; set; }
        public string TransactionTypeId { get; set; }
        public string MainGroupId { get; set; }
        public string SubGroupId { get; set; }
        public long AmountIrr { get; set; }
        public long FeeIrr { get; set; }
        public long? StatementBalanceIrr { get; set; }
        public string TrackingNumber { get; set; }
        public string DateIso { get; set; }
        public string Notes { get; set; }
    }

    public sealed class BankTransactionRow
    {
        public string DocumentNumber { get; set; }
        public string DateIso { get; set; }
        public string BankTitle { get; set; }
        public string Direction { get; set; }
        public long AmountIrr { get; set; }
        public string PartyName { get; set; }
        public string Status { get; set; }
    }

    public sealed class BankAccountRow
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string BankName { get; set; }
        public long BalanceIrr { get; set; }
    }

    /// <summary>
    /// Bank transactions (G8).
    ///
    /// Rules honoured (12_Rules_Contract.json, sections "money", "settlement"):
    ///  - amounts are integer Rial;
    ///  - a bank transaction is a document (BANK_TXN) plus exactly one
    ///    payment_transactions row, so settlement and the bank balance view can
    ///    always find it;
    ///  - the bank account may be given by id, by ledger account code or by title,
    ///    which closes the G6 gap where a bank statement code was never resolved.
    /// The document is created in REVIEW: no journal line is written in G8.
    /// </summary>
    public static class BankService
    {
        private const string SqlDocumentInsert =
            @"INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,
                    requires_journal,requires_stock_posting,party_account_id,reference_number,notes,created_by)
              VALUES(@id,'BANK_TXN',@fiscal,'',@date,'REVIEW',1,0,@party,@reference,@notes,@user);";
        private const string SqlPaymentInsert =
            @"INSERT INTO payment_transactions(document_id,bank_account_id,party_account_id,direction,
                    transaction_type_id,main_group_id,subgroup_id,amount_irr,statement_balance_irr,
                    system_balance_irr,notes)
              VALUES(@doc,@bank,@party,@direction,@type,@main,@subgroup,@amount,@statement,
                     @system,@notes);";
        private const string SqlHistoryInsert =
            @"INSERT INTO document_status_history(id,document_id,old_status,new_status,changed_by,reason)
              VALUES(@id,@doc,NULL,'REVIEW',@user,@reason);";
        private const string SqlAuditInsert =
            @"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
              VALUES(@id,@actor,@action,@entityType,@entityId,@after,@machine);";
        private const string SqlResolveBank =
            @"SELECT ba.id FROM bank_accounts ba
              LEFT JOIN ledger_accounts la ON la.id=ba.ledger_account_id
              WHERE ba.id=@code OR la.account_code=@code OR ba.display_title=@code
              ORDER BY CASE WHEN ba.id=@code THEN 0 ELSE 1 END LIMIT 1;";
        private const string SqlBalance =
            @"SELECT COALESCE(SUM(CASE WHEN direction='IN' THEN amount_irr ELSE -amount_irr END),0)
              FROM payment_transactions WHERE bank_account_id=@bank;";
        private const string SqlList =
            @"SELECT d.document_number,d.date_iso,COALESCE(ba.display_title,'—'),pt.direction,
                     pt.amount_irr,COALESCE(p.display_name,'—'),d.status
              FROM payment_transactions pt
              JOIN documents d ON d.id=pt.document_id
              LEFT JOIN bank_accounts ba ON ba.id=pt.bank_account_id
              LEFT JOIN party_accounts pa ON pa.id=pt.party_account_id
              LEFT JOIN parties p ON p.id=pa.party_id
              ORDER BY d.created_at_utc DESC LIMIT 300;";
        private const string SqlAccounts =
            @"SELECT ba.id,ba.display_title,COALESCE(ba.bank_name,'—'),
                     COALESCE(SUM(CASE WHEN pt.direction='IN' THEN pt.amount_irr ELSE -pt.amount_irr END),0)
              FROM bank_accounts ba
              LEFT JOIN payment_transactions pt ON pt.bank_account_id=ba.id
              WHERE ba.is_active=1
              GROUP BY ba.id ORDER BY ba.display_title;";
        private const string SqlPermissionCheck =
            @"SELECT COUNT(*) FROM users u
              JOIN roles r ON r.id=u.role_id
              JOIN role_permissions rp ON rp.role_id=r.id
              WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
                AND rp.permission_code='bank.manage' AND rp.granted=1;";

        public static IList<BankAccountRow> Accounts(string databasePath)
        {
            var rows = new List<BankAccountRow>();
            using (var connection = PurchaseService.Open(databasePath))
            using (var command = new SQLiteCommand(SqlAccounts, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new BankAccountRow {
                        Id = reader.GetString(0), Title = reader.GetString(1),
                        BankName = reader.GetString(2), BalanceIrr = reader.GetInt64(3)
                    });
            return rows;
        }

        public static IList<BankTransactionRow> List(string databasePath)
        {
            var rows = new List<BankTransactionRow>();
            using (var connection = PurchaseService.Open(databasePath))
            using (var command = new SQLiteCommand(SqlList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new BankTransactionRow {
                        DocumentNumber = reader.GetString(0), DateIso = reader.GetString(1),
                        BankTitle = reader.GetString(2), Direction = reader.GetString(3),
                        AmountIrr = reader.GetInt64(4), PartyName = reader.GetString(5),
                        Status = PurchaseService.StatusLabel(reader.GetString(6))
                    });
            return rows;
        }

        public static string SaveDraft(string databasePath, BankTransactionDraft draft, string userId)
        {
            if (draft == null) throw new ArgumentNullException("draft");
            if (String.IsNullOrWhiteSpace(draft.BankAccountCode))
                throw new ArgumentException("حساب بانکی الزامی است.");
            if (draft.Direction != "IN" && draft.Direction != "OUT")
                throw new ArgumentException("جهت تراکنش باید IN یا OUT باشد.");
            if (draft.AmountIrr <= 0) throw new ArgumentException("مبلغ تراکنش باید بیشتر از صفر باشد.");
            if (draft.FeeIrr < 0) throw new ArgumentException("کارمزد نمی‌تواند منفی باشد.");
            var dateIso = PurchaseService.RequireDate(draft.DateIso);

            var documentId = Guid.NewGuid().ToString("N");
            using (var connection = PurchaseService.Open(databasePath))
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                PurchaseService.RequirePermission(connection, transaction, userId, "bank.manage");

                var bankId = ResolveBank(connection, transaction, draft.BankAccountCode);
                if (bankId == null)
                    throw new InvalidOperationException("حساب بانکی یافت نشد.");

                var systemBalance = Convert.ToInt64(ReadScalar(connection, transaction, SqlBalance, "@bank", bankId));

                var fiscalYear = FiscalYearService.Resolve(databasePath, dateIso);
                using (var command = new SQLiteCommand(SqlDocumentInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", documentId);
                    command.Parameters.AddWithValue("@fiscal", fiscalYear);
                    command.Parameters.AddWithValue("@date", dateIso);
                    command.Parameters.AddWithValue("@party", (object)draft.PartyAccountId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@reference",
                        (object)draft.TrackingNumber ?? DBNull.Value);
                    command.Parameters.AddWithValue("@notes", (object)draft.Notes ?? DBNull.Value);
                    command.Parameters.AddWithValue("@user", userId);
                    command.ExecuteNonQuery();
                }

                var delta = draft.Direction == "IN" ? draft.AmountIrr : -draft.AmountIrr;
                using (var command = new SQLiteCommand(SqlPaymentInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@doc", documentId);
                    command.Parameters.AddWithValue("@bank", bankId);
                    command.Parameters.AddWithValue("@party", (object)draft.PartyAccountId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@direction", draft.Direction);
                    command.Parameters.AddWithValue("@type", (object)draft.TransactionTypeId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@main", (object)draft.MainGroupId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@subgroup", (object)draft.SubGroupId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@amount", draft.AmountIrr);
                    command.Parameters.AddWithValue("@statement",
                        (object)draft.StatementBalanceIrr ?? DBNull.Value);
                    command.Parameters.AddWithValue("@system", systemBalance + delta);
                    command.Parameters.AddWithValue("@notes", (object)draft.Notes ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }

                using (var history = new SQLiteCommand(SqlHistoryInsert, connection, transaction))
                {
                    history.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    history.Parameters.AddWithValue("@doc", documentId);
                    history.Parameters.AddWithValue("@user", userId);
                    history.Parameters.AddWithValue("@reason", "ثبت تراکنش بانکی؛ در انتظار تأیید مدیر");
                    history.ExecuteNonQuery();
                }
                using (var audit = new SQLiteCommand(SqlAuditInsert, connection, transaction))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", userId);
                    audit.Parameters.AddWithValue("@action", "CREATE_BANK_TRANSACTION");
                    audit.Parameters.AddWithValue("@entityType", "DOCUMENT");
                    audit.Parameters.AddWithValue("@entityId", documentId);
                    audit.Parameters.AddWithValue("@after",
                        "{\"direction\":\"" + draft.Direction + "\",\"amount_irr\":" + draft.AmountIrr + "}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                JournalService.EnsureEntry(connection, transaction, documentId, userId);
                transaction.Commit();
            }
            return documentId;
        }

        /// <summary>
        /// Resolves a bank account from an id, a ledger account code or the title.
        /// </summary>
        public static string ResolveBank(SQLiteConnection connection, SQLiteTransaction transaction,
            string codeOrId)
        {
            var value = ReadScalar(connection, transaction, SqlResolveBank, "@code", codeOrId);
            return value == null ? null : Convert.ToString(value);
        }

        private static object ReadScalar(SQLiteConnection connection, SQLiteTransaction transaction,
            string sql, string parameter, object value)
        {
            using (var command = new SQLiteCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue(parameter, value);
                return command.ExecuteScalar();
            }
        }
    }
}
