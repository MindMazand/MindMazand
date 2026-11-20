using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Mazandasht.Desktop.Services;
using Mazandasht.Desktop.Views;

namespace Mazandasht.Desktop
{
    public partial class MainWindow : Window
    {
        private bool _showToman;
        private readonly string _databasePath;

        public MainWindow(string databasePath)
        {
            InitializeComponent();
            _databasePath = databasePath;
            UserNameText.Text = AppSession.CurrentUser == null ? "کاربر" : AppSession.CurrentUser.DisplayName;
            var isAdmin = AppSession.CurrentUser != null &&
                String.Equals(AppSession.CurrentUser.RoleCode, "ADMIN", StringComparison.OrdinalIgnoreCase);
            DatabaseMaintenanceButton.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
            var canImport = isAdmin || (AppSession.CurrentUser != null &&
                String.Equals(AppSession.CurrentUser.RoleCode, "MANAGER", StringComparison.OrdinalIgnoreCase));
            ImportButton.Visibility = canImport ? Visibility.Visible : Visibility.Collapsed;
            DateText.Text = GetPersianDate();
            SalesUnitText.Text = "ریال | سند قطعی";
            PartyBalanceUnitText.Text = "ریال | دفتر کل";
            RefreshDashboard();
            Closing += MainWindow_Closing;
        }

        /// <summary>
        /// Exit backups run while the window is closing. A failure is reported
        /// but never blocks the operator from leaving.
        /// </summary>
        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var user = AppSession.CurrentUser;
            if (user == null) return;
            try
            {
                var failures = BackupService.RunExitBackups(_databasePath, user.UserId);
                if (failures.Count > 0)
                    MessageBox.Show("برخی نسخه‌های پشتیبان هنگام خروج ساخته نشد:\n" +
                        String.Join("\n", failures.ToArray()),
                        "پشتیبان‌گیری خروج", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show("پشتیبان‌گیری خروج انجام نشد: " + ex.Message,
                    "پشتیبان‌گیری خروج", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OpenBackup_Click(object sender, RoutedEventArgs e)
        {
            var user = AppSession.CurrentUser;
            if (user == null) return;
            var window = new Views.BackupWindow(_databasePath, user.UserId) { Owner = this };
            window.ShowDialog();
        }

        private void OpenSecurity_Click(object sender, RoutedEventArgs e)
        {
            var user = AppSession.CurrentUser;
            if (user == null) return;
            var window = new Views.SecurityWindow(_databasePath, user.UserId) { Owner = this };
            window.ShowDialog();
        }

        /// <summary>
        /// Logs the operator out: the logout backups run first, then the session
        /// is dropped and the application restarts on the login screen.
        /// </summary>
        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            var user = AppSession.CurrentUser;
            if (user == null) return;
            var answer = MessageBox.Show("خروج از حساب کاربری؟\n\nقبل از خروج، نسخه‌های پشتیبان " +
                "پروفایل‌های فعال با پرچم «پشتیبان هنگام خروج کاربر» ساخته می‌شوند.",
                "خروج از حساب کاربری", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                var failures = BackupService.RunLogoutBackups(_databasePath, user.UserId);
                if (failures.Count > 0)
                    MessageBox.Show("برخی نسخه‌های پشتیبان قبل از خروج ساخته نشد:\n" +
                        String.Join("\n", failures.ToArray()),
                        "پشتیبان‌گیری خروج", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show("پشتیبان‌گیری قبل از خروج انجام نشد: " + ex.Message,
                    "پشتیبان‌گیری خروج", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            AppSession.CurrentUser = null;
            Application.Current.Shutdown();
        }

        private static string GetPersianDate()
        {
            var pc = new PersianCalendar();
            var now = DateTime.Now;
            return pc.GetYear(now).ToString("0000") + "/" +
                   pc.GetMonth(now).ToString("00") + "/" +
                   pc.GetDayOfMonth(now).ToString("00");
        }

        private void ToggleCurrency_Click(object sender, RoutedEventArgs e)
        {
            _showToman = !_showToman;
            CurrencyButton.Content = _showToman ? "نمایش ریال" : "نمایش تومان";
            SalesUnitText.Text = _showToman ? "تومان | سند قطعی" : "ریال | سند قطعی";
            PartyBalanceUnitText.Text = _showToman ? "تومان | دفتر کل" : "ریال | دفتر کل";
            RefreshDashboard();
        }

        private void OpenQuickEntry_Click(object sender, RoutedEventArgs e)
        {
            QuickEntryOverlay.Visibility = Visibility.Visible;
        }

        private void OpenMasterData_Click(object sender, RoutedEventArgs e)
        {
            var user = AppSession.CurrentUser;
            if (user == null || (user.RoleCode != "ADMIN" && user.RoleCode != "MANAGER"))
            {
                MessageBox.Show("مدیریت اطلاعات پایه فقط برای مدیر سیستم یا مدیر مجموعه مجاز است.",
                    "محدودیت دسترسی", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var window = new MasterDataWindow(_databasePath, user == null ? String.Empty : user.UserId) { Owner = this };
            window.ShowDialog();
            RefreshDashboard();
        }

        private void OpenApproval_Click(object sender, RoutedEventArgs e)
        {
            var user = AppSession.CurrentUser;
            if (user == null || (user.RoleCode != "ADMIN" && user.RoleCode != "MANAGER"))
            {
                MessageBox.Show("فقط مدیر سیستم یا مدیر مجموعه مجاز به بررسی و تأیید اسناد است.",
                    "محدودیت دسترسی", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var window = new ApprovalReviewWindow(_databasePath, user.UserId) { Owner = this };
            window.ShowDialog();
            RefreshDashboard();
        }

        private void OpenWeighing_Click(object sender, RoutedEventArgs e)
        {
            QuickEntryOverlay.Visibility = Visibility.Collapsed;
            var user = AppSession.CurrentUser;
            if (user == null || (user.RoleCode != "ADMIN" && user.RoleCode != "MANAGER" && user.RoleCode != "SCALE"))
            {
                MessageBox.Show("مجوز ثبت باسکول برای این کاربر تعریف نشده است.",
                    "محدودیت دسترسی", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var window = new WeighingEntryWindow(_databasePath, user == null ? String.Empty : user.UserId) { Owner = this };
            if (window.ShowDialog() == true) RefreshDashboard();
        }

        private void RefreshDashboard()
        {
            try
            {
                var snapshot = DashboardQueryService.Load(_databasePath, DateTime.Now);
                InboundKpiText.Text = (snapshot.InboundGrams / 1000.0).ToString("#,##0.0", CultureInfo.GetCultureInfo("fa-IR"));
                OutboundKpiText.Text = (snapshot.OutboundGrams / 1000.0).ToString("#,##0.0", CultureInfo.GetCultureInfo("fa-IR"));
                SalesKpiText.Text = FormatMoney(snapshot.SalesIrr);
                PartyBalanceKpiText.Text = FormatMoney(snapshot.NetPartyBalanceIrr);
                PendingKpiText.Text = snapshot.PendingApprovalCount.ToString("N0", CultureInfo.GetCultureInfo("fa-IR"));
                OverdueChequeKpiText.Text = snapshot.OverdueChequeCount.ToString("N0", CultureInfo.GetCultureInfo("fa-IR"));
                DrawTrendChart(DashboardQueryService.LoadDailyFlow(_databasePath, DateTime.Now, 7));
            }
            catch
            {
                InboundKpiText.Text = OutboundKpiText.Text = SalesKpiText.Text = PartyBalanceKpiText.Text = "—";
                PendingKpiText.Text = OverdueChequeKpiText.Text = "—";
                TrendChartCanvas.Children.Clear();
                TrendChartCanvas.Children.Add(new TextBlock { Text="داده تأییدشده‌ای برای نمودار در دسترس نیست", Foreground=Brushes.Gray });
            }
        }

        private void DrawTrendChart(System.Collections.Generic.IList<DailyFlowPoint> points)
        {
            TrendChartCanvas.Children.Clear();
            if (points == null || points.Count == 0) return;
            double width = Math.Max(360, TrendChartCanvas.ActualWidth);
            double height = 120;
            long max = 0;
            foreach (var p in points) max = Math.Max(max, Math.Max(p.InboundGrams,p.OutboundGrams));
            if (max <= 0)
            {
                TrendChartCanvas.Children.Add(new TextBlock { Text="در بازه انتخاب‌شده سند باسکول تأییدشده‌ای وجود ندارد", Foreground=new SolidColorBrush(Color.FromRgb(169,185,174)), FontSize=11 });
                return;
            }
            double groupWidth = width / points.Count;
            double barWidth = Math.Min(15, groupWidth * 0.28);
            for (int i=0;i<points.Count;i++)
            {
                var point=points[i];
                double x=i*groupWidth + groupWidth/2;
                double inHeight=point.InboundGrams/(double)max*78;
                double outHeight=point.OutboundGrams/(double)max*78;
                var inbound=new Rectangle { Width=barWidth, Height=Math.Max(1,inHeight), RadiusX=3, RadiusY=3, Fill=new SolidColorBrush(Color.FromRgb(104,148,56)) };
                var outbound=new Rectangle { Width=barWidth, Height=Math.Max(1,outHeight), RadiusX=3, RadiusY=3, Fill=new SolidColorBrush(Color.FromRgb(212,105,9)) };
                Canvas.SetLeft(inbound,x-barWidth-2); Canvas.SetTop(inbound,82-inHeight);
                Canvas.SetLeft(outbound,x+2); Canvas.SetTop(outbound,82-outHeight);
                TrendChartCanvas.Children.Add(inbound); TrendChartCanvas.Children.Add(outbound);
                var label=new TextBlock { Text=point.DateIso.Substring(5), FontSize=9, Foreground=new SolidColorBrush(Color.FromRgb(169,185,174)), Width=groupWidth, TextAlignment=TextAlignment.Center };
                Canvas.SetLeft(label,i*groupWidth); Canvas.SetTop(label,88); TrendChartCanvas.Children.Add(label);
            }
            var legendIn=new TextBlock { Text="■ ورودی", Foreground=new SolidColorBrush(Color.FromRgb(104,148,56)), FontSize=9 };
            var legendOut=new TextBlock { Text="■ خروجی", Foreground=new SolidColorBrush(Color.FromRgb(212,105,9)), FontSize=9 };
            Canvas.SetLeft(legendIn,8); Canvas.SetTop(legendIn,0);
            Canvas.SetLeft(legendOut,72); Canvas.SetTop(legendOut,0);
            TrendChartCanvas.Children.Add(legendIn); TrendChartCanvas.Children.Add(legendOut);
        }

        private string FormatMoney(long amountIrr)
        {
            var shown = _showToman ? amountIrr / 10m : amountIrr;
            var unit = _showToman ? " تومان" : " ریال";
            return shown.ToString("#,##0", CultureInfo.GetCultureInfo("fa-IR")) + unit;
        }

        private void DatabaseMaintenance_Click(object sender, RoutedEventArgs e)
        {
            var user = AppSession.CurrentUser;
            if (user == null || !String.Equals(user.RoleCode, "ADMIN", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("مدیریت و پاکسازی کامل پایگاه داده فقط برای مدیر سیستم مجاز است.",
                    "محدودیت دسترسی", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var window = new DatabaseMaintenanceWindow(_databasePath, user.UserId) { Owner = this };
            if (window.ShowDialog() == true && window.ResetCompleted)
                Application.Current.Shutdown();
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var role = AppSession.CurrentUser == null ? String.Empty : AppSession.CurrentUser.RoleCode;
            if (!String.Equals(role, "ADMIN", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(role, "MANAGER", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("ورود فایل‌های منبع فقط برای مدیر سیستم یا مدیر مجموعه مجاز است.",
                    "محدودیت دسترسی", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var window = new ImportReviewWindow(_databasePath) { Owner = this };
            window.ShowDialog();
        }

        private void CloseQuickEntry_Click(object sender, RoutedEventArgs e)
        {
            QuickEntryOverlay.Visibility = Visibility.Collapsed;
        }

        private void OpenAccounting_Click(object sender, RoutedEventArgs e)
        {
            var user = AppSession.CurrentUser;
            if (user == null) return;
            var window = new AccountingWindow(_databasePath, user.UserId) { Owner = this };
            window.ShowDialog();
        }

        private void OpenDashboard_Click(object sender, RoutedEventArgs e)
        {
            var user = AppSession.CurrentUser;
            if (user == null) return;
            var window = new Views.DashboardWindow(_databasePath, user.UserId, false) { Owner = this };
            window.ShowDialog();
        }

        private void OpenReports_Click(object sender, RoutedEventArgs e)
        {
            var user = AppSession.CurrentUser;
            if (user == null) return;
            var window = new Views.ReportsWindow(_databasePath, user.UserId) { Owner = this };
            window.ShowDialog();
        }

        /// <summary>
        /// The second display is a read-only, self-refreshing window: it opens
        /// maximised and stays on top so it can sit on the operations-hall screen.
        /// </summary>
        private void OpenSecondDisplay_Click(object sender, RoutedEventArgs e)
        {
            var user = AppSession.CurrentUser;
            if (user == null) return;
            var window = new Views.DashboardWindow(_databasePath, user.UserId, true);
            window.Show();
        }

        private void OpenFinanceModules_Click(object sender, RoutedEventArgs e)
        {
            var user = AppSession.CurrentUser;
            if (user == null) return;
            var window = new FinanceModulesWindow(_databasePath, user.UserId) { Owner = this };
            window.ShowDialog();
        }

        private void OpenOperationalModules_Click(object sender, RoutedEventArgs e)
        {
            var user = AppSession.CurrentUser;
            if (user == null) return;
            var window = new OperationalModulesWindow(_databasePath, user.UserId) { Owner = this };
            window.ShowDialog();
        }

        private void OpenImportQueue_Click(object sender, RoutedEventArgs e)
        {
            var window = new ImportQueueWindow(_databasePath) { Owner = this };
            window.ShowDialog();
        }

        private void OpenBaseData_Click(object sender, RoutedEventArgs e)
        {
            var user = AppSession.CurrentUser;
            if (user == null) return;
            var window = new BaseDataWindow(_databasePath, user.UserId) { Owner = this };
            window.ShowDialog();
        }

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            var user = AppSession.CurrentUser;
            var window = new ThemePreviewWindow(
                _databasePath,
                user == null ? String.Empty : user.UserId,
                ThemeService.Current) { Owner = this };
            window.ShowDialog();
        }

        private void Navigation_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;
            var page = Convert.ToString(button.Tag);
            PageTitleText.Text = String.IsNullOrWhiteSpace(page) ? "مازندشت" : page;
            PageSubtitleText.Text = "این ماژول در حال توسعه است؛ تا زمان اتصال داده واقعی، عدد یا گزارش ساختگی نمایش داده نمی‌شود.";
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}