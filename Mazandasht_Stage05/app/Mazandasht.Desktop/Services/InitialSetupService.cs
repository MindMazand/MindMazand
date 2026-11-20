using System;
using System.Data.SQLite;
using Mazandasht.Desktop.Security;

namespace Mazandasht.Desktop.Services
{
    public static class InitialSetupService
    {
        private const string ImportPromptKey = "INITIAL_IMPORT_PROMPT_SHOWN";

        // Returns true exactly once per clean database. The flag is committed before
        // showing UI, so restarting the app never repeats the prompt.
        public static bool TryMarkInitialImportPromptShown(string databasePath)
        {
            using (var connection = new SQLiteConnection("Data Source=" + databasePath + ";Version=3;Pooling=True;"))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    using (var read = new SQLiteCommand(
                        "SELECT value_json FROM app_settings WHERE setting_key=@key;", connection, transaction))
                    {
                        read.Parameters.AddWithValue("@key", ImportPromptKey);
                        if (read.ExecuteScalar() != null)
                        {
                            transaction.Commit();
                            return false;
                        }
                    }

                    using (var insert = new SQLiteCommand(@"
                        INSERT INTO app_settings(setting_key,value_json,category,updated_by)
                        VALUES(@key,'true','ONBOARDING',@user);", connection, transaction))
                    {
                        insert.Parameters.AddWithValue("@key", ImportPromptKey);
                        insert.Parameters.AddWithValue("@user",
                            AppSession.CurrentUser == null ? (object)DBNull.Value : AppSession.CurrentUser.UserId);
                        insert.ExecuteNonQuery();
                    }
                    transaction.Commit();
                    return true;
                }
            }
        }
    }
}
