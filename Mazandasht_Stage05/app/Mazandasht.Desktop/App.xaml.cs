using System;
using System.Windows;
using System.Windows.Threading;
using Mazandasht.Desktop.Database;
using Mazandasht.Desktop.Security;
using Mazandasht.Desktop.Services;
using Mazandasht.Desktop.Import;
using Mazandasht.Desktop.Views;

namespace Mazandasht.Desktop
{
    public partial class App : Application
    {
        private string _databasePath;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            try
            {
                _databasePath = DatabaseBootstrapper.Initialize();
                ThemeService.ApplySavedTheme(_databasePath);
                if (!AuthService.HasUsers(_databasePath))
                {
                    var firstRun = new FirstRunWindow(_databasePath);
                    if (firstRun.ShowDialog() != true)
                    {
                        Shutdown();
                        return;
                    }
                }
                else
                {
                    var login = new LoginWindow(_databasePath);
                    if (login.ShowDialog() != true)
                    {
                        Shutdown();
                        return;
                    }
                }

                var main = new MainWindow(_databasePath);
                MainWindow = main;
                main.Loaded += (loadedSender, loadedArgs) =>
                {
                    try
                    {
                        if (AppSession.CurrentUser != null &&
                            String.Equals(AppSession.CurrentUser.RoleCode, "ADMIN", StringComparison.OrdinalIgnoreCase) &&
                            InitialSetupService.TryMarkInitialImportPromptShown(_databasePath))
                        {
                            var answer = MessageBox.Show(
                                "راه‌اندازی اولیه انجام شد. اگر فایل‌های اطلاعات پایه یا سوابق خود را آماده دارید، می‌توانید اکنون Excel یا CSV را برای ماژول مربوط انتخاب و پیش‌نمایش کنید. این مرحله اختیاری است و می‌توانید بعداً از منوی «ورود اطلاعات Excel» انجام دهید.\n\nآیا اکنون مرکز ورود اطلاعات باز شود؟",
                                "ورود اولیه اطلاعات | مازندشت",
                                MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
                            if (answer == MessageBoxResult.Yes)
                            {
                                var importWindow = new ImportReviewWindow(_databasePath) { Owner = main };
                                importWindow.ShowDialog();
                            }
                        }
                    }
                    catch (Exception promptError)
                    {
                        MessageBox.Show("نمایش راه‌اندازی اختیاری ورود اطلاعات انجام نشد: " + promptError.Message,
                            "مازندشت", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                };
                main.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show("راه‌اندازی نرم‌افزار با خطا مواجه شد.\n\n" + ex.Message,
                    "مازندشت", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(-1);
            }
        }
    }
}
