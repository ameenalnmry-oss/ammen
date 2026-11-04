using PharmaLIMS.Infrastructure;
using PharmaLIMS.Repositories;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;

internal sealed class TrustedActivityAuthenticator(string connectionString) : ITrustedActivityAuthenticator
{
    public async Task<VerifiedActivitySigner?> AuthenticateAsync(string boundUsername, string password,
        CancellationToken cancellationToken)
    {
        // Uses guarded password verification and atomic success/failure/lockout commits.
        // The bound username is resolved by the native pipe caller map, never by the payload.
        var auth = new AuthService(new UserRepository(new DatabaseConnection(connectionString)), useTrustedTransport:false);
        var user = await auth.AuthenticateAsync(boundUsername, password, cancellationToken).ConfigureAwait(false);
        if (user is null) return null;
        try
        {
            return new VerifiedActivitySigner(user.Username, user.IsActive, user.IsLocked, user.MustChangePassword,
                user.CanReviewResults, user.CanApproveResults, (byte[])user.AuthenticationRowVersion.Clone());
        }
        finally { user.ClearSensitivePasswordData(); auth.ClearCurrentUser(); }
    }
}
