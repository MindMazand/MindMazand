using System.Windows;
using System.Windows.Input;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Views
{
    public partial class LoginWindow : Window
    {
        private readonly string _databasePath;

        public LoginWindow(string databasePath)
        {
            InitializeComponent();
            _databasePath = databasePath;
            UsernameBox.Focus();
        }

        private void Login_Click(object sender, RoutedEventArgs e)
        {
            var session = AuthService.Authenticate(_databasePath, UsernameBox.Text, PasswordBox.Password);
            if (session == null)
            {
                ErrorText.Text = "نام کاربری یا گذرواژه صحیح نیست، یا حساب غیرفعال است.";
                PasswordBox.Clear();
                return;
            }

            DialogResult = true;
            Close();
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                Login_Click(sender, e);
        }
    }
}