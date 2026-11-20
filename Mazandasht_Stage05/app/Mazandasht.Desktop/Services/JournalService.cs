using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;

namespace Mazandasht.Desktop.Services
{
    public sealed class JournalLineRow
    {
        public int LineNumber { get; set; }
        public string AccountCode { get; set; }
        public string AccountName { get; set; }
        public long DebitIrr { get; set; }
        public long CreditIrr { get; set; }
    }

    public sealed class JournalEntryRow
    {
        public string EntryId { get; set; }
        public string JournalNumber { get; set; }
        public string DocumentId { get; set; }
        public string DocumentType { get; set; }
        public string DocumentNumber { get; set; }
        public string EntryDateIso { get; set; }
        public string Description { get; set; }
        public bool IsPosted { get; set; }
        public long TotalIrr { get; set; }
        public List<JournalLineRow> Lines { get; set; }
    }

    /// <summary>
    /// Double-entry journal engine (G9).
    ///
    /// Rules honoured (12_Rules_Contract.json, section "accounting"):
    ///  - minLines = 2, mustBalance = true;
    ///  - postRequiresApprovedDocument = true (enforced by the database trigger
    ///    trg_journal_must_balance_before_post);
    ///  - postedLinesImmutable = true (enforced by the database triggers);
    ///  - documentJournalRules map each document type to its debit/credit accounts.
    ///
    /// An entry is created unposted together with its document, so a REVIEW
    /// document still has no financial effect: the reporting views only read
    /// entries with is_posted = 1.
    /// </summary>
    public static class JournalService
    {
        private const string SqlEntryInsert =
            @"INSERT INTO journal_entries(id,journal_number,document_id,entry_date_iso,description,
                    is_posted) VALUES(@id,@number,@doc,@date,@description,0);";
        private const string SqlLineInsert =
            @"INSERT INTO journal_lines(id,journal_entry_id,line_number,ledger_account_id,
                    debit_irr,credit_irr,description)
              VALUES(@id,@entry,@line,@account,@debit,@credit,@description);";
        private const string SqlAuditInsert =
            @"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
              VALUES(@id,@actor,@action,@entityType,@entityId,@after,@machine);";
        private const string SqlEntries =
            @"SELECT je.id,je.journal_number,je.document_id,d.document_type,d.document_number,
                     je.entry_date_iso,je.description,je.is_posted,
                     COALESCE((SELECT SUM(debit_irr) FROM journal_lines WHERE journal_entry_id=je.id),0)
              FROM journal_entries je JOIN documents d ON d.id=je.document_id
              ORDER BY je.entry_date_iso DESC, je.journal_number DESC LIMIT 300;";
        private const string SqlLines =
            @"SELECT jl.line_number,la.account_code,la.display_name,jl.debit_irr,jl.credit_irr
              FROM journal_lines jl JOIN ledger_accounts la ON la.id=jl.ledger_account_id
              WHERE jl.journal_entry_id=@entry ORDER BY jl.line_number;";
        private const string SqlEntryForDocument =
            @"SELECT id FROM journal_entries WHERE document_id=@doc;";
        private const string SqlDocument =
            @"SELECT document_type,party_account_id,date_iso,fiscal_year,reference_number
              FROM documents WHERE id=@id;";
        private const string SqlPartyAccount =
            @"SELECT ledger_account_id FROM party_accounts WHERE id=@id;";
        private const string SqlAccountByCode =
            @"SELECT id FROM ledger_accounts WHERE account_code=@code AND is_active=1;";
        private const string SqlPurchaseTotal =
            @"SELECT COALESCE(SUM(net_amount_irr),0) FROM purchase_invoice_lines WHERE document_id=@doc;";
        private const string SqlSalesTotal =
            @"SELECT COALESCE(SUM(net_receivable_irr),0) FROM sales_invoice_lines WHERE document_id=@doc;";
        private const string SqlBankAmount =
            @"SELECT COALESCE(SUM(amount_irr),0) FROM payment_transactions WHERE document_id=@doc;";
        private const string SqlChequeAmount =
            @"SELECT COALESCE(SUM(amount_irr),0) FROM cheques WHERE document_id=@doc;";
        private const string SqlVatRate =
            @"SELECT value_integer FROM operational_parameters
              WHERE parameter_code='VAT_RATE_PERCENT' AND is_active=1;";

        public static IList<JournalEntryRow> List(string databasePath)
        {
            var rows = new List<JournalEntryRow>();
            using (var connection = PurchaseService.Open(databasePath))
            {
                // Materialise the entries first: a nested command cannot run on the
                // same connection while this reader is still open.
                using (var command = new SQLiteCommand(SqlEntries, connection))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        rows.Add(new JournalEntryRow {
                            EntryId = reader.GetString(0), JournalNumber = reader.GetString(1),
                            DocumentId = reader.GetString(2), DocumentType = reader.GetString(3),
                            DocumentNumber = reader.GetString(4), EntryDateIso = reader.GetString(5),
                            Description = reader.IsDBNull(6) ? null : reader.GetString(6),
                            IsPosted = Convert.ToInt32(reader.GetValue(7)) == 1,
                            TotalIrr = reader.GetInt64(8), Lines = new List<JournalLineRow>()
                        });

                foreach (var entry in rows)
                {
                    using (var lines = new SQLiteCommand(SqlLines, connection))
                    {
                        lines.Parameters.AddWithValue("@entry", entry.EntryId);
                        using (var lineReader = lines.ExecuteReader())
                            while (lineReader.Read())
                                entry.Lines.Add(new JournalLineRow {
                                    LineNumber = lineReader.GetInt32(0),
                                    AccountCode = lineReader.GetString(1),
                                    AccountName = lineReader.GetString(2),
                                    DebitIrr = lineReader.GetInt64(3),
                                    CreditIrr = lineReader.GetInt64(4)
                                });
                    }
                }
            }
            return rows;
        }

        /// <summary>
        /// Creates the balanced journal entry for a document if it does not have one
        /// yet. Safe to call repeatedly.
        /// </summary>
        public static string EnsureEntry(string databasePath, string documentId, string userId)
        {
            if (String.IsNullOrWhiteSpace(documentId)) throw new ArgumentException("سند الزامی است.");
            using (var connection = PurchaseService.Open(databasePath))
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                var entryId = EnsureEntry(connection, transaction, documentId, userId);
                transaction.Commit();
                return entryId;
            }
        }

        /// <summary>
        /// Transactional overload used by the module services so the journal entry
        /// and its document commit (or roll back) together.
        /// </summary>
        internal static string EnsureEntry(SQLiteConnection connection,
            SQLiteTransaction transaction, string documentId, string userId)
        {
            {
                var existing = ReadScalar(connection, transaction, SqlEntryForDocument, "@doc", documentId);
                if (existing != null) { return Convert.ToString(existing); }

                string documentType, partyAccountId, dateIso, fiscalYear, reference;
                using (var command = new SQLiteCommand(SqlDocument, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", documentId);
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read()) throw new InvalidOperationException("سند پیدا نشد.");
                        documentType = reader.GetString(0);
                        partyAccountId = reader.IsDBNull(1) ? null : reader.GetString(1);
                        dateIso = reader.GetString(2);
                        fiscalYear = reader.GetString(3);
                        reference = reader.IsDBNull(4) ? null : reader.GetString(4);
                    }
                }

                var lines = BuildLines(connection, transaction, documentType, partyAccountId,
                    documentId, dateIso, out var description);
                if (lines.Count < 2) throw new InvalidOperationException("المجو باید دست‌کم دو خط داشته باشد.");
                var debit = lines.Where(l => l.Item1 > 0).Sum(l => l.Item1);
                var credit = lines.Where(l => l.Item2 > 0).Sum(l => l.Item2);
                if (debit != credit) throw new InvalidOperationException("المجو متوازن نیست.");
                if (debit <= 0) throw new InvalidOperationException("مجموع المجو باید بیشتر از صفر باشد.");

                var entryId = Guid.NewGuid().ToString("N");
                var journalNumber = "J-" + fiscalYear + "-" + entryId.Substring(0, 8).ToUpperInvariant();
                using (var command = new SQLiteCommand(SqlEntryInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", entryId);
                    command.Parameters.AddWithValue("@number", journalNumber);
                    command.Parameters.AddWithValue("@doc", documentId);
                    command.Parameters.AddWithValue("@date", dateIso);
                    command.Parameters.AddWithValue("@description",
                        (object)(description + " | " + (reference ?? documentId)) ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }

                var lineNumber = 0;
                foreach (var line in lines)
                {
                    lineNumber++;
                    using (var command = new SQLiteCommand(SqlLineInsert, connection, transaction))
                    {
                        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                        command.Parameters.AddWithValue("@entry", entryId);
                        command.Parameters.AddWithValue("@line", lineNumber);
                        command.Parameters.AddWithValue("@account", line.Item3);
                        command.Parameters.AddWithValue("@debit", line.Item1);
                        command.Parameters.AddWithValue("@credit", line.Item2);
                        command.Parameters.AddWithValue("@description", documentType);
                        command.ExecuteNonQuery();
                    }
                }

                using (var audit = new SQLiteCommand(SqlAuditInsert, connection, transaction))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", userId);
                    audit.Parameters.AddWithValue("@action", "CREATE_JOURNAL_ENTRY");
                    audit.Parameters.AddWithValue("@entityType", "JOURNAL");
                    audit.Parameters.AddWithValue("@entityId", entryId);
                    audit.Parameters.AddWithValue("@after",
                        "{\"document_type\":\"" + documentType + "\",\"total_irr\":" + debit + "}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                return entryId;
            }
        }

        /// <summary>
        /// Posts the entry of an approved document. The database trigger refuses to
        /// post an unbalanced entry, an entry with fewer than two lines, or an entry
        /// whose document is not APPROVED.
        /// </summary>
        public static void PostEntry(string databasePath, string documentId, string userId)
        {
            using (var connection = PurchaseService.Open(databasePath))
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                var entryId = ReadScalar(connection, transaction, SqlEntryForDocument, "@doc", documentId);
                if (entryId == null)
                    throw new InvalidOperationException("برای این سند المجوی ساخته نشده است.");
                using (var command = new SQLiteCommand(
                    "UPDATE journal_entries SET is_posted=1, posted_by=@user, "
                    + "posted_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=@id AND is_posted=0;",
                    connection, transaction))
                {
                    command.Parameters.AddWithValue("@user", userId);
                    command.Parameters.AddWithValue("@id", Convert.ToString(entryId));
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        /// <summary>
        /// Builds the debit/credit lines for a document type, following
        /// documentJournalRules in the rules contract.
        /// </summary>
        private static List<Tuple<long, long, string>> BuildLines(SQLiteConnection connection,
            SQLiteTransaction transaction, string documentType, string partyAccountId,
            string documentId, string dateIso, out string description)
        {
            var lines = new List<Tuple<long, long, string>>();
            string partyLedgerId = null;
            if (partyAccountId != null)
                partyLedgerId = ReadScalar(connection, transaction, SqlPartyAccount, "@id", partyAccountId) as string;

            switch (documentType)
            {
                case "PURCHASE":
                {
                    var total = Convert.ToInt64(ReadScalar(connection, transaction, SqlPurchaseTotal,
                        "@doc", documentId));
                    description = "ثبت تعهد خرید";
                    lines.Add(Tuple.Create(total, 0L, AccountId(connection, transaction, "5001")));   // COGS
                    lines.Add(Tuple.Create(0L, total,
                        partyLedgerId ?? AccountId(connection, transaction, "2001")));                // party payable
                    break;
                }
                case "SALE":
                {
                    var total = Convert.ToInt64(ReadScalar(connection, transaction, SqlSalesTotal,
                        "@doc", documentId));
                    var vat = VatRate(connection, transaction);
                    var tax = (long)Math.Round(total * vat / 100m, MidpointRounding.AwayFromZero);
                    var revenue = total - tax;
                    description = "ثبت فروش" + (tax > 0 ? " و مالیات بر ارزش افزوده" : "");
                    lines.Add(Tuple.Create(total, 0L,
                        partyLedgerId ?? AccountId(connection, transaction, "1101")));                // party receivable
                    if (revenue > 0)
                        lines.Add(Tuple.Create(0L, revenue, AccountId(connection, transaction, "4001")));
                    if (tax > 0)
                        lines.Add(Tuple.Create(0L, tax, AccountId(connection, transaction, "2002")));
                    break;
                }
                case "BANK_TXN":
                {
                    var amount = Convert.ToInt64(ReadScalar(connection, transaction, SqlBankAmount,
                        "@doc", documentId));
                    string direction;
                    using (var command = new SQLiteCommand(
                        "SELECT direction FROM payment_transactions WHERE document_id=@doc LIMIT 1;",
                        connection, transaction))
                    {
                        command.Parameters.AddWithValue("@doc", documentId);
                        direction = Convert.ToString(command.ExecuteScalar());
                    }
                    description = "تراکنش بانکی " + (direction == "IN" ? "ورودی" : "خروجی");
                    if (direction == "IN")
                    {
                        lines.Add(Tuple.Create(amount, 0L, AccountId(connection, transaction, "1003")));
                        lines.Add(Tuple.Create(0L, amount,
                            partyLedgerId ?? AccountId(connection, transaction, "1101")));
                    }
                    else
                    {
                        lines.Add(Tuple.Create(amount, 0L,
                            partyLedgerId ?? AccountId(connection, transaction, "2001")));
                        lines.Add(Tuple.Create(0L, amount, AccountId(connection, transaction, "1003")));
                    }
                    break;
                }
                case "CHEQUE":
                {
                    var amount = Convert.ToInt64(ReadScalar(connection, transaction, SqlChequeAmount,
                        "@doc", documentId));
                    string direction;
                    using (var command = new SQLiteCommand(
                        "SELECT direction FROM cheques WHERE document_id=@doc LIMIT 1;",
                        connection, transaction))
                    {
                        command.Parameters.AddWithValue("@doc", documentId);
                        direction = Convert.ToString(command.ExecuteScalar());
                    }
                    description = "چک " + (direction == "IN" ? "دریافتی" : "پرداختی");
                    if (direction == "IN")
                    {
                        lines.Add(Tuple.Create(amount, 0L, AccountId(connection, transaction, "1003")));
                        lines.Add(Tuple.Create(0L, amount,
                            partyLedgerId ?? AccountId(connection, transaction, "1101")));
                    }
                    else
                    {
                        lines.Add(Tuple.Create(amount, 0L,
                            partyLedgerId ?? AccountId(connection, transaction, "2001")));
                        lines.Add(Tuple.Create(0L, amount, AccountId(connection, transaction, "1003")));
                    }
                    break;
                }
                default:
                    throw new InvalidOperationException(
                        "برای نوع سند " + documentType + " قاعدهٔ المجو تعریف نشده است.");
            }

            // A zero-value line is meaningless; drop it so the entry stays readable.
            return lines.Where(l => l.Item1 > 0 || l.Item2 > 0).ToList();
        }

        private static string AccountId(SQLiteConnection connection, SQLiteTransaction transaction,
            string accountCode)
        {
            var id = ReadScalar(connection, transaction, SqlAccountByCode, "@code", accountCode) as string;
            if (id == null)
                throw new InvalidOperationException("حساب سامانه‌ای با کد " + accountCode + " پیدا نشد.");
            return id;
        }

        private static decimal VatRate(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            object value;
            using (var command = new SQLiteCommand(SqlVatRate, connection, transaction))
                value = command.ExecuteScalar();
            if (value == null || value == DBNull.Value) return 9m;
            decimal rate;
            if (!Decimal.TryParse(Convert.ToString(value), NumberStyles.Number,
                CultureInfo.InvariantCulture, out rate)) return 9m;
            return rate < 0 || rate > 100 ? 9m : rate;
        }

        private static object ReadScalar(SQLiteConnection connection, SQLiteTransaction transaction,
            string sql, string parameter, object value)
        {
            using (var command = new SQLiteCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue(parameter, value ?? DBNull.Value);
                return command.ExecuteScalar();
            }
        }
    }
}
