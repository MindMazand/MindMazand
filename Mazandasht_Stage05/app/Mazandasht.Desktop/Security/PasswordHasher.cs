using System;
using System.Security.Cryptography;

namespace Mazandasht.Desktop.Security
{
    public sealed class PasswordHash
    {
        public byte[] Hash { get; set; }
        public byte[] Salt { get; set; }
        public int Iterations { get; set; }
    }

    public static class PasswordHasher
    {
        public const int DefaultIterations = 600000;
        private const int SaltBytes = 16;
        private const int HashBytes = 32;

        public static PasswordHash Create(string password)
        {
            if (String.IsNullOrWhiteSpace(password) || password.Length < 12)
                throw new ArgumentException("گذرواژه باید حداقل ۱۲ نویسه داشته باشد.");

            var salt = new byte[SaltBytes];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(salt);

            return new PasswordHash
            {
                Salt = salt,
                Iterations = DefaultIterations,
                Hash = Derive(password, salt, DefaultIterations)
            };
        }

        public static bool Verify(string password, byte[] expectedHash, byte[] salt, int iterations)
        {
            if (String.IsNullOrEmpty(password) || expectedHash == null || salt == null || iterations < 100000)
                return false;

            var actual = Derive(password, salt, iterations);
            if (actual.Length != expectedHash.Length)
                return false;

            int diff = 0;
            for (int i = 0; i < actual.Length; i++)
                diff |= actual[i] ^ expectedHash[i];
            return diff == 0;
        }

        private static byte[] Derive(string password, byte[] salt, int iterations)
        {
            using (var derive = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                return derive.GetBytes(HashBytes);
        }
    }
}