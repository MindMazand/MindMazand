using System;
using System.Windows;
using System.Windows.Controls;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Views
{
    /// <summary>
    /// Asks for a new password twice. Used by the user-administration screen so
    /// a reset password is typed once and confirmed, without ever being shown.
    /// </summary>
    public sealed class PasswordPromptWindow : Window
    {
        private readonly PasswordBox _first;
        private readonly PasswordBox _second;
        private readonly TextBlock _error;

        public string Password { get; private set; }

        public PasswordPromptWindow()
        {
            Title = "گذرواژهٔ جدید";
            Width = 420; Height = 260;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FlowDirection = FlowDirection.RightToLeft;
            Background = ThemeService.Brush("WindowBackgroundBrush");
            Foreground = ThemeService.Brush("TextBrush");

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "گذرواژهٔ جدید را دو بار وارد کنید.",
                Margin = new Thickness(0, 0, 0, 10),
                Foreground = ThemeService.Brush("TextBrush") });
            _first = new PasswordBox { Height = 34, Margin = new Thickness(0, 4, 0, 8),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                BorderBrush = ThemeService.Brush("GlassBorderBrush") };
            _second = new PasswordBox { Height = 34, Margin = new Thickness(0, 4, 0, 8),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                BorderBrush = ThemeService.Brush("GlassBorderBrush") };
            panel.Children.Add(_first);
            panel.Children.Add(_second);
            _error = new TextBlock { Margin = new Thickness(0, 0, 0, 8),
                Foreground = ThemeService.Brush("DangerBrush") };
            panel.Children.Add(_error);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left };
            var ok = new Button { Content = "تأیید", Height = 38, Width = 110,
                Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 5, 10, 5),
                Background = ThemeService.Brush("AccentBrush"),
                Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            ok.Click += Confirm;
            var cancel = new Button { Content = "انصراف", Height = 38, Width = 110,
                Padding = new Thickness(10, 5, 10, 5),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                BorderBrush = ThemeService.Brush("GlassBorderBrush") };
            cancel.Click += (s, e) => { DialogResult = false; Close(); };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            Content = panel;
        }

        private void Confirm(object sender, RoutedEventArgs e)
        {
            if (String.IsNullOrEmpty(_first.Password))
            {
                _error.Text = "گذرواژه الزامی است.";
                return;
            }
            if (!String.Equals(_first.Password, _second.Password, StringComparison.Ordinal))
            {
                _error.Text = "دو گذرواژه یکسان نیستند.";
                return;
            }
            Password = _first.Password;
            DialogResult = true;
            Close();
        }
    }
}
