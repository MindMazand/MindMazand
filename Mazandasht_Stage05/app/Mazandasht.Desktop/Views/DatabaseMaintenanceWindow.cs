using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Mazandasht.Desktop.Database;

namespace Mazandasht.Desktop.Views
{
    public sealed class DatabaseMaintenanceWindow : Window
    {
        private readonly string _databasePath;
        private readonly string _userId;
        private readonly DataGrid _grid;
        private readonly TextBlock _summary;
        private readonly TextBox _backupPath;
        private readonly TextBox _confirmation;
        private readonly Button _resetButton;

        public bool ResetCompleted { get; private set; }

        public DatabaseMaintenanceWindow(string databasePath, string userId)
        {
            _databasePath = databasePath;
            _userId = userId;
            Title = "مدیریت پایگاه داده | مازندشت";
            Width = 920; Height = 700; MinWidth = 780; MinHeight = 580;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FlowDirection = FlowDirection.RightToLeft;
            Background = new SolidColorBrush(Color.FromRgb(11, 23, 17));
            Foreground = Brushes.White;

            var root = new DockPanel { Margin = new Thickness(20) };
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            heading.Children.Add(new TextBlock { Text = "پاکسازی کامل پایگاه داده", FontSize = 24, FontWeight = FontWeights.Bold });
            heading.Children.Add(new TextBlock { Text = "ابتدا نسخه پشتیبان مستقل ساخته می‌شود؛ سپس دیتابیس با ساختار خالی و داده‌های سیستمی ضروری جایگزین خواهد شد.",
                Margin = new Thickness(0, 6, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(169, 185, 174)), TextWrapping = TextWrapping.Wrap });
            DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);

            var footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            _summary = new TextBlock { Text = "در حال خواندن وضعیت دیتابیس...", Foreground = new SolidColorBrush(Color.FromRgb(200, 215, 204)), Margin = new Thickness(0, 0, 0, 8) };
            footer.Children.Add(_summary);

            var backupRow = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            _backupPath = new TextBox { Height = 34, IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(29, 48, 38)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(90, 125, 100)), Padding = new Thickness(8, 4, 8, 4) };
            var browse = new Button { Content = "انتخاب مسیر پشتیبان", Height = 34, Width = 150, Margin = new Thickness(0, 0, 8, 0) };
            browse.Click += BrowseBackup_Click;
            DockPanel.SetDock(browse, Dock.Left); backupRow.Children.Add(browse); backupRow.Children.Add(_backupPath);
            footer.Children.Add(new TextBlock { Text = "فایل پشتیبان قبل از پاکسازی (الزامی)", Foreground = new SolidColorBrush(Color.FromRgb(169, 185, 174)), FontSize = 11 });
            footer.Children.Add(backupRow);

            footer.Children.Add(new TextBlock { Text = "برای فعال‌شدن عملیات، عبارت زیر را دقیقاً وارد کنید: " + DatabaseResetService.ConfirmationPhrase,
                Foreground = new SolidColorBrush(Color.FromRgb(238, 178, 106)), Margin = new Thickness(0, 8, 0, 5), FontWeight = FontWeights.SemiBold });
            _confirmation = new TextBox { Height = 36, Padding = new Thickness(8, 4, 8, 4), Background = new SolidColorBrush(Color.FromRgb(29, 48, 38)),
                Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(90, 125, 100)) };
            _confirmation.TextChanged += (s, e) => UpdateResetButton();
            footer.Children.Add(_confirmation);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            var close = new Button { Content = "بستن", Height = 40, Width = 100, Margin = new Thickness(0, 0, 8, 0) };
            close.Click += (s, e) => Close();
            _resetButton = new Button { Content = "پشتیبان‌گیری و پاکسازی کامل", Height = 40, Width = 245, IsEnabled = false,
                Background = new SolidColorBrush(Color.FromRgb(166, 62, 48)), Foreground = Brushes.White, FontWeight = FontWeights.Bold };
            _resetButton.Click += Reset_Click;
            buttons.Children.Add(close); buttons.Children.Add(_resetButton); footer.Children.Add(buttons);
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

            _grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = true, CanUserAddRows = false,
                Background = new SolidColorBrush(Color.FromRgb(18, 33, 24)), Foreground = Brushes.White,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            root.Children.Add(_grid);
            Content = root;
            Loaded += (s, e) => LoadPreview();
        }

        private void LoadPreview()
        {
            try
            {
                var preview = DatabaseResetService.GetPreview(_databasePath);
                _grid.ItemsSource = preview.Tables;
                _summary.Text = "نسخه ساختار: " + preview.SchemaVersion + "  |  حجم فایل: " + (preview.DatabaseBytes / 1024.0).ToString("N1") + " KB  |  مجموع رکوردهای تمام جداول: " + preview.TotalRows.ToString("N0") +
                    "\nپس از پاکسازی: تمام کاربران، اشخاص، حساب‌های بانکی واقعی، اسناد، تراکنش‌ها، موجودی‌ها، فایل‌های ورودی و سوابق حذف می‌شوند؛ ساختار برنامه، نقش‌ها/مجوزها، فهرست نمونه بانک‌ها و انواع عملیات پایه بازسازی می‌شوند.";
            }
            catch (Exception ex)
            {
                _summary.Text = "خواندن وضعیت دیتابیس ناموفق بود: " + ex.Message;
                _resetButton.IsEnabled = false;
            }
        }

        private void BrowseBackup_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "ذخیره نسخه پشتیبان پیش از پاکسازی",
                Filter = "SQLite Database Backup (*.db)|*.db",
                FileName = "Mazandasht_Backup_BeforeReset_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".db",
                AddExtension = true,
                OverwritePrompt = false,
                CheckPathExists = true
            };
            if (dialog.ShowDialog(this) == true)
            {
                if (File.Exists(dialog.FileName))
                {
                    MessageBox.Show("این فایل از قبل وجود دارد؛ برای جلوگیری از بازنویسی نسخه پشتیبان، نام دیگری انتخاب کنید.",
                        "فایل پشتیبان موجود است", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _backupPath.Text = dialog.FileName;
                UpdateResetButton();
            }
        }

        private void UpdateResetButton()
        {
            _resetButton.IsEnabled = !String.IsNullOrWhiteSpace(_backupPath.Text) &&
                String.Equals(_confirmation.Text, DatabaseResetService.ConfirmationPhrase, StringComparison.Ordinal);
        }

        private async void Reset_Click(object sender, RoutedEventArgs e)
        {
            var answer = MessageBox.Show(
                "این عملیات برگشت‌ناپذیر است.\n\n" +
                "ابتدا یک نسخه پشتیبان کامل از دیتابیس فعلی ساخته می‌شود. سپس دیتابیس فعال خالی می‌شود.\n" +
                "تمام کاربران، اشخاص و اطلاعات پایه تجاری، حساب‌های بانکی واقعی، اسناد، گردش‌ها، مانده‌ها، فایل‌های صف ورود و سوابق حذف خواهند شد. فهرست نمونه نام بانک‌ها و کاتالوگ عملیات سیستمی باقی می‌مانند.\n" +
                "پس از راه‌اندازی مجدد، باید مدیر اولیه را دوباره بسازید.\n\nآیا ادامه می‌دهید؟",
                "تأیید نهایی پاکسازی کامل", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            _resetButton.IsEnabled = false;
            _summary.Text = "در حال ساخت پشتیبان، بررسی سلامت و جایگزینی دیتابیس...";
            try
            {
                var backup = _backupPath.Text;
                var confirmation = _confirmation.Text;
                await Task.Run(() => DatabaseResetService.ResetToCleanState(_databasePath, backup, _userId, confirmation));
                ResetCompleted = true;
                MessageBox.Show("پاکسازی کامل انجام شد.\n\nنسخه پشتیبان:\n" + backup +
                    "\n\nدیتابیس جدید فاقد کاربران و داده‌های عملیاتی است. نرم‌افزار بسته می‌شود؛ با اجرای مجدد، مدیر اولیه را تعریف کنید.",
                    "پاکسازی موفق", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                _summary.Text = "پاکسازی انجام نشد: " + ex.Message;
                MessageBox.Show(ex.Message, "خطا در پاکسازی", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateResetButton();
            }
        }
    }
}
