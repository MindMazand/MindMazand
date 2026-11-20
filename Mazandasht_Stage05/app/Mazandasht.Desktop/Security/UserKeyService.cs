using System;
using System.Data.SQLite;
using System.Security.Cryptography;
using System.Text;

namespace Mazandasht.Desktop.Security
{
    public sealed class IssuedUserKey
    {
        public string KeyId { get; set; }
        public string SecretOnce { get; set; }
    }

    public static class UserKeyService
    {
        // The plaintext secret is returned once and is never stored in the database.
        public static IssuedUserKey Create(SQLiteConnection connection, string userId, string label, DateTime? expiresUtc, string actorUserId)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            if (String.IsNullOrWhiteSpace(userId)) throw new ArgumentException("شناسه کاربر الزامی است.");

            var secretBytes = new byte[32];
            var idBytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(secretBytes);
                rng.GetBytes(idBytes);
            }

            var secret = Convert.ToBase64String(secretBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            string hash;
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(secret))).Replace("-", "").ToLowerInvariant();

            var keyId = "MZD-" + BitConverter.ToString(idBytes).Replace("-", "");
            using (var command = new SQLiteCommand(
                @"INSERT INTO user_keys(id,user_id,key_id,secret_hash_sha256,label,expires_at_utc,created_by)
                  VALUES(@id,@user,@key,@hash,@label,@expires,@actor);", connection))
            {
                command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("@user", userId);
                command.Parameters.AddWithValue("@key", keyId);
                command.Parameters.AddWithValue("@hash", hash);
                command.Parameters.AddWithValue("@label", String.IsNullOrWhiteSpace(label) ? "کلید کاربر" : label.Trim());
                command.Parameters.AddWithValue("@expires", expiresUtc.HasValue ? (object)expiresUtc.Value.ToUniversalTime().ToString("o") : DBNull.Value);
                command.Parameters.AddWithValue("@actor", String.IsNullOrWhiteSpace(actorUserId) ? (object)DBNull.Value : actorUserId);
                command.ExecuteNonQuery();
            }

            Array.Clear(secretBytes, 0, secretBytes.Length);
            return new IssuedUserKey { KeyId = keyId, SecretOnce = secret };
        }
    }
}