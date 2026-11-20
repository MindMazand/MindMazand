using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Views
{
    /// <summary>
    /// Accounting screen (G9): journal browser, posting of approved documents and
    /// the profit &amp; loss report. Only posted journal entries affect the
    /// balances and the report, so a REVIEW document is never reflected here.
    /// </summary>
    public sealed class AccountingWindow : Window
    {
        private readonly string _db;
        private readonly string _userId;

        private TextBox _fromDate, _toDate;
        private TextBlock _revenue, _cogs, _grossProfit, _operatingExpense, _operatingProfit;
        private DataGrid _accounts, _journals;
        private ComboBox _pendingDocuments, _approvedDocuments;

        public AccountingWindow(string databasePath, string userId)
        {
            _db = databasePath;
            _userId = userId;
            Title = "حسابداری و سود/زیان | مازندشت";
            Width = 1200; Height = 820;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FlowDirection = FlowDirection.RightToLeft;
            Background = ThemeService.Brush("WindowBackgroundBrush");
            Foreground = ThemeService.Brush("TextBrush");

            var root = new DockPanel { Margin = new Thickness(22) };
            var header = new TextBlock {
                Text = "حسابداری دوطرفه و گزارش سود/زیان",
                FontSize = 22, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 16),
                Foreground = ThemeService.Brush("TextBrush")
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock {
                Text = "فقط المجوهای تأییدشده (is_posted = 1) در مانده‌ها و گزارش سود/زیان لحاظ می‌شوند.",
                TextWrapping = TextWrapping.Wrap, Foreground = ThemeService.Brush("MutedTextBrush"),
                Margin = new Thickness(4, 0, 4, 10)
            });

            var range = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _fromDate = Field(range, "از تاریخ", 120);
            _toDate = Field(range, "تا تاریخ", 120);
            _fromDate.Text = DateTime.Now.ToString("yyyy-MM-01", CultureInfo.InvariantCulture);
            _toDate.Text = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            range.Children.Add(ActionButton("محاسبهٔ سود/زیان", RefreshReport));
            range.Children.Add(ActionButton("بازخوانی المجوها", RefreshJournals));
            panel.Children.Add(range);

            panel.Children.Add(new TextBlock { Text = "خلاصهٔ سود و زیان", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 6, 0, 4) });
            var summary = new WrapPanel();
            _revenue = SummaryBox(summary, "درآمد");
            _cogs = SummaryBox(summary, "بهای کالای فروش‌رفته");
            _grossProfit = SummaryBox(summary, "سود ناخالص");
            _operatingExpense = SummaryBox(summary, "هزینه‌های عملیاتی");
            _operatingProfit = SummaryBox(summary, "سود عملیاتی");
            panel.Children.Add(summary);

            panel.Children.Add(new TextBlock { Text = "گردش حساب‌ها", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 10, 0, 4) });
            _accounts = Grid();
            panel.Children.Add(_accounts);

            panel.Children.Add(new TextBlock { Text = "المجوهای ثبت‌شده", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 10, 0, 4) });
            _journals = Grid();
            panel.Children.Add(_journals);

            panel.Children.Add(new TextBlock { Text = "سند بدون المجو", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 10, 0, 4) });
            var pending = new WrapPanel();
            _pendingDocuments = DocumentCombo();
            pending.Children.Add(_pendingDocuments);
            pending.Children.Add(ActionButton("ساخت المجو", CreatePendingEntry));
            panel.Children.Add(pending);

            panel.Children.Add(new TextBlock { Text = "سند تأییدشده با المجوی پست‌نشده",
                FontWeight = FontWeights.Bold, Foreground = ThemeService.Brush("TextBrush"),
                Margin = new Thickness(0, 10, 0, 4) });
            var approved = new WrapPanel();
            _approvedDocuments = DocumentCombo();
            approved.Children.Add(_approvedDocuments);
            approved.Children.Add(ActionButton("پست المجو", PostSelectedDocument));
            panel.Children.Add(approved);

            root.Children.Add(panel);
            Content = root;
            RefreshReport(null, null);
            RefreshJournals(null, null);
            RefreshPending();
        }

        // ------------------------------------------------------------------
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

        private TextBlock SummaryBox(Panel parent, string label)
        {
            var box = new TextBlock {
                Width = 200, Margin = new Thickness(6), Padding = new Thickness(10),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"), FontSize = 15,
                Text = label + ": —"
            };
            parent.Children.Add(box);
            return box;
        }

        private Button ActionButton(string text, RoutedEventHandler click)
        {
            var button = new Button { Content = text, Height = 36, Margin = new Thickness(5),
                Padding = new Thickness(14, 5, 14, 5),
                Background = ThemeService.Brush("AccentBrush"),
                Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            button.Click += click;
            return button;
        }

        private DataGrid Grid()
        {
            return new DataGrid { AutoGenerateColumns = true, IsReadOnly = true,
                CanUserAddRows = false, Margin = new Thickness(0, 8, 0, 0),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        }

        private static string Money(long value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture) + " ریال";
        }

        // ------------------------------------------------------------------
        private void RefreshReport(object sender, RoutedEventArgs e)
        {
            try
            {
                var report = ProfitLossService.Report(_db, _fromDate.Text.Trim(), _toDate.Text.Trim());
                _revenue.Text = "درآمد: " + Money(report.RevenueIrr);
                _cogs.Text = "بهای کالای فروش‌رفته: " + Money(report.CogsIrr);
                _grossProfit.Text = "سود ناخالص: " + Money(report.GrossProfitIrr);
                _operatingExpense.Text = "هزینه‌های عملیاتی: " + Money(report.OperatingExpenseIrr);
                _operatingProfit.Text = "سود عملیاتی: " + Money(report.OperatingProfitIrr);
                _accounts.ItemsSource = report.Accounts;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "گزارش سود/زیان", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void RefreshJournals(object sender, RoutedEventArgs e)
        {
            _journals.ItemsSource = JournalService.List(_db);
        }

        private ComboBox DocumentCombo()
        {
            return new ComboBox { Width = 360, Height = 34, Margin = new Thickness(5),
                DisplayMemberPath = "Display", SelectedValuePath = "Id",
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush") };
        }

        private void RefreshPending()
        {
            _pendingDocuments.ItemsSource = LoadDocuments(
                "SELECT d.id,d.document_number,d.document_type FROM documents d "
                + "WHERE d.status IN ('DRAFT','REVIEW') AND d.requires_journal=1 "
                + "AND NOT EXISTS (SELECT 1 FROM journal_entries je WHERE je.document_id=d.id) "
                + "ORDER BY d.created_at_utc DESC LIMIT 300;");
            if (_pendingDocuments.Items.Count > 0) _pendingDocuments.SelectedIndex = 0;

            _approvedDocuments.ItemsSource = LoadDocuments(
                "SELECT d.id,d.document_number,d.document_type FROM documents d "
                + "JOIN journal_entries je ON je.document_id=d.id AND je.is_posted=0 "
                + "WHERE d.status='APPROVED' ORDER BY d.created_at_utc DESC LIMIT 300;");
            if (_approvedDocuments.Items.Count > 0) _approvedDocuments.SelectedIndex = 0;
        }

        private List<LookupItem> LoadDocuments(string sql)
        {
            var items = new List<LookupItem>();
            using (var connection = new SQLiteConnection(
                "Data Source=" + _db + ";Version=3;Pooling=True;"))
            {
                connection.Open();
                using (var command = new SQLiteCommand(sql, connection))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        items.Add(new LookupItem { Id = reader.GetString(0),
                            Code = reader.GetString(2), Name = reader.GetString(1) });
            }
            return items;
        }

        private void CreatePendingEntry(object sender, RoutedEventArgs e)
        {
            var item = _pendingDocuments.SelectedItem as LookupItem;
            if (item == null) { MessageBox.Show("سندی انتخاب نشده است.", "مازندشت"); return; }
            try
            {
                JournalService.EnsureEntry(_db, item.Id, _userId);
                MessageBox.Show("المجو ساخته شد.", "مازندشت", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                RefreshJournals(null, null);
                RefreshPending();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "ساخت المجو", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void PostSelectedDocument(object sender, RoutedEventArgs e)
        {
            var item = _approvedDocuments.SelectedItem as LookupItem;
            if (item == null) { MessageBox.Show("سندی انتخاب نشده است.", "مازندشت"); return; }
            try
            {
                JournalService.PostEntry(_db, item.Id, _userId);
                MessageBox.Show("المجو سند پست شد.", "مازندشت", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                RefreshJournals(null, null);
                RefreshReport(null, null);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "پست المجو", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}
