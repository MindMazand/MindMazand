using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using Mazandasht.Desktop.Security;

namespace Mazandasht.Desktop.Services
{
    public sealed class RoleOption
    {
        public string Key { get; set; }
        public string Value { get; set; }
    }

    public sealed class UserRow
    {
        public string Id { get; set; }
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string RoleCode { get; set; }
        public bool IsActive { get; set; }
        public bool MustChangePassword { get; set; }
        public string LastLoginAtUtc { get; set; }
    }

    /// <summary>
    /// User and password administration (G11).
    ///
    /// Everything here is gated by users.manage, which only ADMIN holds in the
    /// shipped seed; a manager may read the list but not change it. Passwords are
    /// never written to the audit log, and the policy is enforced in one place so
    /// the first-admin bootstrap, user creation and password reset cannot drift
    /// apart.
    /// </summary>
    public static class UserAdminService
    {
        public const int MinimumPasswordLength = 12;

        private const string SqlPermissionCheck =
            @"SELECT COUNT(*) FROM users u
              JOIN roles r ON r.id=u.role_id
              JOIN role_permissions rp ON rp.role_id=r.id
              WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
                AND rp.permission_code='users.manage' AND rp.granted=1;";

        private const string SqlUsers =
            @"SELECT u.id,u.username,u.display_name,r.role_code,u.is_active,
                     u.must_change_password,u.last_login_at_utc
              FROM users u JOIN roles r ON r.id=u.role_id
              ORDER BY u.is_active DESC,u.username;";

        private const string SqlRoleByCode =
            "SELECT id FROM roles WHERE role_code=@code AND is_active=1;";
        private const string SqlUserById =
            @"SELECT u.id,u.username,u.display_name,r.role_code,u.is_active
              FROM users u JOIN roles r ON r.id=u.role_id WHERE u.id=@id;";
        private const string SqlActiveAdminCount =
            @"SELECT COUNT(*) FROM users u JOIN roles r ON r.id=u.role_id
              WHERE u.is_active=1 AND r.role_code='ADMIN' AND r.is_active=1;";
        private const string SqlUserInsert =
            @"INSERT INTO users(id,username,display_name,password_algorithm,password_hash,password_salt,
                    password_iterations,role_id,is_active,must_change_password,created_by)
              VALUES(@id,@username,@display,'PBKDF2-HMAC-SHA256',@hash,@salt,@iterations,
                     (SELECT id FROM roles WHERE role_code=@role),1,@mustChange,@createdBy);";
        private const string SqlSetActive =
            "UPDATE users SET is_active=@active WHERE id=@id;";
        private const string SqlUpdatePassword =
            @"UPDATE users SET password_algorithm='PBKDF2-HMAC-SHA256',password_hash=@hash,
                    password_salt=@salt,password_iterations=@iterations,
                    must_change_password=@mustChange WHERE id=@id;";

        /// <summary>
        /// True when the caller may administer users (users.manage).
        /// </summary>
        public static bool HasUserManagement(string databasePath, string userId)
        {
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    RequirePermission(connection, transaction, userId);
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Active roles for the user-creation combo. Role codes, not ids, are
        /// shown so the operator can recognise them.
        /// </summary>
        public static IList<RoleOption> RoleOptions(string databasePath)
        {
            var options = new List<RoleOption>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(
                "SELECT role_code,display_name FROM roles WHERE is_active=1 ORDER BY role_code;",
                connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    options.Add(new RoleOption {
                        Key = reader.GetString(0) + " — " + reader.GetString(1),
                        Value = reader.GetString(0)
                    });
            return options;
        }

        public static IList<UserRow> List(string databasePath, string userId)
        {
            var rows = new List<UserRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlUsers, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    rows.Add(new UserRow {
                        Id = reader.GetString(0),
                        Username = reader.GetString(1),
                        DisplayName = reader.GetString(2),
                        RoleCode = reader.GetString(3),
                        IsActive = reader.GetInt32(4) == 1,
                        MustChangePassword = reader.GetInt32(5) == 1,
                        LastLoginAtUtc = reader.IsDBNull(6) ? null : reader.GetString(6)
                    });
                }
            return rows;
        }

        public static UserRow Find(string databasePath, string userId)
        {
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlUserById, connection))
            {
                command.Parameters.AddWithValue("@id", userId ?? String.Empty);
                using (var reader = command.ExecuteReader())
                    if (!reader.Read()) return null;
                    else return new UserRow {
                        Id = reader.GetString(0), Username = reader.GetString(1),
                        DisplayName = reader.GetString(2), RoleCode = reader.GetString(3),
                        IsActive = reader.GetInt32(4) == 1
                    };
            }
        }

        public static void CreateUser(string databasePath, string username, string displayName,
            string roleCode, string password, string userId)
        {
            username = (username ?? String.Empty).Trim();
            displayName = (displayName ?? String.Empty).Trim();
            roleCode = (roleCode ?? String.Empty).Trim().ToUpperInvariant();
            if (username.Length < 4) throw new ArgumentException("نام کاربری باید حداقل ۴ نویسه باشد.");
            if (displayName.Length < 2) throw new ArgumentException("نام نمایشی معتبر نیست.");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequirePermission(connection, transaction, userId);
                using (var role = new SQLiteCommand(SqlRoleByCode, connection, transaction))
                {
                    role.Parameters.AddWithValue("@code", roleCode);
                    if (role.ExecuteScalar() == null)
                        throw new InvalidOperationException("نقش «" + roleCode + "» پیدا نشد.");
                }
                // Authorised and validated before the expensive key derivation.
                PasswordPolicy.ThrowIfWeak(username, password);
                var hash = PasswordHasher.Create(password);
                using (var insert = new SQLiteCommand(SqlUserInsert, connection, transaction))
                {
                    insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    insert.Parameters.AddWithValue("@username", username);
                    insert.Parameters.AddWithValue("@display", displayName);
                    insert.Parameters.AddWithValue("@hash", hash.Hash);
                    insert.Parameters.AddWithValue("@salt", hash.Salt);
                    insert.Parameters.AddWithValue("@iterations", hash.Iterations);
                    insert.Parameters.AddWithValue("@role", roleCode);
                    insert.Parameters.AddWithValue("@mustChange", 1);
                    insert.Parameters.AddWithValue("@createdBy", userId);
                    insert.ExecuteNonQuery();
                }
                Audit(connection, transaction, userId, "USER_CREATED", "USER", username);
                transaction.Commit();
            }
        }

        /// <summary>
        /// Enables or disables a user. The last active admin cannot be disabled,
        /// and an operator cannot disable their own account.
        /// </summary>
        public static void SetActive(string databasePath, string targetUserId, bool isActive,
            string userId)
        {
            if (String.Equals(targetUserId, userId, StringComparison.Ordinal) && !isActive)
                throw new InvalidOperationException("شما نمی‌توانید حساب خود را غیرفعال کنید.");

            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequirePermission(connection, transaction, userId);
                var target = FindConnection(connection, transaction, targetUserId);
                if (target == null) throw new InvalidOperationException("کاربر پیدا نشد.");

                if (!isActive && target.RoleCode == "ADMIN")
                {
                    var admins = Convert.ToInt32(Scalar(connection, transaction, SqlActiveAdminCount));
                    if (admins <= 1)
                        throw new InvalidOperationException(
                            "نمی‌توان تنها مدیر فعال سیستم را غیرفعال کرد.");
                }

                using (var update = new SQLiteCommand(SqlSetActive, connection, transaction))
                {
                    update.Parameters.AddWithValue("@id", targetUserId);
                    update.Parameters.AddWithValue("@active", isActive ? 1 : 0);
                    update.ExecuteNonQuery();
                }
                if (!isActive)
                    SecurityEvent(connection, transaction, targetUserId, "USER_DISABLED",
                        "username=" + target.Username);
                Audit(connection, transaction, userId,
                    isActive ? "USER_ENABLED" : "USER_DISABLED", "USER", targetUserId);
                transaction.Commit();
            }
        }

        /// <summary>
        /// Sets a new password for another user (the user must change it at the
        /// next login).
        /// </summary>
        public static void ResetPassword(string databasePath, string targetUserId,
            string newPassword, string userId)
        {
            var target = Find(databasePath, targetUserId);
            if (target == null) throw new InvalidOperationException("کاربر پیدا نشد.");
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequirePermission(connection, transaction, userId);
                PasswordPolicy.ThrowIfWeak(target.Username, newPassword);
                var hash = PasswordHasher.Create(newPassword);
                using (var update = new SQLiteCommand(SqlUpdatePassword, connection, transaction))
                {
                    update.Parameters.AddWithValue("@id", targetUserId);
                    update.Parameters.AddWithValue("@hash", hash.Hash);
                    update.Parameters.AddWithValue("@salt", hash.Salt);
                    update.Parameters.AddWithValue("@iterations", hash.Iterations);
                    update.Parameters.AddWithValue("@mustChange", 1);
                    update.ExecuteNonQuery();
                }
                SecurityEvent(connection, transaction, targetUserId, "PASSWORD_CHANGED",
                    "by=" + userId);
                Audit(connection, transaction, userId, "PASSWORD_RESET", "USER", targetUserId);
                transaction.Commit();
            }
        }

        /// <summary>
        /// Changes the caller's own password after checking the current one.
        /// </summary>
        public static void ChangePassword(string databasePath, string userId,
            string currentPassword, string newPassword)
        {
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                var username = FindConnectionUsername(connection, transaction, userId);
                if (username == null) throw new UnauthorizedAccessException("کاربر پیدا نشد.");

                byte[] expectedHash, salt;
                int iterations;
                using (var select = new SQLiteCommand(
                    "SELECT password_hash,password_salt,password_iterations FROM users WHERE id=@id;",
                    connection, transaction))
                {
                    select.Parameters.AddWithValue("@id", userId ?? String.Empty);
                    using (var reader = select.ExecuteReader())
                    {
                        if (!reader.Read())
                            throw new UnauthorizedAccessException("کاربر پیدا نشد.");
                        expectedHash = (byte[])reader["password_hash"];
                        salt = (byte[])reader["password_salt"];
                        iterations = reader.GetInt32(2);
                    }
                }
                if (!PasswordHasher.Verify(currentPassword, expectedHash, salt, iterations))
                    throw new UnauthorizedAccessException("گذرواژهٔ فعلی درست نیست.");

                // The current password is confirmed before any expensive work.
                PasswordPolicy.ThrowIfWeak(username, newPassword);
                var hash = PasswordHasher.Create(newPassword);

                using (var update = new SQLiteCommand(SqlUpdatePassword, connection, transaction))
                {
                    update.Parameters.AddWithValue("@id", userId);
                    update.Parameters.AddWithValue("@hash", hash.Hash);
                    update.Parameters.AddWithValue("@salt", hash.Salt);
                    update.Parameters.AddWithValue("@iterations", hash.Iterations);
                    update.Parameters.AddWithValue("@mustChange", 0);
                    update.ExecuteNonQuery();
                }
                SecurityEvent(connection, transaction, userId, "PASSWORD_CHANGED", "self=1");
                Audit(connection, transaction, userId, "PASSWORD_CHANGED", "USER", userId);
                transaction.Commit();
            }
        }

        // ------------------------------------------------------------------
        public static class PasswordPolicy
        {
            /// <summary>
            /// One place for the password rules so every entry point agrees:
            /// at least 12 characters, at least one letter and one digit, and the
            /// username must not appear in the password.
            /// </summary>
            public static void ThrowIfWeak(string username, string password)
            {
                if (String.IsNullOrEmpty(password) || password.Length < MinimumPasswordLength)
                    throw new ArgumentException(
                        "گذرواژه باید حداقل " + MinimumPasswordLength + " نویسه باشد.");
                var hasLetter = false;
                var hasDigit = false;
                foreach (var character in password)
                {
                    if (Char.IsLetter(character)) hasLetter = true;
                    else if (Char.IsDigit(character)) hasDigit = true;
                }
                if (!hasLetter || !hasDigit)
                    throw new ArgumentException("گذرواژه باید حداقل یک حرف و یک رقم داشته باشد.");
                if (!String.IsNullOrEmpty(username) &&
                    password.IndexOf(username, StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new ArgumentException("گذرواژه نباید شامل نام کاربری باشد.");
            }
        }

        private static string FindConnectionUsername(SQLiteConnection connection,
            SQLiteTransaction transaction, string userId)
        {
            using (var command = new SQLiteCommand(
                "SELECT username FROM users WHERE id=@id;", connection, transaction))
            {
                command.Parameters.AddWithValue("@id", userId ?? String.Empty);
                return Convert.ToString(command.ExecuteScalar());
            }
        }

        private static UserRow FindConnection(SQLiteConnection connection,
            SQLiteTransaction transaction, string userId)
        {
            using (var command = new SQLiteCommand(SqlUserById, connection, transaction))
            {
                command.Parameters.AddWithValue("@id", userId ?? String.Empty);
                using (var reader = command.ExecuteReader())
                    if (!reader.Read()) return null;
                    else return new UserRow {
                        Id = reader.GetString(0), Username = reader.GetString(1),
                        DisplayName = reader.GetString(2), RoleCode = reader.GetString(3),
                        IsActive = reader.GetInt32(4) == 1
                    };
            }
        }

        private static void SecurityEvent(SQLiteConnection connection,
            SQLiteTransaction transaction, string userId, string eventType, string details)
        {
            using (var command = new SQLiteCommand(
                @"INSERT INTO security_events(id,user_id,event_type,details_json,machine_name)
                  VALUES(@id,@user,@type,@details,@machine);", connection, transaction))
            {
                command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("@user", userId);
                command.Parameters.AddWithValue("@type", eventType);
                command.Parameters.AddWithValue("@details",
                    "{\"detail\":\"" + (details ?? String.Empty).Replace("\"", "'") + "\"}");
                command.Parameters.AddWithValue("@machine", Environment.MachineName);
                command.ExecuteNonQuery();
            }
        }

        private static void Audit(SQLiteConnection connection, SQLiteTransaction transaction,
            string userId, string action, string entityType, string entityId)
        {
            using (var command = new SQLiteCommand(
                @"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,machine_name)
                  VALUES(@id,@actor,@action,@entityType,@entityId,@machine);",
                connection, transaction))
            {
                command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("@actor", userId);
                command.Parameters.AddWithValue("@action", action);
                command.Parameters.AddWithValue("@entityType", entityType);
                command.Parameters.AddWithValue("@entityId", (object)entityId ?? DBNull.Value);
                command.Parameters.AddWithValue("@machine", Environment.MachineName);
                command.ExecuteNonQuery();
            }
        }

        private static void RequirePermission(SQLiteConnection connection,
            SQLiteTransaction transaction, string userId)
        {
            using (var command = new SQLiteCommand(SqlPermissionCheck, connection, transaction))
            {
                command.Parameters.AddWithValue("@user", userId ?? String.Empty);
                if (Convert.ToInt32(command.ExecuteScalar()) != 1)
                    throw new UnauthorizedAccessException(
                        "مدیریت کاربران فقط برای کاربر دارای مجوز «مدیریت کاربران و کلیدها» مجاز است.");
            }
        }

        private static object Scalar(SQLiteConnection connection, SQLiteTransaction transaction,
            string sql)
        {
            using (var command = new SQLiteCommand(sql, connection, transaction))
                return command.ExecuteScalar();
        }

        private static SQLiteConnection Open(string databasePath)
        {
            var connection = new SQLiteConnection(
                "Data Source=" + databasePath + ";Version=3;Pooling=True;");
            connection.Open();
            using (var command = new SQLiteCommand(
                "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000; PRAGMA synchronous=FULL;", connection))
                command.ExecuteNonQuery();
            return connection;
        }
    }
}
