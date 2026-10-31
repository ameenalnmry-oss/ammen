using System;
using System.Threading;
using System.Threading.Tasks;

namespace PharmaLIMS.TrustedSigning;

/// <summary>
/// Must resolve the already authenticated caller from a protected host transport
/// (e.g. Windows authenticated and impersonated named-pipe connection).
/// It MUST NOT resolve from caller-provided headers, JSON, usernames or roles.
/// </summary>
public interface ITrustedCallerIdentity
{
    string GetAuthenticatedUsername();
}

public sealed record TrustedSignerAccount(
    string Username,
    string PasswordHashNew,
    string PasswordSalt,
    bool IsActive,
    bool IsLocked,
    bool MustChangePassword,
    bool CanManageSettings,
    int UserId,
    byte[] AuthenticationRowVersion,
    string LegacyPasswordHash);

/// <summary>
/// Repository implementation must independently load current account/permission
/// state from SQL and atomically record each failed or successful reauthentication.
/// Failure transitions must enforce the same server-side lockout policy as login.
/// No credential or password hash may be returned to the desktop client.
/// </summary>
public interface ITrustedSignerAccountRepository
{
    Task<TrustedSignerAccount?> LoadByUsernameAsync(string username, CancellationToken cancellationToken);
    Task RecordFailedAttemptAsync(TrustedSignerAccount verifiedAccount, CancellationToken cancellationToken);
    Task<bool> ConfirmSuccessIfStillAuthorizedAsync(TrustedSignerAccount verifiedAccount, CancellationToken cancellationToken);
}

/// <summary>
/// Implements independent server-side signer verification against current modern
/// PBKDF2 credentials, bound to the host-authenticated principal. It has no
/// ability to accept identity claims from WPF's request payload.
/// </summary>
public sealed class TrustedHostAuthenticator : ITrustedSignerAuthenticator
{
    private readonly ITrustedCallerIdentity _caller;
    private readonly ITrustedSignerAccountRepository _accounts;

    public TrustedHostAuthenticator(
        ITrustedCallerIdentity caller,
        ITrustedSignerAccountRepository accounts)
    {
        _caller = caller ?? throw new ArgumentNullException(nameof(caller));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    }

    public async Task<VerifiedSigner?> AuthenticateForSignatureAsync(
        string reauthenticationSecret,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string username = _caller.GetAuthenticatedUsername();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(reauthenticationSecret))
            throw new UnauthorizedAccessException("A trusted caller identity and fresh credential are required.");

        TrustedSignerAccount? account = await _accounts.LoadByUsernameAsync(
            username, cancellationToken).ConfigureAwait(false);

        // Account roles and status come from the trusted repository, not desktop
        // claims. Do not attempt verification for disabled/locked/temp accounts.
        if (account is null ||
            !string.Equals(account.Username, username, StringComparison.OrdinalIgnoreCase) ||
            !account.IsActive || account.IsLocked || account.MustChangePassword ||
            !account.CanManageSettings)
            throw new UnauthorizedAccessException("EQ-001 signer is not eligible.");

        if (!TrustedCredentialVerifier.VerifyModernCredential(
                reauthenticationSecret, account.PasswordSalt, account.PasswordHashNew))
        {
            await _accounts.RecordFailedAttemptAsync(account, cancellationToken)
                .ConfigureAwait(false);
            throw new UnauthorizedAccessException("EQ-001 reauthentication failed.");
        }

        // The account may have been disabled or locked concurrently.
        bool committed = await _accounts.ConfirmSuccessIfStillAuthorizedAsync(
            account, cancellationToken).ConfigureAwait(false);
        if (!committed)
            throw new UnauthorizedAccessException("EQ-001 signer status changed.");

        return new VerifiedSigner(account.Username, true, false, false, true);
    }
}
