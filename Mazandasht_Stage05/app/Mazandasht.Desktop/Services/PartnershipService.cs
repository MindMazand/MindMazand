using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;

namespace Mazandasht.Desktop.Services
{
    public sealed class PartnershipShare
    {
        public string PartyAccountId { get; set; }
        public decimal SharePercent { get; set; }
    }

    public sealed class PartnershipSplitRow
    {
        public string PartyAccountId { get; set; }
        public decimal SharePercent { get; set; }
        public long AllocatedGrams { get; set; }
    }

    /// <summary>
    /// Partnership share splitting (G7).
    ///
    /// Rules honoured (12_Rules_Contract.json, section "partnership"):
    ///  - shareSumMustEqual: the shares must sum to exactly 100;
    ///  - roundingUnitDefaultKg: 1 kg (1000 g);
    ///  - remainderPolicy: last_line (the rounding remainder goes to the last line).
    ///
    /// The allocation is materialised as document lines on the source document:
    /// one line per partner with quantity_g = the partner's share of the net
    /// weight. The split is pure arithmetic and writes nothing until the caller
    /// asks for it, so it can be reviewed before it becomes part of a document.
    /// </summary>
    public static class PartnershipService
    {
        public const long RoundingUnitGrams = 1000;
        public const decimal ShareSumTarget = 100m;

        /// <summary>
        /// Computes the per-partner allocation without writing anything.
        /// </summary>
        public static IList<PartnershipSplitRow> ComputeSplit(long netWeightGrams,
            IList<PartnershipShare> shares)
        {
            if (netWeightGrams <= 0) throw new ArgumentException("وزن خالص باید بیشتر از صفر باشد.");
            if (shares == null || shares.Count == 0) throw new ArgumentException("حداقل یک شریک الزامی است.");
            foreach (var share in shares)
            {
                if (String.IsNullOrWhiteSpace(share.PartyAccountId))
                    throw new ArgumentException("طرف حساب هر شریک الزامی است.");
                if (share.SharePercent <= 0) throw new ArgumentException("سهم هر شریک باید بیشتر از صفر باشد.");
            }

            var sum = shares.Sum(x => x.SharePercent);
            if (Math.Round(sum, 6, MidpointRounding.AwayFromZero) != ShareSumTarget)
                throw new ArgumentException("مجموع سهم‌ها باید دقیقاً ۱۰۰ درصد باشد.");

            var result = new List<PartnershipSplitRow>();
            var allocated = 0L;
            for (var i = 0; i < shares.Count; i++)
            {
                var share = shares[i];
                long grams;
                if (i == shares.Count - 1)
                {
                    // Remainder policy: last line absorbs the rounding remainder.
                    grams = netWeightGrams - allocated;
                }
                else
                {
                    var raw = netWeightGrams * (share.SharePercent / ShareSumTarget);
                    grams = (long)(Math.Floor(raw / RoundingUnitGrams) * RoundingUnitGrams);
                    if (grams < 0) grams = 0;
                }
                allocated += grams;
                result.Add(new PartnershipSplitRow {
                    PartyAccountId = share.PartyAccountId, SharePercent = share.SharePercent,
                    AllocatedGrams = grams
                });
            }

            if (result.Sum(x => x.AllocatedGrams) != netWeightGrams)
                throw new InvalidOperationException("تخصیص سهم‌ها با وزن خالص برابر نیست.");
            return result;
        }

        /// <summary>
        /// Materialises a weighing split as document lines. Called only when the
        /// weighing document actually carries one product and one packaging
        /// (weigh_ticket_packaging): the split then divides the packaging count
        /// with the same 1 kg rounding unit and last-line remainder.
        /// </summary>
        public static int ApplySplit(string databasePath, string documentId,
            long? netWeightGrams, int? packageCount, IList<PartnershipShare> shares, string userId)
        {
            var grams = ComputeSplit(netWeightGrams ?? 0, shares)
                .Select(x => (int)Math.Min(int.MaxValue, x.AllocatedGrams)).ToList();
            var quantities = grams;
            if (packageCount.HasValue && packageCount.Value > 0)
            {
                var total = packageCount.Value;
                quantities = new List<int>();
                var allocated = 0;
                for (var i = 0; i < grams.Count; i++)
                {
                    int count;
                    if (i == grams.Count - 1)
                        count = total - allocated;
                    else
                        count = (int)Math.Floor((double)total * shares[i].SharePercent / ShareSumTarget);
                    quantities.Add(count);
                    allocated += count;
                }
                if (allocated != total)
                    throw new InvalidOperationException("تخصیص تعداد سبد با تعداد کل برابر نیست.");
            }
            return ApplyLines(databasePath, documentId, quantities, shares, userId);
        }

        /// <summary>
        /// Writes a previously computed split (in grams) as document lines of the
        /// given document. The document must still be editable (DRAFT/REVIEW).
        /// </summary>
        public static int ApplySplit(string databasePath, string documentId,
            IList<PartnershipSplitRow> splits, string userId)
        {
            var shares = splits.Select(x => new PartnershipShare {
                PartyAccountId = x.PartyAccountId, SharePercent = x.SharePercent
            }).ToList();
            return ApplyLines(databasePath, documentId,
                splits.Select(x => (int)Math.Min(int.MaxValue, x.AllocatedGrams)).ToList(),
                shares, userId);
        }

        private static int ApplyLines(string databasePath, string documentId,
            IList<int> quantities, IList<PartnershipShare> shares, string userId)
        {
            if (quantities == null || quantities.Count == 0 || quantities.Count != shares.Count)
                throw new ArgumentException("سهم‌ها الزامی هستند.");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                string status;
                using (var command = new SQLiteCommand(
                    "SELECT status FROM documents WHERE id=@id;", connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", documentId);
                    status = Convert.ToString(command.ExecuteScalar());
                }
                if (status == null) throw new InvalidOperationException("سند شراکت پیدا نشد.");
                if (status != "DRAFT" && status != "REVIEW")
                    throw new InvalidOperationException("فقط سند پیش‌نویس یا در بررسی قابل تخصیص سهم است.");

                var nextLine = 1;
                using (var command = new SQLiteCommand(
                    "SELECT COALESCE(MAX(line_number),0)+1 FROM document_lines WHERE document_id=@id;",
                    connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", documentId);
                    nextLine = Convert.ToInt32(command.ExecuteScalar());
                }

                var lineNumber = nextLine - 1;
                for (var i = 0; i < splits.Count; i++)
                {
                    lineNumber++;
                    var share = shares[i];
                    using (var command = new SQLiteCommand(
                        "INSERT INTO document_lines(id,document_id,line_number,quantity_g,amount_irr,description) "
                        + "VALUES(@id,@doc,@line,@qty,0,@description);", connection, transaction))
                    {
                        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                        command.Parameters.AddWithValue("@doc", documentId);
                        command.Parameters.AddWithValue("@line", lineNumber);
                        command.Parameters.AddWithValue("@qty", quantities[i]);
                        command.Parameters.AddWithValue("@description",
                            "سهم شریک " + share.SharePercent.ToString("0.######", CultureInfo.InvariantCulture)
                            + "٪ | " + share.PartyAccountId);
                        command.ExecuteNonQuery();
                    }
                }

                using (var audit = new SQLiteCommand(
                    "INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name) "
                    + "VALUES(@id,@actor,'APPLY_PARTNERSHIP_SPLIT','DOCUMENT',@entity,@after,@machine);",
                    connection, transaction))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", userId);
                    audit.Parameters.AddWithValue("@entity", documentId);
                    audit.Parameters.AddWithValue("@after",
                        "{\"partners\":" + splits.Count + ",\"rounding_unit_g\":" + RoundingUnitGrams + "}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            return splits.Count;
        }

        private static SQLiteConnection Open(string databasePath)
        {
            var connection = new SQLiteConnection(
                "Data Source=" + databasePath + ";Version=3;Pooling=True;");
            connection.Open();
            using (var command = new SQLiteCommand("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", connection))
                command.ExecuteNonQuery();
            return connection;
        }
    }
}
