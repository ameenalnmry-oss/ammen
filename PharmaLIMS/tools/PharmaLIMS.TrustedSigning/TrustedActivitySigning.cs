namespace PharmaLIMS.TrustedSigning;

public sealed record ActivitySigningRequest(Guid RequestId, long ActivityId, string Stage, string Reason,
    DateTimeOffset IssuedAtUtc, byte[] ExpectedVersion, string DatabaseName);

public sealed record VerifiedActivitySigner(string Username, bool IsActive, bool IsLocked,
    bool MustChangePassword, bool CanReviewResults, bool CanApproveResults, byte[] AuthenticationRowVersion);

public interface ITrustedActivityAuthenticator
{
    Task<VerifiedActivitySigner?> AuthenticateAsync(string boundUsername, string password, CancellationToken cancellationToken);
}

public interface ITrustedActivitySignatureStore
{
    Task CommitOnceAsync(ActivitySigningRequest request, VerifiedActivitySigner signer, CancellationToken cancellationToken);
}

public sealed class TrustedActivitySigningCoordinator(
    ITrustedActivityAuthenticator authentication, ITrustedActivitySignatureStore store, TimeProvider? clock = null)
{
    public async Task CommitAsync(ActivitySigningRequest request, string boundUsername, string secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateShape(request, (clock ?? TimeProvider.System).GetUtcNow());
        if (string.IsNullOrWhiteSpace(boundUsername) || boundUsername.Length > 100 ||
            string.IsNullOrEmpty(secret) || secret.Length > 4096)
            throw new UnauthorizedAccessException("A Windows-bound account and fresh password are required.");

        var signer = await authentication.AuthenticateAsync(boundUsername, secret, cancellationToken).ConfigureAwait(false);
        if (signer is null || !string.Equals(signer.Username, boundUsername, StringComparison.OrdinalIgnoreCase) ||
            !signer.IsActive || signer.IsLocked || signer.MustChangePassword || signer.AuthenticationRowVersion?.Length != 8 ||
            (request.Stage == "Reviewed" ? !signer.CanReviewResults : !signer.CanApproveResults))
            throw new UnauthorizedAccessException("The independently verified account cannot sign this activity stage.");
        ValidateShape(request, (clock ?? TimeProvider.System).GetUtcNow());
        await store.CommitOnceAsync(request, signer, cancellationToken).ConfigureAwait(false);
    }

    public static void ValidateShape(ActivitySigningRequest request, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty || request.ActivityId <= 0 ||
            request.Stage is not ("Reviewed" or "Approved") ||
            request.ExpectedVersion is not { Length: 8 } || !request.ExpectedVersion.Any(value => value != 0) ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 10 ||
            request.Reason.Length > 500 || request.Reason.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(request.DatabaseName) || request.DatabaseName.Length > 128 ||
            !request.DatabaseName.All(value => char.IsAsciiLetterOrDigit(value) || value == '_') ||
            !(request.DatabaseName.StartsWith("EQ001_UAT_", StringComparison.Ordinal) ||
              request.DatabaseName.StartsWith("PharmaLIMS_EQ001_UAT_", StringComparison.Ordinal)))
            throw new ArgumentException("Invalid isolated UAT activity signature request.");
        if (request.IssuedAtUtc.Offset != TimeSpan.Zero || request.IssuedAtUtc > nowUtc ||
            nowUtc - request.IssuedAtUtc > TimeSpan.FromMinutes(2))
            throw new UnauthorizedAccessException("Activity signing request is expired or invalid.");
    }
}
