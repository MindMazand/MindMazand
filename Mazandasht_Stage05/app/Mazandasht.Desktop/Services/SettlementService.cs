using System;
using System.Collections.Generic;
using System.Data.SQLite;

namespace Mazandasht.Desktop.Services
{
    public sealed class SettlementDraft
    {
        public string PaymentDocumentId { get; set; }
        public string InvoiceDocumentId { get; set; }
        public long AmountIrr { get; set; }
        public string Notes { get; set; }
    }

    public sealed class SettlementRow
    {
        public string PaymentNumber { get; set; }
        public string InvoiceNumber { get; set; }
        public long AmountIrr { get; set; }
        public string InvoiceParty { get; set; }
        public string Notes { get; set; }
    }

    /// <summary>
    /// Settlement allocation (G8).
    ///
    /// Rules honoured (12_Rules_Contract.json, section "settlement"):
    ///  - links: payment_document -> invoice_document;
    ///  - allocationSumMustNotExceedPayment: the database triggers
    ///    trg_settlement_not_exceed_payment(_on_update) enforce it and the service
    ///    checks it before writing;
    ///  - createsNewBankTransaction = false: allocation never creates a bank
    ///    transaction of its own.
    /// The payment must already exist as a payment_transactions row
    /// (trg_settlement_requires_payment_txn).
    /// </summary>
    public static class SettlementService
    {
        private const string SqlAllocationInsert =
            @"INSERT INTO settlement_allocations(id,payment_document_id,invoice_document_id,amount_irr,
                    allocated_by,notes) VALUES(@id,@payment,@invoice,@amount,@user,@notes);";
        private const string SqlRelationInsert =
            @"INSERT INTO document_relations(id,source_document_id,target_document_id,relation_type,notes)
              VALUES(@id,@source,@target,'PAYMENT_FOR',@notes);";
        private const string SqlAuditInsert =
            @"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
              VALUES(@id,@actor,@action,@entityType,@entityId,@after,@machine);";
        private const string SqlList =
            @"SELECT pd.document_number,id2.document_number,sa.amount_irr,
                     COALESCE(p.display_name,'—'),sa.notes
              FROM settlement_allocations sa
              JOIN documents pd ON pd.id=sa.payment_document_id
              JOIN documents id2 ON id2.id=sa.invoice_document_id
              LEFT JOIN party_accounts pa ON pa.id=id2.party_account_id
              LEFT JOIN parties p ON p.id=pa.party_id
              ORDER BY sa.allocated_at_utc DESC LIMIT 300;";
        private const string SqlPaymentAmount =
            @"SELECT amount_irr FROM payment_transactions WHERE document_id=@doc;";
        private const string SqlAllocatedSum =
            @"SELECT COALESCE(SUM(amount_irr),0) FROM settlement_allocations WHERE payment_document_id=@doc;";
        private const string SqlPermissionCheck =
            @"SELECT COUNT(*) FROM users u
              JOIN roles r ON r.id=u.role_id
              JOIN role_permissions rp ON rp.role_id=r.id
              WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
                AND rp.permission_code='bank.manage' AND rp.granted=1;";

        public static IList<SettlementRow> List(string databasePath)
        {
            var rows = new List<SettlementRow>();
            using (var connection = PurchaseService.Open(databasePath))
            using (var command = new SQLiteCommand(SqlList, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new SettlementRow {
                        PaymentNumber = reader.GetString(0), InvoiceNumber = reader.GetString(1),
                        AmountIrr = reader.GetInt64(2), InvoiceParty = reader.GetString(3),
                        Notes = reader.IsDBNull(4) ? null : reader.GetString(4)
                    });
            return rows;
        }

        /// <summary>
        /// Remaining unallocated amount of a payment document.
        /// </summary>
        public static long Remaining(string databasePath, string paymentDocumentId)
        {
            using (var connection = PurchaseService.Open(databasePath))
            using (var command = new SQLiteCommand(SqlPaymentAmount, connection))
            {
                command.Parameters.AddWithValue("@doc", paymentDocumentId);
                var value = command.ExecuteScalar();
                if (value == null) throw new InvalidOperationException("سند پرداخت پیدا نشد.");
                var amount = Convert.ToInt64(value);
                using (var allocated = new SQLiteCommand(SqlAllocatedSum, connection))
                {
                    allocated.Parameters.AddWithValue("@doc", paymentDocumentId);
                    return amount - Convert.ToInt64(allocated.ExecuteScalar());
                }
            }
        }

        public static void Allocate(string databasePath, SettlementDraft draft, string userId)
        {
            if (draft == null) throw new ArgumentNullException("draft");
            if (String.IsNullOrWhiteSpace(draft.PaymentDocumentId) || String.IsNullOrWhiteSpace(draft.InvoiceDocumentId))
                throw new ArgumentException("سند پرداخت و فاکتور الزامی هستند.");
            if (draft.PaymentDocumentId == draft.InvoiceDocumentId)
                throw new ArgumentException("سند پرداخت و فاکتور نمی‌توانند یکی باشند.");
            if (draft.AmountIrr <= 0) throw new ArgumentException("مبلغ تخصیص باید بیشتر از صفر باشد.");

            using (var connection = PurchaseService.Open(databasePath))
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                PurchaseService.RequirePermission(connection, transaction, userId, "bank.manage");

                var paymentAmount = ReadScalar(connection, transaction, SqlPaymentAmount, "@doc",
                    draft.PaymentDocumentId);
                if (paymentAmount == null)
                    throw new InvalidOperationException("سند پرداخت باید تراکنش بانکی داشته باشد.");
                var allocated = Convert.ToInt64(ReadScalar(connection, transaction, SqlAllocatedSum, "@doc",
                    draft.PaymentDocumentId));
                if (allocated + draft.AmountIrr > Convert.ToInt64(paymentAmount))
                    throw new InvalidOperationException("مجموع تخصیص‌ها از مبلغ پرداخت بیشتر است.");

                if (!DocumentExists(connection, transaction, draft.InvoiceDocumentId))
                    throw new InvalidOperationException("سند فاکتور پیدا نشد.");

                using (var command = new SQLiteCommand(SqlAllocationInsert, connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@payment", draft.PaymentDocumentId);
                    command.Parameters.AddWithValue("@invoice", draft.InvoiceDocumentId);
                    command.Parameters.AddWithValue("@amount", draft.AmountIrr);
                    command.Parameters.AddWithValue("@user", userId);
                    command.Parameters.AddWithValue("@notes", (object)draft.Notes ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
                using (var relation = new SQLiteCommand(SqlRelationInsert, connection, transaction))
                {
                    relation.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    relation.Parameters.AddWithValue("@source", draft.PaymentDocumentId);
                    relation.Parameters.AddWithValue("@target", draft.InvoiceDocumentId);
                    relation.Parameters.AddWithValue("@notes", "تخصیص پرداخت به فاکتور");
                    relation.ExecuteNonQuery();
                }
                using (var audit = new SQLiteCommand(SqlAuditInsert, connection, transaction))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", userId);
                    audit.Parameters.AddWithValue("@action", "ALLOCATE_SETTLEMENT");
                    audit.Parameters.AddWithValue("@entityType", "SETTLEMENT");
                    audit.Parameters.AddWithValue("@entityId", draft.PaymentDocumentId);
                    audit.Parameters.AddWithValue("@after",
                        "{\"invoice\":\"" + draft.InvoiceDocumentId + "\",\"amount_irr\":"
                        + draft.AmountIrr + "}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        private static bool DocumentExists(SQLiteConnection connection, SQLiteTransaction transaction,
            string documentId)
        {
            using (var command = new SQLiteCommand(
                "SELECT COUNT(*) FROM documents WHERE id=@id;", connection, transaction))
            {
                command.Parameters.AddWithValue("@id", documentId);
                return Convert.ToInt32(command.ExecuteScalar()) == 1;
            }
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
