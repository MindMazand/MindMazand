using System;
using System.Data.SQLite;
using System.Collections.Generic;

namespace Mazandasht.Desktop.Services
{
    public sealed class DashboardSnapshot
    {
        public long InboundGrams { get; set; }
        public long OutboundGrams { get; set; }
        public long SalesIrr { get; set; }
        public long PendingApprovalCount { get; set; }
        public long NetPartyBalanceIrr { get; set; }
        public long OverdueChequeCount { get; set; }
        public long TodayWeighCount { get; set; }
    }

    public sealed class DailyFlowPoint
    {
        public string DateIso { get; set; }
        public long InboundGrams { get; set; }
        public long OutboundGrams { get; set; }
    }

    public static class DashboardQueryService
    {
        public static List<DailyFlowPoint> LoadDailyFlow(string path, DateTime localDate, int days)
        {
            if (days < 1 || days > 90) throw new ArgumentOutOfRangeException("days");
            var start = localDate.Date.AddDays(-(days - 1)).ToString("yyyy-MM-dd");
            var end = localDate.Date.ToString("yyyy-MM-dd");
            var map = new Dictionary<string, DailyFlowPoint>();
            for (var d = localDate.Date.AddDays(-(days - 1)); d <= localDate.Date; d = d.AddDays(1))
            {
                var key = d.ToString("yyyy-MM-dd");
                map[key] = new DailyFlowPoint { DateIso=key, InboundGrams=0, OutboundGrams=0 };
            }
            using (var c = new SQLiteConnection("Data Source=" + path + ";Version=3;Pooling=True;"))
            {
                c.Open();
                using (var cmd = new SQLiteCommand(@"SELECT d.date_iso,w.direction,SUM(w.net_weight_g)
                    FROM weigh_tickets w JOIN documents d ON d.id=w.document_id
                    WHERE d.status='APPROVED' AND d.date_iso BETWEEN @start AND @end
                    GROUP BY d.date_iso,w.direction ORDER BY d.date_iso;", c))
                {
                    cmd.Parameters.AddWithValue("@start",start); cmd.Parameters.AddWithValue("@end",end);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            DailyFlowPoint point;
                            if (!map.TryGetValue(r.GetString(0),out point)) continue;
                            if (r.GetString(1)=="IN") point.InboundGrams=r.GetInt64(2);
                            else if (r.GetString(1)=="OUT") point.OutboundGrams=r.GetInt64(2);
                        }
                }
            }
            return new List<DailyFlowPoint>(map.Values);
        }

        public static DashboardSnapshot Load(string path, DateTime localDate)
        {
            var result = new DashboardSnapshot();
            var date = localDate.ToString("yyyy-MM-dd");
            using (var c = new SQLiteConnection("Data Source=" + path + ";Version=3;Pooling=True;"))
            {
                c.Open();
                result.InboundGrams = ScalarLong(c, @"SELECT COALESCE(SUM(w.net_weight_g),0) FROM weigh_tickets w
                    JOIN documents d ON d.id=w.document_id WHERE d.status='APPROVED' AND d.date_iso=@date AND w.direction='IN';", date);
                result.OutboundGrams = ScalarLong(c, @"SELECT COALESCE(SUM(w.net_weight_g),0) FROM weigh_tickets w
                    JOIN documents d ON d.id=w.document_id WHERE d.status='APPROVED' AND d.date_iso=@date AND w.direction='OUT';", date);
                result.SalesIrr = ScalarLong(c, @"SELECT COALESCE(SUM(dl.amount_irr),0) FROM documents d
                    JOIN document_lines dl ON dl.document_id=d.id
                    WHERE d.status='APPROVED' AND d.document_type='SALE' AND d.date_iso=@date;", date);
                result.PendingApprovalCount = ScalarLong(c, "SELECT COUNT(*) FROM documents WHERE status='REVIEW';", date);
                result.NetPartyBalanceIrr = ScalarLong(c, @"SELECT COALESCE(SUM(jl.debit_irr-jl.credit_irr),0)
                    FROM party_accounts pa JOIN journal_lines jl ON jl.ledger_account_id=pa.ledger_account_id
                    JOIN journal_entries je ON je.id=jl.journal_entry_id
                    JOIN documents d ON d.id=je.document_id
                    WHERE je.is_posted=1 AND d.status='APPROVED';", date);
                result.OverdueChequeCount = ScalarLong(c, @"SELECT COUNT(*) FROM cheques WHERE status IN ('ISSUED','CIRCULATING')
                    AND due_date_iso < @date;", date);
                result.TodayWeighCount = ScalarLong(c, @"SELECT COUNT(*) FROM weigh_tickets w JOIN documents d ON d.id=w.document_id
                    WHERE d.date_iso=@date;", date);
            }
            return result;
        }

        private static long ScalarLong(SQLiteConnection c, string sql, string date)
        {
            using (var cmd = new SQLiteCommand(sql, c))
            {
                cmd.Parameters.AddWithValue("@date", date);
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }
    }
}
