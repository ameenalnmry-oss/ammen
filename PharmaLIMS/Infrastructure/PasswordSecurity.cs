using System.Security.Cryptography;

namespace PharmaLIMS.Infrastructure
{
    internal static class PasswordSecurity
    {
        internal const int CurrentIterations = 600000;
        internal const int LegacyIterations = 100000;
        internal const int HashSizeBytes = 32;
        internal const int SaltSizeBytes = 32;

        internal static string GenerateSaltBase64() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSizeBytes));

        internal static string HashToBase64(string password, string saltBase64, int iterations = CurrentIterations)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password is required.", nameof(password));
            if (string.IsNullOrWhiteSpace(saltBase64))
                throw new ArgumentException("Password salt is required.", nameof(saltBase64));
            if (iterations < 100000)
                throw new ArgumentOutOfRangeException(nameof(iterations));

            byte[] salt = Convert.FromBase64String(saltBase64);
            if (salt.Length != SaltSizeBytes)
                throw new ArgumentException("Password salt has an invalid size.", nameof(saltBase64));

            using var pbkdf2 = new Rfc2898DeriveBytes(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256);

            return Convert.ToBase64String(pbkdf2.GetBytes(HashSizeBytes));
        }


        internal static void ValidateNewPassword(string password, string? username = null)
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException("A password is required.");
            if (password.Length < 12)
                throw new InvalidOperationException("Password must contain at least 12 characters.");
            if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) ||
                !password.Any(char.IsDigit) || !password.Any(ch => !char.IsLetterOrDigit(ch)))
            {
                throw new InvalidOperationException(
                    "Password must include upper-case, lower-case, numeric, and special characters.");
            }
            if (!string.IsNullOrWhiteSpace(username) &&
                password.Contains(username.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Password must not contain the username.");
            }
        }

        internal static bool IsCredentialEncodingStructurallyValid(string? saltBase64, string? hashBase64)
        {
            if (string.IsNullOrWhiteSpace(saltBase64) || string.IsNullOrWhiteSpace(hashBase64))
                return false;

            try
            {
                return Convert.FromBase64String(saltBase64).Length == SaltSizeBytes &&
                       Convert.FromBase64String(hashBase64).Length == HashSizeBytes;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        internal static bool Verify(
            string password,
            string saltBase64,
            string hashBase64,
            out bool needsUpgrade)
        {
            needsUpgrade = false;
            if (string.IsNullOrEmpty(password) ||
                string.IsNullOrWhiteSpace(saltBase64) ||
                string.IsNullOrWhiteSpace(hashBase64))
            {
                return false;
            }

            if (!IsCredentialEncodingStructurallyValid(saltBase64, hashBase64))
                return false;

            try
            {
                byte[] stored = Convert.FromBase64String(hashBase64);

                if (Matches(password, saltBase64, stored, CurrentIterations))
                    return true;

                if (LegacyIterations != CurrentIterations &&
                    Matches(password, saltBase64, stored, LegacyIterations))
                {
                    needsUpgrade = true;
                    return true;
                }

                return false;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        private static bool Matches(string password, string saltBase64, byte[] stored, int iterations)
        {
            byte[] calculated = Convert.FromBase64String(HashToBase64(password, saltBase64, iterations));
            return stored.Length == calculated.Length &&
                   CryptographicOperations.FixedTimeEquals(calculated, stored);
        }
    }
}
