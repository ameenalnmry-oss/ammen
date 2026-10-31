using System;
using System.Security.Cryptography;

namespace PharmaLIMS.TrustedSigning;

/// <summary>
/// Password verification on the trusted host only. The account identity, salt,
/// and hash MUST be loaded independently by a trusted server-side repository.
/// The desktop client must never supply or inspect stored credential material.
/// Legacy plaintext credentials are deliberately rejected for EQ-001 signing.
/// </summary>
public static class TrustedCredentialVerifier
{
    private const int SaltBytes = 32;
    private const int HashBytes = 32;
    private const int CurrentIterations = 600000;
    private const int PreviousIterations = 100000;

    public static bool VerifyModernCredential(
        string? password,
        string? storedSaltBase64,
        string? storedHashBase64)
    {
        if (string.IsNullOrEmpty(password) ||
            string.IsNullOrWhiteSpace(storedSaltBase64) ||
            string.IsNullOrWhiteSpace(storedHashBase64))
            return false;

        try
        {
            byte[] salt = Convert.FromBase64String(storedSaltBase64);
            byte[] expected = Convert.FromBase64String(storedHashBase64);
            if (salt.Length != SaltBytes || expected.Length != HashBytes)
                return false;

            byte[] current = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, CurrentIterations, HashAlgorithmName.SHA256, HashBytes);
            if (CryptographicOperations.FixedTimeEquals(current, expected))
                return true;

            byte[] previous = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, PreviousIterations, HashAlgorithmName.SHA256, HashBytes);
            return CryptographicOperations.FixedTimeEquals(previous, expected);
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
}
