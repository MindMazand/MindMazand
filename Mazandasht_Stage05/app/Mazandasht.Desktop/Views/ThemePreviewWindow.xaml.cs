using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Views
{
    public partial class ThemePreviewWindow : Window
    {
        private readonly string _databasePath;
        private readonly string _userId;
        private AppTheme _selected;

        public ThemePreviewWindow(string databasePath, string userId, AppTheme current)
        {
            InitializeComponent();
            _databasePath = databasePath;
            _userId = userId;
            _selected = current;
            BuildThemeCards();
        }

        private void BuildThemeCards()
        {
            ThemeList.Children.Clear();
            foreach (AppTheme theme in ThemeService.Supported)
            {
                var card = new Button
                {
                    Style = (Style)FindResource("GlassButton"),
                    Width = 260,
                    Margin = new Thickness(6),
                    HorizontalContentAlignment = Stretch,
                    Tag = theme
                };

                var panel = new StackPanel();
                var title = new TextBlock
                {
                    Text = ThemeService.DisplayName(theme),
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (SolidColorBrush)ThemeService.Brush("TextBrush")
                };
                var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                foreach (string key in new[] { "BrandGreen", "Accent", "WindowBackground", "CardBackground" })
                {
                    var box = new Border
                    {
                        Width = 34,
                        Height = 22,
                        Margin = new Thickness(0, 0, 6, 0),
                        CornerRadius = new CornerRadius(5),
                        Background = ThemeService.Brush(key + "Brush"),
                        BorderBrush = ThemeService.Brush("GlassBorderBrush"),
                        BorderThickness = new Thickness(1)
                    };
                    swatches.Children.Add(box);
                }
                panel.Children.Add(title);
                panel.Children.Add(swatches);
                card.Content = panel;
                card.Click += ThemeCard_Click;
                if (theme == _selected) card.BorderBrush = ThemeService.Brush("AccentBrush");
                ThemeList.Children.Add(card);
            }
        }

        private void ThemeCard_Click(object sender, RoutedEventArgs e)
        {
            var card = sender as Button;
            if (card == null) return;
            _selected = (AppTheme)card.Tag;
            foreach (Button item in ThemeList.Children)
                item.BorderBrush = item == card
                    ? ThemeService.Brush("AccentBrush")
                    : ThemeService.Brush("GlassBorderBrush");
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            ThemeService.SaveTheme(_databasePath, _selected, _userId);
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
