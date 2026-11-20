using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Globalization;

namespace Mazandasht.Desktop.Services
{
    public sealed class ReportDefinition
    {
        public string Code { get; set; }
        public string Title { get; set; }
        public bool NeedsDateRange { get; set; }
    }

    /// <summary>
    /// Management reports (G10).
    ///
    /// Every report is read-only and is built from the reconciliation views or
    /// from posted journal data, so a REVIEW document never appears in a
    /// management report (reports.onlyPostedData = true).
    /// </summary>
    public static class ReportService
    {
        private static readonly List<ReportDefinition> Definitions = new List<ReportDefinition> {
            new ReportDefinition { Code = "STOCK_ON_HAND", Title = "موجودی کالا به تفکیک خط و انبار" },
            new ReportDefinition { Code = "PARTY_BALANCE", Title = "مانده طرف‌ها" },
            new ReportDefinition { Code = "BANK_BALANCE", Title = "مانده حساب‌های بانکی" },
            new ReportDefinition { Code = "CHEQUE_REGISTER", Title = "دفتر چک‌ها" },
            new ReportDefinition { Code = "DAILY_FLOW", Title = "گردش روزانه باسکول", NeedsDateRange = true },
            new ReportDefinition { Code = "PROFIT_LOSS", Title = "سود و زیان به تفکیک حساب", NeedsDateRange = true },
            new ReportDefinition { Code = "DOCUMENTS", Title = "فهرست اسناد" }
        };

        public static IList<ReportDefinition> Available()
        {
            return new List<ReportDefinition>(Definitions);
        }

        public static ReportDefinition Find(string code)
        {
            return Definitions.Find(d => String.Equals(d.Code, code, StringComparison.Ordinal));
        }

        public static DataTable Build(string databasePath, string reportCode,
            string fromIso, string toIso)
        {
            var definition = Find(reportCode);
            if (definition == null)
                throw new ArgumentException("گزارش «" + reportCode + "» پیدا نشد.");

            switch (definition.Code)
            {
                case "STOCK_ON_HAND":
                    return Query(databasePath, "SELECT * FROM v_stock_on_hand ORDER BY product_name,warehouse_name,lot_code;");
                case "PARTY_BALANCE":
                    return Query(databasePath, "SELECT * FROM v_party_balance ORDER BY party_name;");
                case "BANK_BALANCE":
                    return Query(databasePath, "SELECT * FROM v_bank_balance ORDER BY display_title;");
                case "CHEQUE_REGISTER":
                    return Query(databasePath, "SELECT * FROM v_cheque_register ORDER BY due_date_iso;");
                case "DOCUMENTS":
                    return Query(databasePath,
                        "SELECT d.document_number AS شماره_سند, d.document_type AS نوع_سند, "
                        + "d.date_iso AS تاریخ, d.status AS وضعیت, "
                        + "COALESCE(p.display_name,'—') AS طرف_حساب, "
                        + "COALESCE(d.reference_number,'—') AS مرجع "
                        + "FROM documents d "
                        + "LEFT JOIN party_accounts pa ON pa.id=d.party_account_id "
                        + "LEFT JOIN parties p ON p.id=pa.party_id "
                        + "ORDER BY d.date_iso DESC, d.document_number DESC LIMIT 2000;");
                case "DAILY_FLOW":
                    return DailyFlow(databasePath, fromIso, toIso);
                case "PROFIT_LOSS":
                    return ProfitLoss(databasePath, fromIso, toIso);
                default:
                    throw new ArgumentException("گزارش پشتیبانی نمی‌شود.");
            }
        }

        /// <summary>
        /// Daily weighing flow for the management dashboard and the second display.
        /// </summary>
        private static DataTable DailyFlow(string databasePath, string fromIso, string toIso)
        {
            var table = new DataTable();
            table.Columns.Add("تاریخ", typeof(string));
            table.Columns.Add("ورود (گرم)", typeof(long));
            table.Columns.Add("خروج (گرم)", typeof(long));
            var points = DashboardQueryService.LoadDailyFlow(databasePath,
                DateTime.ParseExact(toIso, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DaysBetween(fromIso, toIso));
            foreach (var point in points)
            {
                table.Rows.Add(point.DateIso, point.InboundGrams, point.OutboundGrams);
            }
            return table;
        }

        private static DataTable ProfitLoss(string databasePath, string fromIso, string toIso)
        {
            var report = ProfitLossService.Report(databasePath, fromIso, toIso);
            var table = new DataTable();
            table.Columns.Add("کد حساب", typeof(string));
            table.Columns.Add("نام حساب", typeof(string));
            table.Columns.Add("کلاس", typeof(string));
            table.Columns.Add("بدهکار (ریال)", typeof(long));
            table.Columns.Add("بستانکار (ریال)", typeof(long));
            table.Columns.Add("خالص (ریال)", typeof(long));
            foreach (var account in report.Accounts)
                table.Rows.Add(account.AccountCode, account.AccountName, account.AccountClass,
                    account.DebitIrr, account.CreditIrr, account.NetIrr);
            table.Rows.Add("جمع", "درآمد", String.Empty, 0L, 0L, report.RevenueIrr);
            table.Rows.Add("جمع", "بهای کالای فروش‌رفته", String.Empty, 0L, 0L, report.CogsIrr);
            table.Rows.Add("جمع", "سود ناخالص", String.Empty, 0L, 0L, report.GrossProfitIrr);
            table.Rows.Add("جمع", "هزینه‌های عملیاتی", String.Empty, 0L, 0L, report.OperatingExpenseIrr);
            table.Rows.Add("جمع", "سود عملیاتی", String.Empty, 0L, 0L, report.OperatingProfitIrr);
            return table;
        }

        private static int DaysBetween(string fromIso, string toIso)
        {
            var from = DateTime.ParseExact(fromIso, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var to = DateTime.ParseExact(toIso, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var days = (int)(to - from).TotalDays + 1;
            if (days < 1) days = 1;
            if (days > 90) days = 90;
            return days;
        }

        private static DataTable Query(string databasePath, string sql)
        {
            var table = new DataTable();
            using (var connection = PurchaseService.Open(databasePath))
            using (var adapter = new SQLiteDataAdapter(sql, connection))
            {
                adapter.Fill(table);
            }
            return table;
        }
    }
}
