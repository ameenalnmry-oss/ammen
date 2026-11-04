using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PharmaLIMS.TrustedSigning;

public sealed record EquipmentLinkRequest(long ActivityId, string Module, int[] ParentIds, int? ResultId);
public sealed record EquipmentLinkReceipt(Guid RequestId, long ActivityId, int LinkCount, string State);

public static class EquipmentLinkContract
{
    private static readonly JsonSerializerOptions Strict = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8
    };

    public static string CanonicalPayload(EquipmentLinkRequest request)
    {
        if (request is null || request.ActivityId <= 0 ||
            request.Module is not ("PRM" or "EM" or "WATER") ||
            request.ParentIds is null || request.ParentIds.Length is < 1 or > 100 ||
            request.ParentIds.Any(x => x <= 0) ||
            request.ParentIds.Distinct().Count() != request.ParentIds.Length ||
            !request.ParentIds.SequenceEqual(request.ParentIds.OrderBy(x => x)) ||
            request.ResultId.HasValue && (request.ResultId <= 0 || request.ParentIds.Length != 1))
            throw new ArgumentException("Invalid equipment-link identity or ordered sample selection.");
        return JsonSerializer.Serialize(request, Strict);
    }

    public static EquipmentLinkRequest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 8192)
            throw new ArgumentException("Invalid equipment-link payload size.");
        var request = JsonSerializer.Deserialize<EquipmentLinkRequest>(json, Strict)
            ?? throw new ArgumentException("Missing equipment-link payload.");
        if (!string.Equals(CanonicalPayload(request), json, StringComparison.Ordinal))
            throw new ArgumentException("Noncanonical equipment-link payload.");
        return request;
    }
}
