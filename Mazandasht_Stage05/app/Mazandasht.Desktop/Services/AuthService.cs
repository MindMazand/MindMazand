using System;
using System.Data.SQLite;
using Mazandasht.Desktop.Security;

namespace Mazandasht.Desktop.Services
{
    public static class AuthService
    {
        public static bool HasUsers(string databasePath)
        {
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand("SELECT COUNT(1) FROM users;", connection))
                return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }

        public static void CreateFirstAdmin(string databasePath, string username, string displayName, string password)
        {
            username = (username ?? "").Trim();
            displayName = (displayName ?? "").Trim();

            if (username.Length < 4) throw new ArgumentException("نام کاربری باید حداقل ۴ نویسه باشد.");
            if (displayName.Length < 2) throw new ArgumentException("نام نمایشی معتبر نیست.");
            UserAdminService.PasswordPolicy.ThrowIfWeak(username, password);

            var passwordHash = PasswordHasher.Create(password);

            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                using (var count = new SQLiteCommand("SELECT COUNT(1) FROM users;", connection, transaction))
                {
                    if (Convert.ToInt32(count.ExecuteScalar()) != 0)
                        throw new InvalidOperationException("مدیر اولیه قبلاً ایجاد شده است.");
                }

                var userId = Guid.NewGuid().ToString("N");
                using (var insert = new SQLiteCommand(
                    @"INSERT INTO users(id,username,display_name,password_algorithm,password_hash,password_salt,
                        password_iterations,role_id,is_active,must_change_password)
                      VALUES(@id,@username,@display,@algorithm,@hash,@salt,@iterations,'role-admin',1,0);",
                    connection, transaction))
                {
                    insert.Parameters.AddWithValue("@id", userId);
                    insert.Parameters.AddWithValue("@username", username);
                    insert.Parameters.AddWithValue("@display", displayName);
                    insert.Parameters.AddWithValue("@algorithm", "PBKDF2-HMAC-SHA256");
                    insert.Parameters.AddWithValue("@hash", passwordHash.Hash);
                    insert.Parameters.AddWithValue("@salt", passwordHash.Salt);
                    insert.Parameters.AddWithValue("@iterations", passwordHash.Iterations);
                    insert.ExecuteNonQuery();
                }

                using (var audit = new SQLiteCommand(
                    @"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
                      VALUES(@id,@actor,'BOOTSTRAP_ADMIN_CREATED','USER',@entity,@after,@machine);",
                    connection, transaction))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", userId);
                    audit.Parameters.AddWithValue("@entity", userId);
                    audit.Parameters.AddWithValue("@after", "{\"role\":\"ADMIN\",\"bootstrap\":true}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }

                transaction.Commit();
                AppSession.CurrentUser = new UserSession
                {
                    UserId = userId, Username = username, DisplayName = displayName, RoleCode = "ADMIN"
                };
            }
        }

        public static UserSession Authenticate(string databasePath, string username, string password)
        {
            using (var connection = Open(databasePath))
            {
                string userId, displayName, roleCode;
                byte[] hash, salt;
                int iterations;

                using (var command = new SQLiteCommand(
                    @"SELECT u.id,u.display_name,u.password_hash,u.password_salt,u.password_iterations,r.role_code
                      FROM users u INNER JOIN roles r ON r.id=u.role_id
                      WHERE u.username=@username COLLATE NOCASE AND u.is_active=1 AND r.is_active=1;", connection))
                {
                    command.Parameters.AddWithValue("@username", (username ?? "").Trim());
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read())
                        {
                            // Wrong username or a disabled account: record the
                            // attempt without revealing which one.
                            WriteSecurityEvent(connection, null, (username ?? "").Trim(),
                                "LOGIN_FAILURE", "reason=unknown_user");
                            return null;
                        }
                        userId = reader.GetString(0);
                        displayName = reader.GetString(1);
                        hash = (byte[])reader["password_hash"];
                        salt = (byte[])reader["password_salt"];
                        iterations = reader.GetInt32(4);
                        roleCode = reader.GetString(5);
                    }
                }

                if (!PasswordHasher.Verify(password, hash, salt, iterations))
                {
                    WriteSecurityEvent(connection, userId, (username ?? "").Trim(),
                        "LOGIN_FAILURE", "reason=bad_password");
                    return null;
                }

                using (var update = new SQLiteCommand(
                    "UPDATE users SET last_login_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=@id;", connection))
                {
                    update.Parameters.AddWithValue("@id", userId);
                    update.ExecuteNonQuery();
                }
                WriteSecurityEvent(connection, userId, (username ?? "").Trim(),
                    "LOGIN_SUCCESS", "role=" + roleCode);

                var session = new UserSession
                {
                    UserId = userId, Username = (username ?? "").Trim(),
                    DisplayName = displayName, RoleCode = roleCode
                };
                AppSession.CurrentUser = session;
                return session;
            }
        }

        /// <summary>
        /// Security events are part of the fixed schema enum, so only the codes
        /// the database accepts may be written here.
        /// </summary>
        private static void WriteSecurityEvent(SQLiteConnection connection, string userId,
            string usernameAttempted, string eventType, string details)
        {
            try
            {
                using (var command = new SQLiteCommand(
                    @"INSERT INTO security_events(id,user_id,username_attempted,event_type,details_json,machine_name)
                      VALUES(@id,@user,@username,@type,@details,@machine);", connection))
                {
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@user", (object)userId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@username",
                        (object)usernameAttempted ?? DBNull.Value);
                    command.Parameters.AddWithValue("@type", eventType);
                    command.Parameters.AddWithValue("@details",
                        "{\"detail\":\"" + (details ?? String.Empty).Replace("\"", "'") + "\"}");
                    command.Parameters.AddWithValue("@machine", Environment.MachineName);
                    command.ExecuteNonQuery();
                }
            }
            catch
            {
                // Auditing must never block a login.
            }
        }

        private static SQLiteConnection Open(string databasePath)
        {
            var connection = new SQLiteConnection("Data Source=" + databasePath + ";Version=3;Pooling=True;");
            connection.Open();
            using (var command = new SQLiteCommand("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;", connection))
                command.ExecuteNonQuery();
            return connection;
        }
    }
}