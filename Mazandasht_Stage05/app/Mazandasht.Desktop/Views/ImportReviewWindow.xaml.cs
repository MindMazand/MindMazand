using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Mazandasht.Desktop.Import;

namespace Mazandasht.Desktop.Views
{
    public partial class ImportReviewWindow : Window
    {
        private readonly string _databasePath;
        private WorkbookPreview _preview;
        private bool _loadingSheets;
        private List<ImportSourceColumn> _sourceColumns;
        private List<ImportColumnMapping> _columnMappings;
        private int _headerRowNumber;

        public ImportReviewWindow(string databasePath)
        {
            InitializeComponent();
            _databasePath = databasePath;
            ProfileCombo.ItemsSource = ImportProfileCatalog.All;
            ProfileCombo.SelectedValue = "MASTER_PARTIES";
            ProfileCombo.SelectionChanged += (s, e) => UpdateProfileDescription();
            MappingGrid.CellEditEnding += (s, e) => Dispatcher.BeginInvoke(new Action(UpdateStageAvailability));
            MappingGrid.CurrentCellChanged += (s, e) => UpdateStageAvailability();
            UpdateProfileDescription();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "انتخاب فایل Excel یا CSV",
                Filter = "Excel و CSV (*.xlsx;*.xls;*.csv)|*.xlsx;*.xls;*.csv|Excel Workbook (*.xlsx;*.xls)|*.xlsx;*.xls|CSV (*.csv)|*.csv|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;

            FilePathBox.Text = dialog.FileName;
            SheetCombo.ItemsSource = null;
            SheetCombo.IsEnabled = false;
            StageButton.IsEnabled = false;
            PreviewGrid.Columns.Clear();
            PreviewGrid.ItemsSource = null;
            MappingGrid.Columns.Clear();
            MappingGrid.ItemsSource = null;
            _sourceColumns = null;
            _columnMappings = null;
            _preview = null;
            StatusText.Text = "فایل انتخاب شد؛ برای Excel شیت و برای CSV جدول تک‌شیتی خوانده می‌شود. روی «خواندن و پیش‌نمایش» کلیک کنید.";
        }

        private async void Load_Click(object sender, RoutedEventArgs e)
        {
            if (String.IsNullOrWhiteSpace(FilePathBox.Text))
            {
                StatusText.Text = "ابتدا یک فایل انتخاب کنید.";
                return;
            }

            LoadButton.IsEnabled = false;
            StageButton.IsEnabled = false;
            StatusText.Text = "در حال خواندن فایل؛ لطفاً صبر کنید...";
            try
            {
                var path = FilePathBox.Text;
                var requestedSheet = SheetCombo.SelectedItem as string;
                var preview = await Task.Run(() => ImportWorkbookReader.ReadPreview(path, requestedSheet));
                _preview = preview;

                _loadingSheets = true;
                SheetCombo.ItemsSource = preview.Sheets.ConvertAll(s => s.Name);
                SheetCombo.SelectedItem = preview.SelectedSheet;
                SheetCombo.IsEnabled = true;
                _loadingSheets = false;

                ShowPreview(preview);
                BuildColumnMappings(preview);
                UpdateStageAvailability();
                StatusText.Text = "پیش‌نمایش Excel/CSV و نگاشت اولیه ستون‌ها آماده است؛ ستون‌های الزامی را کنترل کنید.";
            }
            catch (Exception ex)
            {
                _preview = null;
                StatusText.Text = "خطا در خواندن فایل: " + ex.Message;
            }
            finally
            {
                _loadingSheets = false;
                LoadButton.IsEnabled = true;
            }
        }

        private async void SheetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingSheets || String.IsNullOrWhiteSpace(FilePathBox.Text) || SheetCombo.SelectedItem == null) return;
            var selected = SheetCombo.SelectedItem as string;
            if (_preview != null && String.Equals(_preview.SelectedSheet, selected, StringComparison.Ordinal)) return;
            LoadButton.IsEnabled = false;
            StageButton.IsEnabled = false;
            try
            {
                var preview = await Task.Run(() => ImportWorkbookReader.ReadPreview(FilePathBox.Text, selected));
                _preview = preview;
                ShowPreview(preview);
                BuildColumnMappings(preview);
                UpdateStageAvailability();
                StatusText.Text = "پیش‌نمایش شیت «" + selected + "» و نگاشت ستون‌ها آماده است.";
            }
            catch (Exception ex)
            {
                _preview = null;
                StatusText.Text = "خطا در خواندن شیت: " + ex.Message;
            }
            finally
            {
                LoadButton.IsEnabled = true;
            }
        }

        private void ShowPreview(WorkbookPreview preview)
        {
            FileNameText.Text = preview.FileName;
            RowsCountText.Text = preview.TotalRows.ToString("N0");
            ColumnsCountText.Text = preview.ColumnCount.ToString("N0");
            HashText.Text = preview.Sha256;
            PreviewGrid.Columns.Clear();
            PreviewGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "ردیف منبع",
                Binding = new Binding("RowNumber"),
                Width = 90
            });
            for (var i = 0; i < preview.ColumnCount; i++)
            {
                PreviewGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = "ستون " + ExcelColumnName(i + 1),
                    Binding = new Binding("Cells[" + i + "]"),
                    Width = new DataGridLength(180)
                });
            }
            PreviewGrid.ItemsSource = preview.Rows;
        }

        private async void Stage_Click(object sender, RoutedEventArgs e)
        {
            if (_preview == null)
            {
                StatusText.Text = "ابتدا فایل را بخوانید و پیش‌نمایش را مشاهده کنید.";
                return;
            }

            UpdateStageAvailability();
            if (_columnMappings == null || _columnMappings.Any(x => x.Required && !x.SourceColumnIndex.HasValue))
            {
                MessageBox.Show("برای تمام فیلدهای الزامی، ستون متناظر فایل را در جدول نگاشت انتخاب کنید.", "نگاشت ناقص", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var answer = MessageBox.Show(
                "«" + _preview.FileName + "» / شیت «" + _preview.SelectedSheet + "» با " +
                (_preview.TotalRows - 1).ToString("N0") + " ردیف داده (به‌علاوه یک ردیف عنوان) در صف بررسی ذخیره می‌شود.\n\n" +
                "مقادیر خام در این مرحله به جدول عملیاتی منتقل نمی‌شوند. ادامه می‌دهید؟",
                "تأیید ذخیره پیش‌نمایش", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            StageButton.IsEnabled = false;
            LoadButton.IsEnabled = false;
            StatusText.Text = "در حال رمزگذاری و ذخیره ردیف‌ها در صف بررسی...";
            try
            {
                var preview = _preview;
                var profileCode = Convert.ToString(ProfileCombo.SelectedValue);
                var profile = ImportProfileCatalog.Find(profileCode);
                if (profile == null) throw new InvalidOperationException("نوع اطلاعات مقصد انتخاب نشده است.");
                var mappingJson = BuildMappingJson(profile.Code);
                var headerRow = _headerRowNumber;
                var batchId = await Task.Run(() => ImportStagingService.SavePreview(_databasePath, preview, profile.Code, mappingJson, headerRow));
                StatusText.Text = "بسته پیش‌نمایش برای «" + profile.DisplayName + "» ایجاد شد. شناسه: " + batchId;
                MessageBox.Show("فایل با پروفایل «" + profile.DisplayName + "» در صف بررسی رمزگذاری‌شده ذخیره شد.\nشناسه: " + batchId +
                    "\n\nاین مرحله هنوز داده را به دفاتر عملیاتی منتقل نمی‌کند؛ هیچ رکورد عملیاتی یا سند مالی ایجاد نشده است.", "مازندشت", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusText.Text = "ذخیره صف بررسی ناموفق بود: " + ex.Message;
                StageButton.IsEnabled = true;
            }
            finally
            {
                LoadButton.IsEnabled = true;
            }
        }

        private static string ExcelColumnName(int column)
        {
            var name = String.Empty;
            while (column > 0)
            {
                var remainder = (column - 1) % 26;
                name = (char)('A' + remainder) + name;
                column = (column - 1) / 26;
            }
            return name;
        }

        private void UpdateProfileDescription()
        {
            var profile = ImportProfileCatalog.Find(Convert.ToString(ProfileCombo.SelectedValue));
            ProfileDescriptionText.Text = profile == null ? "نوع اطلاعات را انتخاب کنید." : profile.Description;
            if (_preview != null)
            {
                BuildColumnMappings(_preview);
                UpdateStageAvailability();
            }
        }

        private void BuildColumnMappings(WorkbookPreview preview)
        {
            if (preview == null || preview.Rows == null || preview.Rows.Count == 0) return;
            var profile = ImportProfileCatalog.Find(Convert.ToString(ProfileCombo.SelectedValue));
            if (profile == null) return;
            _headerRowNumber = preview.Rows[0].RowNumber;
            _sourceColumns = new List<ImportSourceColumn>();
            for (var i = 0; i < preview.ColumnCount; i++)
            {
                var header = i < preview.Rows[0].Cells.Length ? (preview.Rows[0].Cells[i] ?? String.Empty).Trim() : String.Empty;
                _sourceColumns.Add(new ImportSourceColumn
                {
                    Index = i,
                    Header = header,
                    DisplayName = ExcelColumnName(i + 1) + " | " + (String.IsNullOrWhiteSpace(header) ? "(بدون عنوان)" : header)
                });
            }

            var fields = ImportProfileCatalog.GetFieldDefinitions(profile.Code);
            _columnMappings = new List<ImportColumnMapping>();
            foreach (var field in fields)
            {
                var match = _sourceColumns.FirstOrDefault(x => !String.IsNullOrWhiteSpace(x.Header) &&
                    String.Equals(x.Header.Trim(), field.TargetField.Trim(), StringComparison.OrdinalIgnoreCase));
                _columnMappings.Add(new ImportColumnMapping
                {
                    TargetField = field.TargetField,
                    Required = field.Required,
                    SourceColumnIndex = match == null ? (int?)null : match.Index,
                    SourceHeader = match == null ? null : match.Header
                });
            }

            MappingGrid.Columns.Clear();
            MappingGrid.Columns.Add(new DataGridTextColumn { Header = "فیلد مقصد نرم‌افزار", Binding = new Binding("TargetField"), IsReadOnly = true, Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            MappingGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "الزامی", Binding = new Binding("Required"), IsReadOnly = true, Width = 75 });
            MappingGrid.Columns.Add(new DataGridComboBoxColumn
            {
                Header = "ستون متناظر در Excel",
                ItemsSource = _sourceColumns,
                DisplayMemberPath = "DisplayName",
                SelectedValuePath = "Index",
                SelectedValueBinding = new Binding("SourceColumnIndex") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(3, DataGridLengthUnitType.Star)
            });
            MappingGrid.ItemsSource = _columnMappings;
        }

        private void UpdateStageAvailability()
        {
            if (StageButton == null) return;
            StageButton.IsEnabled = _preview != null && _preview.TotalRows > 1 && _columnMappings != null &&
                _columnMappings.Where(x => x.Required).All(x => x.SourceColumnIndex.HasValue);
        }

        private string BuildMappingJson(string profileCode)
        {
            var serializer = new JavaScriptSerializer();
            var fields = _columnMappings.Select(x => new
            {
                targetField = x.TargetField,
                required = x.Required,
                sourceColumnIndex = x.SourceColumnIndex,
                sourceHeader = x.SourceColumnIndex.HasValue ? _sourceColumns.FirstOrDefault(c => c.Index == x.SourceColumnIndex.Value)?.Header : null
            }).ToList();
            return serializer.Serialize(new { profileCode = profileCode, headerRowNumber = _headerRowNumber, fields = fields });
        }

        private void Template_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var profile = ImportProfileCatalog.Find(Convert.ToString(ProfileCombo.SelectedValue));
                if (profile == null) throw new InvalidOperationException("ابتدا نوع اطلاعات را انتخاب کنید.");
                var dialog = new SaveFileDialog
                {
                    Title = "ذخیره قالب Excel ماژول " + profile.DisplayName,
                    Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                    FileName = profile.TemplateFile,
                    AddExtension = true,
                    OverwritePrompt = true,
                    CheckPathExists = true
                };
                if (dialog.ShowDialog(this) != true) return;
                File.Copy(ImportProfileCatalog.TemplatePath(profile.Code), dialog.FileName, true);
                StatusText.Text = "قالب خالی Excel ذخیره شد: " + dialog.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "دریافت قالب Excel", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
    }
}
