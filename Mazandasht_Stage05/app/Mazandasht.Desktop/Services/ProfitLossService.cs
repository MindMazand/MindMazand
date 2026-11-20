using System;
using System.Collections.Generic;
using System.Data.SQLite;

namespace Mazandasht.Desktop.Services
{
    public sealed class ProfitLossAccountRow
    {
        public string AccountCode { get; set; }
        public string AccountName { get; set; }
        public string AccountClass { get; set; }
        public long DebitIrr { get; set; }
        public long CreditIrr { get; set; }
        public long NetIrr { get; set; }
    }

    public sealed class ProfitLossReport
    {
        public string FromIso { get; set; }
        public string ToIso { get; set; }
        public long RevenueIrr { get; set; }
        public long CogsIrr { get; set; }
        public long GrossProfitIrr { get; set; }
        public long OperatingExpenseIrr { get; set; }
        public long OperatingProfitIrr { get; set; }
        public List<ProfitLossAccountRow> Accounts { get; set; }
    }

    /// <summary>
    /// Profit &amp; loss reporting (G9).
    ///
    /// Rules honoured (12_Rules_Contract.json, section "accounting"):
    ///  - grossProfit = REVENUE - COGS;
    ///  - operatingProfit = grossProfit - OPERATING_EXPENSES;
    ///  - cogsMethod = FIFO (the cost side comes from the posted purchase/issue
    ///    journals, so it already reflects the FIFO allocation);
    ///  - reports.onlyPostedData = true: only journal entries with is_posted = 1
    ///    are aggregated, so a REVIEW document never changes a report.
    /// </summary>
    public static class ProfitLossService
    {
        private const string SqlReport =
            @"SELECT la.account_code,la.display_name,la.account_class,
                     COALESCE(SUM(jl.debit_irr),0), COALESCE(SUM(jl.credit_irr),0)
              FROM ledger_accounts la
              LEFT JOIN journal_lines jl ON jl.ledger_account_id=la.id
                   AND EXISTS (SELECT 1 FROM journal_entries je
                               WHERE je.id=jl.journal_entry_id AND je.is_posted=1
                                 AND je.entry_date_iso BETWEEN @from AND @to)
              GROUP BY la.id
              ORDER BY la.account_code;";

        /// <summary>
        /// Pure aggregation over the posted journal lines in a date range.
        /// </summary>
        public static ProfitLossReport Report(string databasePath, string fromIso, string toIso)
        {
            if (String.IsNullOrWhiteSpace(fromIso) || String.IsNullOrWhiteSpace(toIso))
                throw new ArgumentException("بازهٔ تاریخ گزارش الزامی است.");
            var report = new ProfitLossReport {
                FromIso = fromIso, ToIso = toIso, Accounts = new List<ProfitLossAccountRow>()
            };
            using (var connection = PurchaseService.Open(databasePath))
            using (var command = new SQLiteCommand(SqlReport, connection))
            {
                command.Parameters.AddWithValue("@from", fromIso);
                command.Parameters.AddWithValue("@to", toIso);
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        var row = new ProfitLossAccountRow {
                            AccountCode = reader.GetString(0), AccountName = reader.GetString(1),
                            AccountClass = reader.GetString(2),
                            DebitIrr = reader.GetInt64(3), CreditIrr = reader.GetInt64(4)
                        };
                        row.NetIrr = row.AccountClass == "REVENUE"
                            ? row.CreditIrr - row.DebitIrr
                            : row.DebitIrr - row.CreditIrr;
                        report.Accounts.Add(row);

                        if (row.AccountClass == "REVENUE")
                            report.RevenueIrr += row.CreditIrr - row.DebitIrr;
                        else if (row.AccountClass == "EXPENSE")
                        {
                            if (row.AccountCode == "5001")
                                report.CogsIrr += row.DebitIrr - row.CreditIrr;
                            else
                                report.OperatingExpenseIrr += row.DebitIrr - row.CreditIrr;
                        }
                    }
            }
            report.GrossProfitIrr = report.RevenueIrr - report.CogsIrr;
            report.OperatingProfitIrr = report.GrossProfitIrr - report.OperatingExpenseIrr;
            return report;
        }
    }
}
