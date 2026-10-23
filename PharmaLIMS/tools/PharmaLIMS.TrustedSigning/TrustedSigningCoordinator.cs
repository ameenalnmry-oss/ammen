using System;
using System.Threading;
using System.Threading.Tasks;

namespace PharmaLIMS.TrustedSigning;

/// <summary>
/// Trusted-host-only orchestration. The desktop client must never implement these
/// interfaces or supply an identity/authorization result for itself.
/// </summary>
public sealed record EquipmentSigningRequest(
    Guid RequestId,
    string Action,
    string EquipmentCode,
    string Reason,
    DateTimeOffset IssuedAtUtc,
    byte[]? ExpectedRowVersion,
    string RequestedEquipmentStateJson);

public sealed record VerifiedSigner(
    string Username,
    bool IsActive,
    bool IsLocked,
    bool MustChangePassword,
    bool CanManageSettings);

/// <summary>
/// Implementation must retrieve the signer from an independently authenticated
/// trusted-host security context, verify a fresh credential, and commit account
/// lockout/failure counters. Never trust a username or role supplied by WPF.
/// </summary>
public interface ITrustedSignerAuthenticator
{
    Task<VerifiedSigner?> AuthenticateForSignatureAsync(
        string reauthenticationSecret,
        CancellationToken cancellationToken);
}

/// <summary>
/// The implementation must atomically reject repeated RequestIds, verify the
/// equipment RowVersion, derive old and new state evidence from the database
/// inside the same transaction, persist the equipment change and immutable
/// signature, and enforce authorization using a SQL principal inaccessible to WPF.
/// The request's RequestedEquipmentStateJson is proposed input, NEVER evidence.
/// </summary>
public interface ITrustedEquipmentSignatureStore
{
    Task CommitOnceAsync(
        EquipmentSigningRequest request,
        VerifiedSigner signer,
        CancellationToken cancellationToken);
}

public sealed class TrustedSigningCoordinator
{
    private readonly ITrustedSignerAuthenticator _authenticator;
    private readonly ITrustedEquipmentSignatureStore _store;
    private readonly TimeProvider _clock;

    public TrustedSigningCoordinator(
        ITrustedSignerAuthenticator authenticator,
        ITrustedEquipmentSignatureStore store,
        TimeProvider? clock = null)
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task CommitEquipmentSignatureAsync(
        EquipmentSigningRequest request,
        string reauthenticationSecret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Reject malformed requests before consuming any authentication attempt.
        SigningRequestPolicy.ValidateRequestShape(
            request.Action, request.EquipmentCode, request.Reason,
            request.RequestId, request.IssuedAtUtc, _clock.GetUtcNow());
        if (string.IsNullOrWhiteSpace(reauthenticationSecret))
            throw new UnauthorizedAccessException("A fresh EQ-001 credential is required.");
        if (request.Action == SigningRequestPolicy.EquipmentUpdate &&
            request.ExpectedRowVersion?.Length != 8)
            throw new ArgumentException("EQ-001 update requires an exact SQL rowversion.");
        if (request.Action == SigningRequestPolicy.EquipmentCreate &&
            request.ExpectedRowVersion is { Length: > 0 })
            throw new ArgumentException("EQ-001 create must not supply a previous rowversion.");
        if (string.IsNullOrWhiteSpace(request.RequestedEquipmentStateJson) ||
            request.RequestedEquipmentStateJson.Length > 32768)
            throw new ArgumentException("EQ-001 requires bounded proposed equipment data.");

        // Revalidate after independent reauthentication; do not use WPF session
        // flags, workstation timestamps, or a user-supplied signer identity.
        VerifiedSigner? signer = await _authenticator.AuthenticateForSignatureAsync(
            reauthenticationSecret, cancellationToken).ConfigureAwait(false);
        if (signer is null)
            throw new UnauthorizedAccessException("EQ-001 reauthentication failed.");

        SigningRequestPolicy.Validate(
            signer.Username, signer.IsActive, signer.IsLocked,
            signer.MustChangePassword, signer.CanManageSettings,
            request.Action, request.EquipmentCode, request.Reason,
            request.RequestId, request.IssuedAtUtc, _clock.GetUtcNow());

        // ONLY a separately privileged, transactional store may sign. If it is
        // unavailable, the operation fails closed; no WPF SQL fallback is allowed.
        await _store.CommitOnceAsync(request, signer, cancellationToken)
            .ConfigureAwait(false);
    }
}
