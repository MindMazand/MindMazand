using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mazandasht.Desktop.Import;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Views
{
    public partial class ImportQueueWindow : Window
    {
        private readonly string _databasePath;
        private readonly string _userId;

        public ImportQueueWindow(string databasePath)
        {
            InitializeComponent();
            _databasePath = databasePath;
            _userId = AppSession.CurrentUser == null ? String.Empty : AppSession.CurrentUser.UserId;
            RefreshBatches();
        }

        private void RefreshBatches()
        {
            BatchGrid.ItemsSource = ImportQueueService.ListBatches(_databasePath);
            RowGrid.ItemsSource = null;
            StatusText.Text = "بسته‌های ورود اطلاعات نمایش داده شد. برای دیدن جزئیات، یک بسته را انتخاب کنید.";
        }

        private void BatchGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var batch = BatchGrid.SelectedItem as ImportBatchRow;
            if (batch == null) return;
            try
            {
                var summary = ImportQueueService.GetSummary(_databasePath, batch.Id);
                RowGrid.ItemsSource = summary.Rows;
                ValidateButton.IsEnabled = summary.Batch.Status == "پیش‌نمایش";
                CommitButton.IsEnabled = summary.Batch.Status == "پیش‌نمایش";
                CancelButton.IsEnabled = summary.Batch.Status == "پیش‌نمایش";
                StatusText.Text = "بسته «" + summary.Batch.SourceFileName + "» | مقصد: " +
                    summary.Batch.TargetKind + " | فیلدهای نگاشت‌شده: " + summary.MappedFieldCount +
                    " | فیلدهای الزامی بدون نگاشت: " + summary.MissingRequiredFieldCount +
                    (summary.MissingRequiredFieldCount > 0 ? " (این بسته قابل انتقال نیست)" : "");
            }
            catch (Exception ex)
            {
                StatusText.Text = "خواندن جزئیات بسته ناموفق بود: " + ex.Message;
            }
        }

        private void Validate_Click(object sender, RoutedEventArgs e)
        {
            var batch = BatchGrid.SelectedItem as ImportBatchRow;
            if (batch == null) { StatusText.Text = "ابتدا یک بسته انتخاب کنید."; return; }
            try
            {
                var result = ImportCommitService.Validate(_databasePath, batch.Id, _userId);
                StatusText.Text = "اعتبارسنجی بسته: کل " + result.TotalRows + " | قابل انتقال " +
                    result.ImportedRows + " | ردشده " + result.RejectedRows + " | تکراری " + result.DuplicateRows;
                MessageBox.Show(StatusText.Text + "\n\nاعتبارسنجی هیچ داده‌ای ثبت نکرد.", "اعتبارسنجی",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshBatches();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "اعتبارسنجی", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Commit_Click(object sender, RoutedEventArgs e)
        {
            var batch = BatchGrid.SelectedItem as ImportBatchRow;
            if (batch == null) { StatusText.Text = "ابتدا یک بسته انتخاب کنید."; return; }
            var answer = MessageBox.Show(
                "بسته «" + batch.SourceFileName + "» به جداول عملیاتی منتقل می‌شود.\n\n" +
                "اسناد ورودی در وضعیت «پیش‌نویس» ثبت می‌شوند و تا تأیید مدیر و اجرای موتور ماژول، هیچ اثر مالی یا انباری ندارند.\n\nادامه می‌دهید؟",
                "تأیید انتقال", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                var result = ImportCommitService.Commit(_databasePath, batch.Id, _userId);
                StatusText.Text = "انتقال انجام شد: قابل انتقال " + result.ImportedRows +
                    " | ردشده " + result.RejectedRows + " | تکراری " + result.DuplicateRows;
                MessageBox.Show(StatusText.Text, "انتقال به جداول عملیاتی",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshBatches();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "انتقال", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            var batch = BatchGrid.SelectedItem as ImportBatchRow;
            if (batch == null) { StatusText.Text = "ابتدا یک بسته انتخاب کنید."; return; }
            if (MessageBox.Show("بسته «" + batch.SourceFileName + "» لغو شود؟", "لغو بسته",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                var cancelled = ImportQueueService.CancelBatch(_databasePath, batch.Id);
                StatusText.Text = cancelled ? "بسته لغو شد." : "این بسته در وضعیت پیش‌نمایش نیست و لغو نشد.";
                RefreshBatches();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "لغو بسته", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "ذخیره خروجی CSV اسناد",
                Filter = "CSV (*.csv)|*.csv",
                FileName = "mazandasht-documents.csv",
                AddExtension = true,
                OverwritePrompt = true,
                CheckPathExists = true
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var count = ExportService.ExportDocumentsCsv(_databasePath, null, dialog.FileName, _userId);
                StatusText.Text = "خروجی CSV با " + count.ToString("N0") + " ردیف ذخیره شد: " + dialog.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "خروجی CSV", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) { RefreshBatches(); }
    }
}
