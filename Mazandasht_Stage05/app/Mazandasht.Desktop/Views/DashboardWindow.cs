using System;
using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Views
{
    /// <summary>
    /// Management dashboard (G10).
    ///
    /// The same window is reused as the second-monitor display: with
    /// secondDisplay = true it is maximised, topmost, read-only and refreshes
    /// itself on a timer, so the operations hall can show live KPIs without
    /// giving anyone a way to change data.
    /// </summary>
    public sealed class DashboardWindow : Window
    {
        private readonly string _db;
        private readonly string _userId;
        private readonly bool _secondDisplay;
        private readonly DispatcherTimer _timer;

        private TextBlock _inbound, _outbound, _sales, _pending, _partyBalance, _overdueCheque, _weighCount;
        private TextBlock _revenue, _grossProfit, _operatingProfit;
        private DataGrid _flow, _stock, _cheques;

        public DashboardWindow(string databasePath, string userId, bool secondDisplay)
        {
            _db = databasePath;
            _userId = userId;
            _secondDisplay = secondDisplay;

            Title = secondDisplay ? "نمایشگر دوم مازندشت" : "داشبورد مدیریتی | مازندشت";
            Width = secondDisplay ? 1400 : 1180;
            Height = secondDisplay ? 900 : 800;
            WindowStartupLocation = secondDisplay
                ? WindowStartupLocation.Manual
                : WindowStartupLocation.CenterOwner;
            FlowDirection = FlowDirection.RightToLeft;
            Background = ThemeService.Brush("WindowBackgroundBrush");
            Foreground = ThemeService.Brush("TextBrush");
            if (secondDisplay)
            {
                WindowStyle = WindowStyle.None;
                Topmost = true;
                ResizeMode = ResizeMode.NoResize;
            }

            var root = new DockPanel { Margin = new Thickness(22) };
            var header = new TextBlock {
                Text = secondDisplay ? "نمایشگر دوم — وضعیت عملیات" : "داشبورد مدیریتی",
                FontSize = 24, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 16),
                Foreground = ThemeService.Brush("TextBrush")
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock {
                Text = "همهٔ اعداد فقط از اسناد تأییدشده و المجوهای پست‌شده خوانده می‌شوند.",
                TextWrapping = TextWrapping.Wrap, Foreground = ThemeService.Brush("MutedTextBrush"),
                Margin = new Thickness(4, 0, 4, 10)
            });

            var kpis = new WrapPanel();
            _inbound = Kpi(kpis, "ورود امروز (گرم)");
            _outbound = Kpi(kpis, "خروج امروز (گرم)");
            _sales = Kpi(kpis, "فروش امروز (ریال)");
            _pending = Kpi(kpis, "سند در انتظار تأیید");
            _partyBalance = Kpi(kpis, "مانده خالص طرف‌ها (ریال)");
            _overdueCheque = Kpi(kpis, "چک سررسیدگذشته");
            _weighCount = Kpi(kpis, "تعداد باسکول امروز");
            _revenue = Kpi(kpis, "درآمد ماه (ریال)");
            _grossProfit = Kpi(kpis, "سود ناخالص ماه (ریال)");
            _operatingProfit = Kpi(kpis, "سود عملیاتی ماه (ریال)");
            panel.Children.Add(kpis);

            panel.Children.Add(new TextBlock { Text = "گردش روزانه باسکول", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 12, 0, 4) });
            _flow = Grid();
            panel.Children.Add(_flow);

            panel.Children.Add(new TextBlock { Text = "موجودی کالا", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 12, 0, 4) });
            _stock = Grid();
            panel.Children.Add(_stock);

            panel.Children.Add(new TextBlock { Text = "چک‌های در گردش", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 12, 0, 4) });
            _cheques = Grid();
            panel.Children.Add(_cheques);

            if (!secondDisplay)
            {
                var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
                var refresh = new Button { Content = "بازخوانی", Height = 36, Margin = new Thickness(5),
                    Padding = new Thickness(14, 5, 14, 5),
                    Background = ThemeService.Brush("AccentBrush"), Foreground = Brushes.White,
                    BorderThickness = new Thickness(0) };
                refresh.Click += (s, e) => RefreshAll();
                actions.Children.Add(refresh);
                var reports = new Button { Content = "گزارش‌های تحلیلی", Height = 36,
                    Margin = new Thickness(5), Padding = new Thickness(14, 5, 14, 5),
                    Background = ThemeService.Brush("PanelBackgroundBrush"),
                    Foreground = ThemeService.Brush("TextBrush"),
                    BorderBrush = ThemeService.Brush("GlassBorderBrush") };
                reports.Click += (s, e) =>
                {
                    var window = new ReportsWindow(_db, _userId) { Owner = this };
                    window.ShowDialog();
                };
                actions.Children.Add(reports);
                panel.Children.Add(actions);
            }
            root.Children.Add(panel);
            Content = root;

            if (secondDisplay)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _timer.Tick += (s, e) => RefreshAll();
                _timer.Start();
                Loaded += (s, e) => { WindowState = WindowState.Maximized; };
            }
            RefreshAll();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_timer != null) _timer.Stop();
            base.OnClosed(e);
        }

        // ------------------------------------------------------------------
        private TextBlock Kpi(Panel parent, string label)
        {
            var box = new TextBlock {
                Width = 210, Margin = new Thickness(6), Padding = new Thickness(10),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"), FontSize = 15,
                Text = label + ": —"
            };
            parent.Children.Add(box);
            return box;
        }

        private DataGrid Grid()
        {
            return new DataGrid { AutoGenerateColumns = true, IsReadOnly = true,
                CanUserAddRows = false, Margin = new Thickness(0, 8, 0, 0),
                MaxHeight = 260,
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        }

        private static string Number(long value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------
        private void RefreshAll()
        {
            try
            {
                var today = DateTime.Now;
                var snapshot = DashboardQueryService.Load(_db, today);
                _inbound.Text = "ورود امروز (گرم): " + Number(snapshot.InboundGrams);
                _outbound.Text = "خروج امروز (گرم): " + Number(snapshot.OutboundGrams);
                _sales.Text = "فروش امروز (ریال): " + Number(snapshot.SalesIrr);
                _pending.Text = "سند در انتظار تأیید: " + Number(snapshot.PendingApprovalCount);
                _partyBalance.Text = "مانده خالص طرف‌ها (ریال): " + Number(snapshot.NetPartyBalanceIrr);
                _overdueCheque.Text = "چک سررسیدگذشته: " + Number(snapshot.OverdueChequeCount);
                _weighCount.Text = "تعداد باسکول امروز: " + Number(snapshot.TodayWeighCount);

                var from = today.ToString("yyyy-MM-01", CultureInfo.InvariantCulture);
                var to = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var profit = ProfitLossService.Report(_db, from, to);
                _revenue.Text = "درآمد ماه (ریال): " + Number(profit.RevenueIrr);
                _grossProfit.Text = "سود ناخالص ماه (ریال): " + Number(profit.GrossProfitIrr);
                _operatingProfit.Text = "سود عملیاتی ماه (ریال): " + Number(profit.OperatingProfitIrr);

                _flow.ItemsSource = ReportService.Build(_db, "DAILY_FLOW", from, to).DefaultView;
                _stock.ItemsSource = ReportService.Build(_db, "STOCK_ON_HAND", null, null).DefaultView;
                _cheques.ItemsSource = ReportService.Build(_db, "CHEQUE_REGISTER", null, null).DefaultView;
            }
            catch (Exception ex)
            {
                if (!_secondDisplay)
                    MessageBox.Show(ex.Message, "داشبورد", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
