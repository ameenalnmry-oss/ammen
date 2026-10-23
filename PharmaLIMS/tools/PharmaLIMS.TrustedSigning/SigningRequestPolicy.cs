using System;
using System.Collections.Generic;

namespace PharmaLIMS.TrustedSigning;

/// <summary>
/// Server-side policy for EQ-001 equipment signatures. This policy is a
/// prerequisite, not an authentication or persistence authority.
/// The trusted service must obtain identity from its own authenticated session,
/// not from the WPF request's display name.
/// </summary>
public static class SigningRequestPolicy
{
    public const string EquipmentCreate = "EquipmentCreate";
    public const string EquipmentUpdate = "EquipmentUpdate";

    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal)
    {
        EquipmentCreate,
        EquipmentUpdate
    };

    public static void Validate(
        string? verifiedPrincipal,
        bool principalActive,
        bool principalLocked,
        bool mustChangePassword,
        bool canManageSettings,
        string? action,
        string? equipmentCode,
        string? reason,
        Guid requestId,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(verifiedPrincipal) ||
            !principalActive || principalLocked || mustChangePassword || !canManageSettings)
            throw new UnauthorizedAccessException("EQ-001 trusted signer is not authorized.");

        if (action is null || !AllowedActions.Contains(action))
            throw new UnauthorizedAccessException("EQ-001 signing action is not allowed.");

        if (string.IsNullOrWhiteSpace(equipmentCode) || equipmentCode.Length > 50 ||
            string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10 || reason.Length > 500)
            throw new ArgumentException("EQ-001 equipment identity and action reason are required.");

        if (requestId == Guid.Empty)
            throw new ArgumentException("EQ-001 requires a unique signing request identifier.");

        if (issuedAtUtc.Offset != TimeSpan.Zero || nowUtc.Offset != TimeSpan.Zero ||
            issuedAtUtc > nowUtc || nowUtc - issuedAtUtc > TimeSpan.FromMinutes(2))
            throw new UnauthorizedAccessException("EQ-001 signing request is expired or invalid.");
    }
}
