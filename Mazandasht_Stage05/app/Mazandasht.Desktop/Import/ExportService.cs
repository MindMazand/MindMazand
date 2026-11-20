using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Text;

namespace Mazandasht.Desktop.Import
{
    /// <summary>A row of an export run, safe to show in a grid.</summary>
    public sealed class ExportRunRow
    {
        public string Id { get; set; }
        public string ExportKind { get; set; }
        public string TargetPath { get; set; }
        public string Status { get; set; }
        public int RowsExported { get; set; }
        public string CreatedAtUtc { get; set; }
        public string ErrorText { get; set; }
    }

    /// <summary>
    /// CSV export of operational documents.
    ///
    /// Only CSV is written here: the runtime references ExcelDataReader for
    /// reading and has no workbook-writing dependency, so claiming .xlsx output
    /// would be false. The CSV is UTF-8 with a byte-order mark so Excel opens it
    /// with the Persian text intact, and every run is recorded in export_runs.
    /// </summary>
    public static class ExportService
    {
        public const string CsvEncodingName = "utf-8";

        private const string SqlPermissionCheck =
            @"SELECT COUNT(*) FROM users u
              JOIN roles r ON r.id=u.role_id
              JOIN role_permissions rp ON rp.role_id=r.id
              WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
                AND rp.permission_code='reports.view' AND rp.granted=1;";
        private const string SqlDocumentExport =
            @"SELECT d.document_number,d.document_type,d.date_iso,d.fiscal_year,d.status,
                     COALESCE(p.display_name,'—'),COALESCE(d.reference_number,'—'),
                     (SELECT COUNT(*) FROM document_lines l WHERE l.document_id=d.id),
                     COALESCE((SELECT SUM(l.amount_irr) FROM document_lines l WHERE l.document_id=d.id),0),
                     COALESCE(d.notes,'')
              FROM documents d
              LEFT JOIN party_accounts pa ON pa.id=d.party_account_id
              LEFT JOIN parties p ON p.id=pa.party_id
              WHERE (@type IS NULL OR d.document_type=@type)
              ORDER BY d.date_iso,d.document_number;";
        private const string SqlExportStart =
            @"INSERT INTO export_runs(id,export_kind,target_path,status,created_by,rows_exported,error_text)
              VALUES(@id,@kind,@path,'STARTED',@user,0,NULL);";
        private const string SqlExportFinish =
            @"UPDATE export_runs SET status=@status,completed_at_utc=@completed,
                     rows_exported=@rows,error_text=@error WHERE id=@id;";
        private const string SqlExportRuns =
            @"SELECT id,export_kind,target_path,status,rows_exported,created_at_utc,error_text
              FROM export_runs ORDER BY created_at_utc DESC LIMIT 200;";

        private static readonly string[] Headers = {
            "شماره سند", "نوع سند", "تاریخ", "سال مالی", "وضعیت",
            "طرف حساب", "مرجع", "تعداد خطوط", "مبلغ (ریال)", "یادداشت"
        };

        public static IList<ExportRunRow> History(string databasePath)
        {
            var rows = new List<ExportRunRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlExportRuns, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new ExportRunRow {
                        Id = reader.GetString(0), ExportKind = reader.GetString(1),
                        TargetPath = reader.GetString(2), Status = reader.GetString(3),
                        RowsExported = reader.GetInt32(4), CreatedAtUtc = reader.GetString(5),
                        ErrorText = reader.IsDBNull(6) ? null : reader.GetString(6)
                    });
            return rows;
        }

        /// <summary>
        /// Exports documents of one type (or all types when documentType is null)
        /// to a CSV file and records the run. Returns the number of rows written.
        /// </summary>
        public static int ExportDocumentsCsv(string databasePath, string documentType,
            string outputPath, string userId)
        {
            if (String.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("مسیر خروجی الزامی است.");
            if (String.IsNullOrWhiteSpace(documentType)) documentType = null;

            var runId = Guid.NewGuid().ToString("N");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequirePermission(connection, transaction, userId);
                using (var start = new SQLiteCommand(SqlExportStart, connection, transaction))
                {
                    start.Parameters.AddWithValue("@id", runId);
                    start.Parameters.AddWithValue("@kind", "DELTA_EXCEL");
                    start.Parameters.AddWithValue("@path", outputPath);
                    start.Parameters.AddWithValue("@user", userId);
                    start.ExecuteNonQuery();
                }
                transaction.Commit();
            }

            var count = 0;
            string failure = null;
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var encoding = new UTF8Encoding(true);
                using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, encoding))
                {
                    writer.WriteLine(Join(Headers));
                    using (var connection = Open(databasePath))
                    using (var command = new SQLiteCommand(SqlDocumentExport, connection))
                    {
                        command.Parameters.AddWithValue("@type", (object)documentType ?? DBNull.Value);
                        using (var reader = command.ExecuteReader())
                            while (reader.Read())
                            {
                                writer.WriteLine(Join(new[] {
                                    reader.GetString(0), DocumentTypeLabel(reader.GetString(1)),
                                    reader.GetString(2), reader.GetString(3), StatusLabel(reader.GetString(4)),
                                    reader.GetString(5), reader.GetString(6),
                                    reader.GetInt32(7).ToString(CultureInfo.InvariantCulture),
                                    reader.GetInt64(8).ToString(CultureInfo.InvariantCulture),
                                    reader.GetString(9)
                                }));
                                count++;
                            }
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                throw;
            }
            finally
            {
                using (var connection = Open(databasePath))
                using (var transaction = connection.BeginTransaction())
                using (var finish = new SQLiteCommand(SqlExportFinish, connection, transaction))
                {
                    finish.Parameters.AddWithValue("@id", runId);
                    finish.Parameters.AddWithValue("@status", failure == null ? "SUCCESS" : "FAILED");
                    finish.Parameters.AddWithValue("@completed",
                        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                    finish.Parameters.AddWithValue("@rows", count);
                    finish.Parameters.AddWithValue("@error", (object)failure ?? DBNull.Value);
                    finish.ExecuteNonQuery();
                    transaction.Commit();
                }
            }
            return count;
        }

        /// <summary>
        /// Exports any in-memory report table (G10) to CSV and records the run.
        /// Used by the reports screen so every management report is exportable
        /// without inventing an .xlsx writer.
        /// </summary>
        public static int ExportTableCsv(string databasePath, string exportKind,
            System.Data.DataTable table, string outputPath, string userId)
        {
            if (table == null) throw new ArgumentNullException("table");
            if (String.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("مسیر خروجی الزامی است.");
            if (String.IsNullOrWhiteSpace(exportKind))
                throw new ArgumentException("نام گزارش الزامی است.");

            var runId = Guid.NewGuid().ToString("N");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequirePermission(connection, transaction, userId);
                using (var start = new SQLiteCommand(SqlExportStart, connection, transaction))
                {
                    start.Parameters.AddWithValue("@id", runId);
                    start.Parameters.AddWithValue("@kind", exportKind);
                    start.Parameters.AddWithValue("@path", outputPath);
                    start.Parameters.AddWithValue("@user", userId);
                    start.ExecuteNonQuery();
                }
                transaction.Commit();
            }

            var count = 0;
            string failure = null;
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var encoding = new UTF8Encoding(true);
                using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, encoding))
                {
                    var headers = new List<string>();
                    foreach (System.Data.DataColumn column in table.Columns)
                        headers.Add(column.ColumnName);
                    writer.WriteLine(Join(headers));

                    foreach (System.Data.DataRow row in table.Rows)
                    {
                        var values = new List<string>();
                        foreach (System.Data.DataColumn column in table.Columns)
                        {
                            var value = row[column];
                            values.Add(value == null || value == DBNull.Value
                                ? String.Empty : Convert.ToString(value, CultureInfo.InvariantCulture));
                        }
                        writer.WriteLine(Join(values));
                        count++;
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                throw;
            }
            finally
            {
                using (var connection = Open(databasePath))
                using (var transaction = connection.BeginTransaction())
                using (var finish = new SQLiteCommand(SqlExportFinish, connection, transaction))
                {
                    finish.Parameters.AddWithValue("@id", runId);
                    finish.Parameters.AddWithValue("@status", failure == null ? "SUCCESS" : "FAILED");
                    finish.Parameters.AddWithValue("@completed",
                        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                    finish.Parameters.AddWithValue("@rows", count);
                    finish.Parameters.AddWithValue("@error", (object)failure ?? DBNull.Value);
                    finish.ExecuteNonQuery();
                    transaction.Commit();
                }
            }
            return count;
        }

        // ------------------------------------------------------------------
        private static void RequirePermission(SQLiteConnection connection,
            SQLiteTransaction transaction, string userId)
        {
            using (var command = new SQLiteCommand(SqlPermissionCheck, connection, transaction))
            {
                command.Parameters.AddWithValue("@user", userId ?? String.Empty);
                if (Convert.ToInt32(command.ExecuteScalar()) != 1)
                    throw new UnauthorizedAccessException(
                        "خروجی گرفتن از اسناد فقط برای کاربر دارای مجوز «مشاهده گزارش‌ها» مجاز است.");
            }
        }

        private static string Join(IList<string> values)
        {
            var escaped = new List<string>();
            foreach (var value in values)
            {
                var text = value ?? String.Empty;
                if (text.IndexOfAny(new[] { '"', ',', '\n', '\r' }) >= 0)
                    text = "\"" + text.Replace("\"", "\"\"") + "\"";
                escaped.Add(text);
            }
            return String.Join(",", escaped);
        }

        private static string DocumentTypeLabel(string value)
        {
            switch (value)
            {
                case "WEIGHING": return "باسکول";
                case "PURCHASE": return "خرید";
                case "SALE": return "فروش";
                case "SORTING": return "سورتینگ";
                case "BASKET": return "گردش سبد";
                case "BANK_TXN": return "تراکنش بانکی";
                case "CHEQUE": return "چک";
                case "STOCK_TRANSFER": return "انتقال انبار";
                case "ADJUSTMENT": return "تسویه انبار";
                case "OPENING_BALANCE": return "افتتاحیه";
                case "EXPENSE": return "هزینه";
                case "WAGE": return "دستمزد";
                default: return "سایر";
            }
        }

        private static string StatusLabel(string value)
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
