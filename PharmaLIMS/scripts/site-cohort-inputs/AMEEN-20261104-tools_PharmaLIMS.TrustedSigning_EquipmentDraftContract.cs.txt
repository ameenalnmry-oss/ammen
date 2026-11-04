using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace PharmaLIMS.TrustedSigning;

public sealed record EquipmentDraftRequest(int EquipmentId, string ActivityType, string MethodReference);

public sealed record EquipmentDraftReceipt(Guid RequestId, long ActivityId, string State);

public static class EquipmentDraftContract
{
    private static readonly string[] AllowedTypes =
    {
        "Use", "Incubation", "Calibration", "Periodic Verification",
        "Cleaning", "Sanitization", "Maintenance", "Breakdown", "Repair"
    };

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8
    };

    public static void ValidateTarget(string connectionString)
    {
        var connection = new SqlConnectionStringBuilder(connectionString);
        if (!string.Equals(connection.InitialCatalog, "EQ001_UAT_AMEEN_20261024", StringComparison.Ordinal)
            || !connection.IntegratedSecurity)
            throw new UnauthorizedAccessException("Equipment draft service is limited to isolated EQ001 UAT.");
    }

    public static string CanonicalPayload(EquipmentDraftRequest request)
    {
        if (request is null || request.EquipmentId <= 0 ||
            Array.IndexOf(AllowedTypes, request.ActivityType) < 0 ||
            string.IsNullOrWhiteSpace(request.MethodReference) ||
            request.MethodReference.Length > 200 ||
            request.MethodReference != request.MethodReference.Trim())
            throw new ArgumentException("Invalid equipment draft request.");
        return JsonSerializer.Serialize(request, StrictJson);
    }

    public static EquipmentDraftRequest Parse(string payload)
    {
        if (payload is null || payload.Length > 2048)
            throw new ArgumentException("Invalid equipment draft payload.");
        var request = JsonSerializer.Deserialize<EquipmentDraftRequest>(payload, StrictJson)
                      ?? throw new ArgumentException("Missing equipment draft.");
        if (!string.Equals(CanonicalPayload(request), payload, StringComparison.Ordinal))
            throw new ArgumentException("Noncanonical equipment draft payload.");
        return request;
    }
}
