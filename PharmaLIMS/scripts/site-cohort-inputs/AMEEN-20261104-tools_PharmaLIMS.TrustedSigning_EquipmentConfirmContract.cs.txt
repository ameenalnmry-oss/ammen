using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PharmaLIMS.TrustedSigning;

public sealed record EquipmentConfirmRequest(long ActivityId, byte[] ExpectedVersion,
    DateTimeOffset Started, DateTimeOffset Ended);
public sealed record EquipmentConfirmReceipt(Guid RequestId, long ActivityId, string State);

public static class EquipmentConfirmContract
{
    private static readonly JsonSerializerOptions Strict = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8
    };

    public static string CanonicalPayload(EquipmentConfirmRequest request)
    {
        if (request is null || request.ActivityId <= 0 ||
            request.ExpectedVersion is null || request.ExpectedVersion.Length != 8 ||
            request.Started == default || request.Ended == default ||
            request.Ended < request.Started ||
            request.Ended - request.Started > TimeSpan.FromDays(365) ||
            request.Started > DateTimeOffset.UtcNow.AddDays(1))
            throw new ArgumentException("Invalid equipment use evidence, rowversion, or timestamps.");
        return JsonSerializer.Serialize(request, Strict);
    }

    public static EquipmentConfirmRequest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096)
            throw new ArgumentException("Invalid equipment confirmation payload size.");
        var request = JsonSerializer.Deserialize<EquipmentConfirmRequest>(json, Strict)
            ?? throw new ArgumentException("Missing equipment confirmation.");
        if (!string.Equals(CanonicalPayload(request), json, StringComparison.Ordinal))
            throw new ArgumentException("Noncanonical equipment confirmation payload.");
        return request;
    }
}
