using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Views
{
    /// <summary>
    /// Users, roles and passwords (G11).
    ///
    /// Administrative actions need users.manage; changing your own password is
    /// available to every signed-in operator. Passwords are never echoed back or
    /// written to the audit log.
    /// </summary>
    public sealed class SecurityWindow : Window
    {
        private readonly string _databasePath;
        private readonly string _userId;
        private readonly bool _canManage;

        private DataGrid _users;
        private ComboBox _roles;
        private TextBox _username;
        private TextBox _displayName;
        private PasswordBox _password;
        private PasswordBox _currentPassword;
        private PasswordBox _newPassword;
        private TextBlock _summary;
        private Button _createButton;
        private Button _disableButton;
        private Button _enableButton;
        private Button _resetButton;

        public SecurityWindow(string databasePath, string userId)
        {
            _databasePath = databasePath;
            _userId = userId;
            _canManage = UserAdminService.HasUserManagement(databasePath, userId);

            Title = "امنیت و کاربران | مازندشت";
            Width = 1080; Height = 780;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FlowDirection = FlowDirection.RightToLeft;
            Background = ThemeService.Brush("WindowBackgroundBrush");
            Foreground = ThemeService.Brush("TextBrush");

            var root = new DockPanel { Margin = new Thickness(22) };
            var heading = new TextBlock { Text = "امنیت و کاربران",
                FontSize = 24, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 16),
                Foreground = ThemeService.Brush("TextBrush") };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);

            var panel = new StackPanel { Margin = new Thickness(14) };

            if (!_canManage)
            {
                panel.Children.Add(new TextBlock {
                    Text = "شما مجوز «مدیریت کاربران و کلیدها» را ندارید؛ فقط مدیر سیستم می‌تواند کاربر بسازد یا گذرواژه بازنشانی کند.",
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 10),
                    Foreground = ThemeService.Brush("DangerBrush")
                });
            }

            panel.Children.Add(Section("کاربران"));
            _users = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true,
                CanUserAddRows = false, MaxHeight = 220, Margin = new Thickness(0, 6, 0, 0),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
            _users.SelectionChanged += (s, e) => UpdateActionButtons();
            panel.Children.Add(_users);

            var adminRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            _username = Field(adminRow, "نام کاربری", 150);
            _displayName = Field(adminRow, "نام نمایشی", 170);
            _roles = new ComboBox { Width = 150, Height = 34, Margin = new Thickness(5),
                DisplayMemberPath = "Key", SelectedValuePath = "Value",
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                BorderBrush = ThemeService.Brush("GlassBorderBrush") };
            adminRow.Children.Add(_roles);
            _password = Password(adminRow, "گذرواژهٔ اولیه", 160);
            _createButton = ActionButton("ساخت کاربر", CreateUser);
            adminRow.Children.Add(_createButton);
            panel.Children.Add(adminRow);

            var selectedRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            _disableButton = ActionButton("غیرفعال‌کردن کاربر انتخاب‌شده", DisableUser);
            _enableButton = ActionButton("فعال‌کردن کاربر انتخاب‌شده", EnableUser);
            _resetButton = ActionButton("بازنشانی گذرواژهٔ کاربر انتخاب‌شده", ResetPassword);
            selectedRow.Children.Add(_disableButton);
            selectedRow.Children.Add(_enableButton);
            selectedRow.Children.Add(_resetButton);
            panel.Children.Add(selectedRow);

            panel.Children.Add(Section("تغییر گذرواژهٔ خودتان"));
            var ownRow = new WrapPanel();
            _currentPassword = Password(ownRow, "گذرواژهٔ فعلی", 170);
            _newPassword = Password(ownRow, "گذرواژهٔ جدید", 170);
            var change = new Button { Content = "تغییر گذرواژه", Height = 38,
                Margin = new Thickness(5), Padding = new Thickness(12, 5, 12, 5),
                Background = ThemeService.Brush("AccentBrush"),
                Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            change.Click += ChangeOwnPassword;
            ownRow.Children.Add(change);
            panel.Children.Add(ownRow);

            _summary = new TextBlock { Margin = new Thickness(5, 12, 5, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeService.Brush("MutedTextBrush") };
            panel.Children.Add(_summary);

            root.Children.Add(panel);
            Content = root;
            Loaded += (s, e) => RefreshAll();
        }

        // ------------------------------------------------------------------
        private TextBlock Section(string text)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.Bold,
                FontSize = 15, Margin = new Thickness(0, 10, 0, 4),
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

        private PasswordBox Password(Panel parent, string label, double width)
        {
            var box = new PasswordBox { Width = width, Height = 34, Margin = new Thickness(5),
                VerticalContentAlignment = VerticalAlignment.Center, ToolTip = label,
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                BorderBrush = ThemeService.Brush("GlassBorderBrush") };
            parent.Children.Add(box);
            return box;
        }

        private Button ActionButton(string text, RoutedEventHandler click)
        {
            var button = new Button { Content = text, Height = 38, Margin = new Thickness(5),
                Padding = new Thickness(12, 5, 12, 5), IsEnabled = false,
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                BorderBrush = ThemeService.Brush("GlassBorderBrush") };
            button.Click += click;
            return button;
        }

        // ------------------------------------------------------------------
        private void RefreshAll()
        {
            try
            {
                _users.ItemsSource = UserAdminService.List(_databasePath, _userId);
                _roles.ItemsSource = UserAdminService.RoleOptions(_databasePath);
                if (_roles.Items.Count > 0) _roles.SelectedIndex = 0;
                _summary.Text = "گذرواژه‌ها هرگز در تاریخچهٔ حسابرسی ثبت نمی‌شوند. " +
                    "حداقل طول گذرواژه " + UserAdminService.MinimumPasswordLength +
                    " نویسه است و باید حرف و رقم داشته باشد.";
            }
            catch (Exception ex)
            {
                _summary.Text = "خواندن کاربران ناموفق بود: " + ex.Message;
            }
            UpdateActionButtons();
        }

        private void UpdateActionButtons()
        {
            var hasSelection = _users.SelectedItem is UserRow;
            _createButton.IsEnabled = _canManage;
            _disableButton.IsEnabled = _canManage && hasSelection;
            _enableButton.IsEnabled = _canManage && hasSelection;
            _resetButton.IsEnabled = _canManage && hasSelection;
        }

        private void CreateUser(object sender, RoutedEventArgs e)
        {
            try
            {
                var role = _roles.SelectedItem as RoleOption;
                UserAdminService.CreateUser(_databasePath, _username.Text, _displayName.Text,
                    role == null ? null : role.Value, _password.Password, _userId);
                _username.Text = String.Empty;
                _displayName.Text = String.Empty;
                _password.Password = String.Empty;
                RefreshAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "ساخت کاربر", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void DisableUser(object sender, RoutedEventArgs e) { SetActive(false); }

        private void EnableUser(object sender, RoutedEventArgs e) { SetActive(true); }

        private void SetActive(bool isActive)
        {
            var row = _users.SelectedItem as UserRow;
            if (row == null) return;
            try
            {
                UserAdminService.SetActive(_databasePath, row.Id, isActive, _userId);
                RefreshAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "تغییر وضعیت کاربر",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ResetPassword(object sender, RoutedEventArgs e)
        {
            var row = _users.SelectedItem as UserRow;
            if (row == null) return;
            var dialog = new PasswordPromptWindow { Owner = this };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                UserAdminService.ResetPassword(_databasePath, row.Id, dialog.Password, _userId);
                MessageBox.Show("گذرواژه بازنشانی شد؛ کاربر در ورود بعدی باید آن را تغییر دهد.",
                    "بازنشانی گذرواژه", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "بازنشانی گذرواژه",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ChangeOwnPassword(object sender, RoutedEventArgs e)
        {
            try
            {
                UserAdminService.ChangePassword(_databasePath, _userId,
                    _currentPassword.Password, _newPassword.Password);
                _currentPassword.Password = String.Empty;
                _newPassword.Password = String.Empty;
                MessageBox.Show("گذرواژه تغییر کرد.", "تغییر گذرواژه",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "تغییر گذرواژه",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
