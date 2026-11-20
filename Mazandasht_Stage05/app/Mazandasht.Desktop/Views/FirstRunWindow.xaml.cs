using System;
using System.Windows;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Views
{
    public partial class FirstRunWindow : Window
    {
        private readonly string _databasePath;

        public FirstRunWindow(string databasePath)
        {
            InitializeComponent();
            _databasePath = databasePath;
        }

        private void CreateAdmin_Click(object sender, RoutedEventArgs e)
        {
            ErrorText.Text = "";
            if (PasswordBox.Password != ConfirmPasswordBox.Password)
            {
                ErrorText.Text = "گذرواژه و تکرار آن یکسان نیستند.";
                return;
            }

            try
            {
                AuthService.CreateFirstAdmin(_databasePath, UsernameBox.Text, DisplayNameBox.Text, PasswordBox.Password);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                ErrorText.Text = ex.Message;
            }
        }
    }
}