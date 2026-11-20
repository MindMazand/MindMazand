using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Mazandasht.Desktop.Import;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Views
{
    /// <summary>
    /// Analytical reports (G10): a read-only browser over the management reports
    /// with CSV export. Every report is built from posted data only.
    /// </summary>
    public sealed class ReportsWindow : Window
    {
        private readonly string _db;
        private readonly string _userId;

        private ComboBox _reports;
        private TextBox _fromDate, _toDate;
        private TextBlock _summary;
        private DataGrid _grid;

        public ReportsWindow(string databasePath, string userId)
        {
            _db = databasePath;
            _userId = userId;
            Title = "گزارش‌های تحلیلی | مازندشت";
            Width = 1180; Height = 800;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FlowDirection = FlowDirection.RightToLeft;
            Background = ThemeService.Brush("WindowBackgroundBrush");
            Foreground = ThemeService.Brush("TextBrush");

            var root = new DockPanel { Margin = new Thickness(22) };
            var header = new TextBlock {
                Text = "گزارش‌های تحلیلی",
                FontSize = 22, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 16),
                Foreground = ThemeService.Brush("TextBrush")
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock {
                Text = "گزارش‌ها فقط از دادهٔ تأییدشده و پست‌شده خوانده می‌شوند؛ سند در وضعیت بررسی در گزارش دیده نمی‌شود.",
                TextWrapping = TextWrapping.Wrap, Foreground = ThemeService.Brush("MutedTextBrush"),
                Margin = new Thickness(4, 0, 4, 10)
            });

            var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _reports = new ComboBox { Width = 380, Height = 34, Margin = new Thickness(5),
                DisplayMemberPath = "Title", SelectedValuePath = "Code",
                ItemsSource = ReportService.Available(),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush") };
            _reports.SelectionChanged += (s, e) => Refresh();
            controls.Children.Add(_reports);
            _fromDate = Field(controls, "از تاریخ", 120);
            _toDate = Field(controls, "تا تاریخ", 120);
            _fromDate.Text = DateTime.Now.ToString("yyyy-MM-01", CultureInfo.InvariantCulture);
            _toDate.Text = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var show = Button("نمایش گزارش", Refresh);
            controls.Children.Add(show);
            var export = Button("خروجی CSV", Export);
            controls.Children.Add(export);
            panel.Children.Add(controls);

            _summary = new TextBlock { Margin = new Thickness(5), FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("MutedTextBrush") };
            panel.Children.Add(_summary);

            _grid = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true,
                CanUserAddRows = false, Margin = new Thickness(0, 8, 0, 0),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
            panel.Children.Add(_grid);

            root.Children.Add(panel);
            Content = root;
            if (_reports.Items.Count > 0) _reports.SelectedIndex = 0;
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

        private Button Button(string text, RoutedEventHandler click)
        {
            var button = new Button { Content = text, Height = 36, Margin = new Thickness(5),
                Padding = new Thickness(14, 5, 14, 5),
                Background = ThemeService.Brush("AccentBrush"),
                Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            button.Click += click;
            return button;
        }

        private void Refresh()
        {
            var definition = _reports.SelectedItem as ReportDefinition;
            if (definition == null) return;
            try
            {
                var table = ReportService.Build(_db, definition.Code,
                    _fromDate.Text.Trim(), _toDate.Text.Trim());
                _grid.ItemsSource = table.DefaultView;
                _summary.Text = definition.Title + " — " + table.Rows.Count + " ردیف";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "گزارش", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Export(object sender, RoutedEventArgs e)
        {
            var definition = _reports.SelectedItem as ReportDefinition;
            if (definition == null) return;
            try
            {
                var table = ReportService.Build(_db, definition.Code,
                    _fromDate.Text.Trim(), _toDate.Text.Trim());
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mazandasht");
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, definition.Code + "-" +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
                var rows = ExportService.ExportTableCsv(_db, definition.Code, table, path, _userId);
                MessageBox.Show(rows + " ردیف در فایل ذخیره شد:\n" + path, "مازندشت",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "خروجی CSV", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
