namespace PharmaLIMS.TrustedSigning;

public sealed record ActivitySigningRequest(Guid RequestId, long ActivityId, string Stage, string Reason,
    DateTimeOffset IssuedAtUtc, byte[] ExpectedVersion, string DatabaseName);

public sealed record ActivitySnapshotRequest(Guid RequestId, long ActivityId, int EquipmentId, string Stage);
public sealed record ActivitySnapshot(long ActivityId, int EquipmentId, string ActivityStatus,
    byte[] VersionToken, string DatabaseName);
public sealed record ActivitySnapshotReply(Guid RequestId, bool Success, string ErrorCode,
    ActivitySnapshot? Snapshot);

public sealed record VerifiedActivitySigner(string Username, bool IsActive, bool IsLocked,
    bool MustChangePassword, bool CanReviewResults, bool CanApproveResults, byte[] AuthenticationRowVersion);

public interface ITrustedActivityAuthenticator
{
    Task<VerifiedActivitySigner?> AuthenticateAsync(string boundUsername, string password, CancellationToken cancellationToken);
}

public interface ITrustedActivitySignatureStore
{
    Task<ActivitySnapshot> ReadSnapshotAsync(ActivitySnapshotRequest request, CancellationToken cancellationToken);
    Task CommitOnceAsync(ActivitySigningRequest request, VerifiedActivitySigner signer, CancellationToken cancellationToken);
}

public sealed class TrustedActivitySigningCoordinator(
    ITrustedActivityAuthenticator authentication, ITrustedActivitySignatureStore store, TimeProvider? clock = null)
{
    public Task<ActivitySnapshot> ReadSnapshotAsync(ActivitySnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSnapshotShape(request);
        return store.ReadSnapshotAsync(request, cancellationToken);
    }

    public async Task CommitAsync(ActivitySigningRequest request, string boundUsername, string secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateShape(request, (clock ?? TimeProvider.System).GetUtcNow());
        if (string.IsNullOrWhiteSpace(boundUsername) || boundUsername.Length > 100 ||
            string.IsNullOrEmpty(secret) || secret.Length > 4096)
            throw new UnauthorizedAccessException("A Windows-bound account and fresh password are required.");

        var signer = await authentication.AuthenticateAsync(boundUsername, secret, cancellationToken).ConfigureAwait(false);
        if (signer is null || !string.Equals(signer.Username, boundUsername, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The independently verified account cannot sign this activity stage.");

        await CommitVerifiedAsync(request, signer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Commits an activity stage after the host has already performed canonical
    /// Windows-bound PharmaLIMS reauthentication through the trusted application
    /// endpoint. This prevents activity signing from maintaining a second password
    /// verifier that can drift from Login/Change Password semantics.
    /// </summary>
    public async Task CommitVerifiedAsync(ActivitySigningRequest request, VerifiedActivitySigner signer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(signer);
        ValidateShape(request, (clock ?? TimeProvider.System).GetUtcNow());

        if (string.IsNullOrWhiteSpace(signer.Username) || signer.Username.Length > 100 ||
            !signer.IsActive || signer.IsLocked || signer.MustChangePassword ||
            signer.AuthenticationRowVersion is not { Length: 8 } ||
            (request.Stage == "Reviewed" ? !signer.CanReviewResults : !signer.CanApproveResults))
        {
            throw new UnauthorizedAccessException("The verified account cannot sign this activity stage.");
        }

        await store.CommitOnceAsync(request, signer, cancellationToken).ConfigureAwait(false);
    }

    public static void ValidateSnapshotShape(ActivitySnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty || request.ActivityId <= 0 || request.EquipmentId <= 0 ||
            request.Stage is not ("Reviewed" or "Approved"))
            throw new ArgumentException("Invalid isolated UAT activity snapshot request.");
    }

    public static void ValidateShape(ActivitySigningRequest request, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty || request.ActivityId <= 0 ||
            request.Stage is not ("Reviewed" or "Approved") ||
            request.ExpectedVersion is not { Length: 8 } || !request.ExpectedVersion.Any(value => value != 0) ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 10 ||
            request.Reason.Length > 500 || request.Reason.Any(char.IsControl) ||
            !ActivitySigningDatabasePolicy.IsAllowedUatDatabase(request.DatabaseName))
            throw new ArgumentException("Invalid isolated UAT activity signature request.");
        if (request.IssuedAtUtc.Offset != TimeSpan.Zero || request.IssuedAtUtc > nowUtc ||
            nowUtc - request.IssuedAtUtc > TimeSpan.FromMinutes(2))
            throw new UnauthorizedAccessException("Activity signing request is expired or invalid.");
    }
}


public static class ActivitySigningDatabasePolicy
{
    public static bool IsAllowedUatDatabase(string? databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName) || databaseName.Length > 128 ||
            !databaseName.All(value => char.IsAsciiLetterOrDigit(value) || value == '_'))
            return false;

        return databaseName.StartsWith("EQ001_UAT_", StringComparison.Ordinal) ||
               databaseName.StartsWith("PharmaLIMS_EQ001_UAT_", StringComparison.Ordinal) ||
               databaseName.StartsWith("PharmaLIMS_UAT_", StringComparison.Ordinal) ||
               databaseName.StartsWith("PharmaLIMS_Integration_", StringComparison.Ordinal);
    }
}
