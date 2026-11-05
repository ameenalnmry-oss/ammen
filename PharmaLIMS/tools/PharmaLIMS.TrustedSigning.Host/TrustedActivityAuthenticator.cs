using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Models;
using PharmaLIMS.Repositories;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;

internal sealed class TrustedActivityAuthenticator(string connectionString) : ITrustedActivityAuthenticator
{
    private readonly string _connectionString = ValidateConnection(connectionString);

    public async Task<VerifiedActivitySigner?> AuthenticateAsync(
        string boundUsername,
        string password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(boundUsername) || boundUsername.Length > 100 ||
            string.IsNullOrEmpty(password) || password.Length > 4096)
            return null;

        var repository = new UserRepository(new DatabaseConnection(_connectionString));
        User? account = await repository.GetByUsernameAsync(
            boundUsername, cancellationToken).ConfigureAwait(false);

        // The primary EQ001 Users row is the authoritative credential/account state.
        // The isolated activity database remains authoritative only for the activity,
        // local UAT authorization and signed evidence.
        if (account is null ||
            !string.Equals(account.Username, boundUsername, StringComparison.OrdinalIgnoreCase) ||
            !account.IsActive || account.IsLocked || account.MustChangePassword ||
            account.PasswordChangedAt is null ||
            account.AuthenticationRowVersion is not { Length: 8 } ||
            string.IsNullOrWhiteSpace(account.PasswordHashNew) ||
            string.IsNullOrWhiteSpace(account.PasswordSalt) ||
            !PasswordSecurity.IsCredentialEncodingStructurallyValid(
                account.PasswordSalt, account.PasswordHashNew))
            return null;

        bool valid = PasswordSecurity.Verify(
            password,
            account.PasswordSalt,
            account.PasswordHashNew,
            out bool needsUpgrade);

        if (!valid)
        {
            await repository.RegisterAuthenticationFailureAsync(
                account, cancellationToken).ConfigureAwait(false);
            return null;
        }

        string? upgradedHash = null;
        string? upgradedSalt = null;
        if (needsUpgrade)
        {
            upgradedSalt = PasswordSecurity.GenerateSaltBase64();
            string salt = upgradedSalt;
            upgradedHash = await Task.Run(
                () => PasswordSecurity.HashToBase64(password, salt),
                cancellationToken).ConfigureAwait(false);
        }

        bool committed = await repository.ResetAuthenticationFailuresAsync(
            account, upgradedHash, upgradedSalt, cancellationToken).ConfigureAwait(false);
        if (!committed || account.AuthenticationRowVersion is not { Length: 8 })
            return null;

        return new VerifiedActivitySigner(
            account.Username,
            account.IsActive,
            account.IsLocked,
            account.MustChangePassword,
            account.CanReviewResults,
            account.CanApproveResults,
            (byte[])account.AuthenticationRowVersion.Clone());
    }

    private static string ValidateConnection(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        bool primaryUat =
            builder.InitialCatalog.StartsWith("EQ001_UAT_", StringComparison.Ordinal) ||
            builder.InitialCatalog.StartsWith("PharmaLIMS_EQ001_UAT_", StringComparison.Ordinal);

        if (!primaryUat ||
            !builder.IntegratedSecurity ||
            (builder.Encrypt != SqlConnectionEncryptOption.Mandatory &&
             builder.Encrypt != SqlConnectionEncryptOption.Strict) ||
            builder.TrustServerCertificate ||
            builder.AttachDBFilename.Length != 0)
        {
            throw new ArgumentException(
                "Trusted activity reauthentication requires the primary Windows-authenticated EQ001 UAT connection.");
        }

        return builder.ConnectionString;
    }
}
