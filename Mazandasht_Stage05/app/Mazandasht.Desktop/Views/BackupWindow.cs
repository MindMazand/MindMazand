using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Views
{
    /// <summary>
    /// Backup profiles, runs, retention, verification and restore (G11).
    ///
    /// Restore is the dangerous half: it needs the manager confirmation phrase,
    /// a verified backup file and a safety copy of the current database. The
    /// button stays disabled until the phrase matches and the caller may
    /// approve a restore.
    /// </summary>
    public sealed class BackupWindow : Window
    {
        private readonly string _databasePath;
        private readonly string _userId;
        private readonly bool _canManage;

        private DataGrid _profiles;
        private DataGrid _history;
        private TextBox _profileName;
        private TextBox _destination;
        private TextBox _keepLast;
        private TextBox _keepMonthly;
        private CheckBox _onLogout;
        private CheckBox _onExit;
        private TextBox _restorePath;
        private TextBox _confirmation;
        private TextBlock _summary;
        private Button _restoreButton;

        public BackupWindow(string databasePath, string userId)
        {
            _databasePath = databasePath;
            _userId = userId;
            _canManage = HasPermission(databasePath, userId);

            Title = "پشتیبان‌گیری و بازیابی | مازندشت";
            Width = 1180; Height = 820;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FlowDirection = FlowDirection.RightToLeft;
            Background = ThemeService.Brush("WindowBackgroundBrush");
            Foreground = ThemeService.Brush("TextBrush");

            var root = new DockPanel { Margin = new Thickness(22) };
            var heading = new TextBlock { Text = "پشتیبان‌گیری و بازیابی",
                FontSize = 24, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 16),
                Foreground = ThemeService.Brush("TextBrush") };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);

            var panel = new StackPanel { Margin = new Thickness(14) };

            if (!_canManage)
            {
                panel.Children.Add(new TextBlock {
                    Text = "شما مجوز «مدیریت پشتیبان» را ندارید؛ فقط مدیر سیستم می‌تواند پشتیبان بگیرد یا بازیابی کند.",
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 10),
                    Foreground = ThemeService.Brush("DangerBrush")
                });
            }

            panel.Children.Add(Title("پروفایل‌های پشتیبان"));
            _profiles = Grid();
            panel.Children.Add(_profiles);

            var create = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            _profileName = Field(create, "نام پروفایل", 170);
            _destination = Field(create, "مسیر نگهداری", 260);
            var browse = new Button { Content = "انتخاب پوشه", Height = 34, Width = 110,
                Margin = new Thickness(5), Padding = new Thickness(8, 4, 8, 4),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                BorderBrush = ThemeService.Brush("GlassBorderBrush"), IsEnabled = _canManage };
            browse.Click += (s, e) => BrowseDestination();
            create.Children.Add(browse);
            _keepLast = Field(create, "نگهداشت نسخه‌های اخیر", 120);
            _keepMonthly = Field(create, "نگهداشت ماهانه", 120);
            _keepLast.Text = "30";
            _keepMonthly.Text = "3";
            _onLogout = new CheckBox { Content = "پشتیبان هنگام خروج کاربر",
                Margin = new Thickness(8, 10, 8, 0), IsEnabled = _canManage,
                Foreground = ThemeService.Brush("TextBrush") };
            _onExit = new CheckBox { Content = "پشتیبان هنگام بستن برنامه",
                Margin = new Thickness(8, 4, 8, 0), IsEnabled = _canManage,
                Foreground = ThemeService.Brush("TextBrush") };
            create.Children.Add(_onLogout);
            create.Children.Add(_onExit);
            var add = new Button { Content = "ساخت پروفایل", Height = 38, Width = 150,
                Margin = new Thickness(5, 10, 5, 0), Padding = new Thickness(10, 5, 10, 5),
                Background = ThemeService.Brush("AccentBrush"), Foreground = Brushes.White,
                BorderThickness = new Thickness(0), IsEnabled = _canManage };
            add.Click += (s, e) => CreateProfile();
            create.Children.Add(add);
            panel.Children.Add(create);

            panel.Children.Add(Title("تاریخچهٔ پشتیبان‌ها"));
            _history = Grid();
            panel.Children.Add(_history);
            var verify = new Button { Content = "تأیید سلامت نسخهٔ انتخاب‌شده", Height = 36,
                Width = 230, Margin = new Thickness(5, 8, 5, 0),
                Padding = new Thickness(10, 5, 10, 5),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                BorderBrush = ThemeService.Brush("GlassBorderBrush"), IsEnabled = _canManage };
            verify.Click += (s, e) => VerifySelected();
            panel.Children.Add(verify);

            panel.Children.Add(Title("بازیابی"));
            var restoreRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            _restorePath = Field(restoreRow, "مسیر فایل پشتیبان (.db)", 320);
            var pick = new Button { Content = "انتخاب فایل", Height = 34, Width = 110,
                Margin = new Thickness(5), Padding = new Thickness(8, 4, 8, 4),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                BorderBrush = ThemeService.Brush("GlassBorderBrush"), IsEnabled = _canManage };
            pick.Click += (s, e) => BrowseBackup();
            restoreRow.Children.Add(pick);
            _confirmation = Field(restoreRow, "عبارت تأیید بازیابی", 240);
            _confirmation.TextChanged += (s, e) => UpdateRestoreButton();
            panel.Children.Add(restoreRow);

            _restoreButton = new Button { Content = "بازیابی با تهیهٔ پشتیبان اجباری",
                Height = 42, Width = 320, Margin = new Thickness(5, 10, 5, 0),
                Padding = new Thickness(10, 5, 10, 5), IsEnabled = false,
                Background = ThemeService.Brush("DangerBrush"),
                Foreground = Brushes.White, FontWeight = FontWeights.Bold };
            _restoreButton.Click += (s, e) => Restore();
            panel.Children.Add(_restoreButton);

            _summary = new TextBlock { Margin = new Thickness(5, 12, 5, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeService.Brush("MutedTextBrush") };
            panel.Children.Add(_summary);

            root.Children.Add(panel);
            Content = root;
            Loaded += (s, e) => RefreshAll();
        }

        // ------------------------------------------------------------------
        private static bool HasPermission(string databasePath, string userId)
        {
            try
            {
                using (var connection = new System.Data.SQLite.SQLiteConnection(
                    "Data Source=" + databasePath + ";Version=3;Pooling=True;"))
                {
                    connection.Open();
                    using (var command = new System.Data.SQLite.SQLiteCommand(
                        @"SELECT COUNT(*) FROM users u
                          JOIN roles r ON r.id=u.role_id
                          JOIN role_permissions rp ON rp.role_id=r.id
                          WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
                            AND rp.permission_code='backup.manage' AND rp.granted=1;", connection))
                    {
                        command.Parameters.AddWithValue("@user", userId ?? String.Empty);
                        return Convert.ToInt32(command.ExecuteScalar()) == 1;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private TextBlock Title(string text)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.Bold,
                FontSize = 15, Margin = new Thickness(0, 14, 0, 4),
                Foreground = ThemeService.Brush("TextBrush") };
        }

        private TextBox Field(Panel parent, string label, double width)
        {
            var box = new TextBox { Width = width, Height = 34, Margin = new Thickness(5),
                VerticalContentAlignment = VerticalAlignment.Center, ToolTip = label,
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                BorderBrush = ThemeService.Brush("GlassBorderBrush") };
            parent.Children.Add(box);
            return box;
        }

        private DataGrid Grid()
        {
            return new DataGrid { AutoGenerateColumns = true, IsReadOnly = true,
                CanUserAddRows = false, MaxHeight = 220, Margin = new Thickness(0, 6, 0, 0),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        }

        // ------------------------------------------------------------------
        private void RefreshAll()
        {
            try
            {
                _profiles.ItemsSource = BackupService.ListProfiles(_databasePath);
                _history.ItemsSource = BackupService.History(_databasePath);
                _summary.Text = "پروفایل فعال یعنی پشتیبان‌گیری خودکار هنگام خروج/بستن برنامه انجام می‌شود. " +
                    "نسخهٔ پشتیبان قبل از هر بازیابی به‌صورت خودکار و با برچسب PRE_RESTORE نگهداری می‌شود.";
            }
            catch (Exception ex)
            {
                _summary.Text = "خواندن اطلاعات پشتیبان ناموفق بود: " + ex.Message;
            }
            UpdateRestoreButton();
        }

        private void CreateProfile()
        {
            try
            {
                int keepLast, keepMonthly;
                if (!Int32.TryParse(_keepLast.Text.Trim(), out keepLast)) keepLast = 30;
                if (!Int32.TryParse(_keepMonthly.Text.Trim(), out keepMonthly)) keepMonthly = 3;
                BackupService.CreateProfile(_databasePath, _profileName.Text, _destination.Text,
                    keepLast, keepMonthly, _onLogout.IsChecked == true, _onExit.IsChecked == true,
                    _userId);
                _profileName.Text = String.Empty;
                RefreshAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "ساخت پروفایل", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BrowseDestination()
        {
            var dialog = new OpenFileDialog {
                Title = "پوشهٔ نگهداری نسخه‌های پشتیبان",
                Filter = "Database Backup (*.db)|*.db",
                CheckFileExists = false,
                CheckPathExists = true
            };
            if (dialog.ShowDialog(this) == true)
                _destination.Text = Path.GetDirectoryName(dialog.FileName);
        }

        private void BrowseBackup()
        {
            var dialog = new OpenFileDialog {
                Title = "انتخاب فایل نسخه پشتیبان",
                Filter = "SQLite Database Backup (*.db)|*.db",
                CheckFileExists = true,
                CheckPathExists = true
            };
            if (dialog.ShowDialog(this) == true)
            {
                _restorePath.Text = dialog.FileName;
                UpdateRestoreButton();
            }
        }

        private void VerifySelected()
        {
            var row = _history.SelectedItem as BackupRunRow;
            if (row == null)
            {
                MessageBox.Show("یک رکورد پشتیبان انتخاب کنید.", "تأیید سلامت",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                var ok = BackupService.Verify(_databasePath, row.Id, _userId);
                MessageBox.Show(ok
                    ? "نسخه پشتیبان سالم است و وضعیت آن به VERIFIED تغییر کرد."
                    : "نسخه پشتیبان سالم نیست.",
                    "تأیید سلامت", MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
                RefreshAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "تأیید سلامت", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void UpdateRestoreButton()
        {
            _restoreButton.IsEnabled = _canManage &&
                String.Equals(_confirmation.Text, RestoreService.ConfirmationPhrase,
                    StringComparison.Ordinal) &&
                !String.IsNullOrWhiteSpace(_restorePath.Text) &&
                File.Exists(_restorePath.Text);
        }

        private void Restore()
        {
            var answer = MessageBox.Show(
                "بازیابی محتوای دیتابیس فعلی را با فایل انتخاب‌شده جایگزین می‌کند.\n\n" +
                "ابتدا به‌صورت خودکار یک نسخهٔ پشتیبان از دیتابیس جاری با برچسب PRE_RESTORE ساخته می‌شود تا بازیابی اشتباه هم بازگشت‌پذیر باشد.\n" +
                "این عمل برگشت‌ناپذیر است. آیا ادامه می‌دهید؟",
                "تأیید بازیابی", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                var safety = RestoreService.Restore(_databasePath, _restorePath.Text,
                    _userId, _confirmation.Text);
                MessageBox.Show("بازیابی انجام شد.\n\nنسخهٔ پشتیبان امنیتی قبل از بازیابی:\n" + safety,
                    "بازیابی موفق", MessageBoxButton.OK, MessageBoxImage.Information);
                _confirmation.Text = String.Empty;
                RefreshAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "خطا در بازیابی", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
