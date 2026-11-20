using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ExcelDataReader;

namespace Mazandasht.Desktop.Import
{
    public static class ImportWorkbookReader
    {
        public const int MaximumRowsPerSheet = 10000;
        public const int MaximumPreviewRows = 250;
        public const int MaximumColumns = 64;
        private const long MaximumFileBytes = 50L * 1024L * 1024L;

        public static WorkbookPreview ReadPreview(string path, string selectedSheet)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("فایل انتخاب‌شده پیدا نشد.", path);

            var info = new FileInfo(path);
            if (info.Length > MaximumFileBytes)
                throw new InvalidOperationException("حجم فایل بیش از سقف ۵۰ مگابایت است.");

            if (String.Equals(info.Extension, ".csv", StringComparison.OrdinalIgnoreCase))
                return ReadCsvPreview(path, info);

            var result = new WorkbookPreview
            {
                FilePath = path,
                FileName = info.Name,
                SizeBytes = info.Length,
                Sha256 = ComputeSha256(path),
                Sheets = new List<WorkbookSheetInfo>(),
                Rows = new List<ImportPreviewRow>()
            };

            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            {
                do
                {
                    var sheetName = reader.Name ?? "Sheet";
                    var sheetRows = 0;
                    var columns = Math.Min(reader.FieldCount, MaximumColumns);
                    if (reader.FieldCount > MaximumColumns)
                        throw new InvalidOperationException("فایل بیش از ۶۴ ستون دارد؛ ستون‌های غیرضروری را حذف کنید.");
                    while (reader.Read())
                    {
                        var row = ReadCells(reader, columns);
                        if (IsEmpty(row)) continue;
                        sheetRows++;
                        if (sheetRows > MaximumRowsPerSheet)
                            throw new InvalidOperationException("تعداد ردیف‌های غیرخالی شیت از سقف ۱۰٬۰۰۰ ردیف بیشتر است.");
                    }
                    result.Sheets.Add(new WorkbookSheetInfo
                    {
                        Name = sheetName,
                        NonEmptyRows = sheetRows,
                        ColumnCount = columns
                    });
                }
                while (reader.NextResult());
            }

            if (String.IsNullOrWhiteSpace(selectedSheet))
                selectedSheet = result.Sheets.Count == 0 ? null : result.Sheets[0].Name;
            if (selectedSheet == null)
                throw new InvalidOperationException("هیچ شیت قابل خواندنی در فایل پیدا نشد.");

            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            {
                do
                {
                    if (!String.Equals(reader.Name, selectedSheet, StringComparison.Ordinal)) continue;
                    var rowNumber = 0;
                    var total = 0;
                    var columns = Math.Min(reader.FieldCount, MaximumColumns);
                    while (reader.Read())
                    {
                        rowNumber++;
                        var cells = ReadCells(reader, columns);
                        if (IsEmpty(cells)) continue;
                        total++;
                        if (total <= MaximumPreviewRows)
                            result.Rows.Add(new ImportPreviewRow { RowNumber = rowNumber, Cells = cells });
                        if (total > MaximumRowsPerSheet)
                            throw new InvalidOperationException("تعداد ردیف‌های غیرخالی شیت از سقف ۱۰٬۰۰۰ ردیف بیشتر است؛ فایل را به بخش‌های کوچک‌تر تقسیم کنید.");
                    }
                    result.TotalRows = total;
                    result.ColumnCount = columns;
                    result.SelectedSheet = selectedSheet;
                    break;
                }
                while (reader.NextResult());
            }

            if (result.SelectedSheet == null)
                throw new InvalidOperationException("شیت انتخاب‌شده در فایل پیدا نشد.");
            return result;
        }

        public static IEnumerable<ImportPreviewRow> ReadAllRows(string path, string selectedSheet, int skipRowNumber = 0)
        {
            if (String.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
            {
                var csvRows = ReadCsvRows(path);
                for (var i = 0; i < csvRows.Count; i++)
                {
                    var rowNumber = i + 1;
                    var cells = csvRows[i];
                    if (!IsEmpty(cells) && rowNumber != skipRowNumber)
                        yield return new ImportPreviewRow { RowNumber = rowNumber, Cells = cells };
                }
                yield break;
            }

            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            {
                do
                {
                    if (!String.Equals(reader.Name, selectedSheet, StringComparison.Ordinal)) continue;
                    var rowNumber = 0;
                    var columns = Math.Min(reader.FieldCount, MaximumColumns);
                    if (reader.FieldCount > MaximumColumns)
                        throw new InvalidOperationException("فایل بیش از ۶۴ ستون دارد.");
                    while (reader.Read())
                    {
                        rowNumber++;
                        var cells = ReadCells(reader, columns);
                        if (!IsEmpty(cells) && rowNumber != skipRowNumber)
                            yield return new ImportPreviewRow { RowNumber = rowNumber, Cells = cells };
                    }
                    yield break;
                }
                while (reader.NextResult());
            }
            throw new InvalidOperationException("شیت انتخاب‌شده در فایل پیدا نشد.");
        }

        private static WorkbookPreview ReadCsvPreview(string path, FileInfo info)
        {
            var allRows = ReadCsvRows(path);
            var nonEmpty = new List<ImportPreviewRow>();
            var maxColumns = 0;
            for (var i = 0; i < allRows.Count; i++)
            {
                var cells = allRows[i];
                if (IsEmpty(cells)) continue;
                if (cells.Length > MaximumColumns)
                    throw new InvalidOperationException("فایل CSV بیش از ۶۴ ستون دارد؛ ستون‌های غیرضروری را حذف کنید.");
                maxColumns = Math.Max(maxColumns, cells.Length);
                nonEmpty.Add(new ImportPreviewRow { RowNumber = i + 1, Cells = cells });
                if (nonEmpty.Count > MaximumRowsPerSheet)
                    throw new InvalidOperationException("تعداد ردیف‌های غیرخالی CSV از سقف ۱۰٬۰۰۰ ردیف بیشتر است.");
            }
            if (nonEmpty.Count == 0)
                throw new InvalidOperationException("فایل CSV هیچ ردیف غیرخالی ندارد.");

            var preview = new WorkbookPreview
            {
                FilePath = path,
                FileName = info.Name,
                SizeBytes = info.Length,
                Sha256 = ComputeSha256(path),
                Sheets = new List<WorkbookSheetInfo>
                {
                    new WorkbookSheetInfo { Name = "CSV", NonEmptyRows = nonEmpty.Count, ColumnCount = maxColumns }
                },
                Rows = new List<ImportPreviewRow>(),
                TotalRows = nonEmpty.Count,
                ColumnCount = maxColumns,
                SelectedSheet = "CSV"
            };
            for (var i = 0; i < Math.Min(MaximumPreviewRows, nonEmpty.Count); i++)
                preview.Rows.Add(nonEmpty[i]);
            return preview;
        }

        private static List<string[]> ReadCsvRows(string path)
        {
            var bytes = File.ReadAllBytes(path);
            string content;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                content = new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                content = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                content = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            else
            {
                try { content = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { content = Encoding.GetEncoding(1256).GetString(bytes); }
            }

            var delimiter = DetectDelimiter(content);
            var rows = new List<string[]>();
            var row = new List<string>();
            var cell = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < content.Length; i++)
            {
                var ch = content[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < content.Length && content[i + 1] == '"')
                        {
                            cell.Append('"');
                            i++;
                        }
                        else inQuotes = false;
                    }
                    else cell.Append(ch);
                    continue;
                }

                if (ch == '"' && cell.Length == 0) inQuotes = true;
                else if (ch == delimiter)
                {
                    row.Add(cell.ToString());
                    cell.Clear();
                }
                else if (ch == '\r' || ch == '\n')
                {
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row.ToArray());
                    row.Clear();
                    if (ch == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                }
                else cell.Append(ch);
            }

            if (inQuotes) throw new InvalidDataException("ساختار نقل‌قول‌ها در فایل CSV ناقص است.");
            if (cell.Length > 0 || row.Count > 0)
            {
                row.Add(cell.ToString());
                rows.Add(row.ToArray());
            }
            return rows;
        }

        private static char DetectDelimiter(string text)
        {
            var candidates = new[] { ',', ';', '\t' };
            var counts = new int[candidates.Length];
            var inQuotes = false;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                    inQuotes = !inQuotes;
                }
                else if (!inQuotes && (ch == '\r' || ch == '\n')) break;
                else if (!inQuotes)
                    for (var j = 0; j < candidates.Length; j++)
                        if (ch == candidates[j]) counts[j]++;
            }
            var best = 0;
            for (var i = 1; i < counts.Length; i++)
                if (counts[i] > counts[best]) best = i;
            return counts[best] == 0 ? ',' : candidates[best];
        }

        private static string[] ReadCells(IExcelDataReader reader, int columns)
        {
            var values = new string[columns];
            for (var i = 0; i < columns; i++)
            {
                var value = reader.GetValue(i);
                if (value == null || value == DBNull.Value) values[i] = String.Empty;
                else if (value is DateTime) values[i] = ((DateTime)value).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                else if (value is IFormattable) values[i] = ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
                else values[i] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty;
            }
            return values;
        }

        private static bool IsEmpty(string[] cells)
        {
            foreach (var value in cells)
                if (!String.IsNullOrWhiteSpace(value)) return false;
            return true;
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", String.Empty).ToLowerInvariant();
            }
        }
    }
}
