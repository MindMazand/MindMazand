using System.Collections.Generic;

namespace Mazandasht.Desktop.Import
{
    public sealed class ImportPreviewRow
    {
        public int RowNumber { get; set; }
        public string[] Cells { get; set; }
    }

    public sealed class WorkbookSheetInfo
    {
        public string Name { get; set; }
        public int NonEmptyRows { get; set; }
        public int ColumnCount { get; set; }
    }

    public sealed class WorkbookPreview
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string Sha256 { get; set; }
        public long SizeBytes { get; set; }
        public List<WorkbookSheetInfo> Sheets { get; set; }
        public List<ImportPreviewRow> Rows { get; set; }
        public int TotalRows { get; set; }
        public int ColumnCount { get; set; }
        public string SelectedSheet { get; set; }
    }
}
