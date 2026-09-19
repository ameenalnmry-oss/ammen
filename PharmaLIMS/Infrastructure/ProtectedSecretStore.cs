using System;
using System.Security.Cryptography;
using System.Text;

namespace PharmaLIMS.Infrastructure
{
    /// <summary>
    /// Windows DPAPI wrapper for site-local secrets. Production SQL authentication
    /// passwords are never accepted as plaintext configuration values.
    /// </summary>
    internal static class ProtectedSecretStore
    {
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("PharmaLIMS.DatabaseCredential.v1");

        internal static string UnprotectCurrentUserBase64(string protectedBase64)
        {
            if (string.IsNullOrWhiteSpace(protectedBase64))
                throw new InvalidOperationException("The protected database password is missing.");

            try
            {
                byte[] encrypted = Convert.FromBase64String(protectedBase64.Trim());
                byte[] plaintext = ProtectedData.Unprotect(
                    encrypted,
                    Entropy,
                    DataProtectionScope.CurrentUser);
                try
                {
                    return Encoding.UTF8.GetString(plaintext);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    "The DPAPI-protected database password is not valid Base64.", ex);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    "The DPAPI-protected database password cannot be decrypted by the current Windows account. " +
                    "Re-protect the password while signed in as the account that runs PharmaLIMS.", ex);
            }
        }

        internal static string ProtectCurrentUserBase64(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext))
                throw new ArgumentException("A non-empty secret is required.", nameof(plaintext));

            byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
            try
            {
                byte[] encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encrypted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }
}
