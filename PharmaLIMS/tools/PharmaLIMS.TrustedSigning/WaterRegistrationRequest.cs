using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PharmaLIMS.TrustedSigning;

public sealed record WaterRegistrationRequest(int PointId, string WaterType, int[] TestIds,
    DateTime SamplingAt, DateTime ReceivedAt, DateTime? IncubationAt,
    string SampledBy, string ReceivedBy, int ContainerCount, string SampleVolume,
    string ContainerCondition, string ReceiptTemperature, string ReceiptDecision,
    string ReceiptReason, string IncubationProgram, string IncubatorId, int? WaterPlanSampleId);

public sealed record WaterRegistrationResult(Guid RequestId, string State, int SampleId,
    string SampleNumber, string Status, int TestCount, bool LabelPrinted);

public static class WaterRegistrationPolicy
{
    public const string Database = "EQ001_UAT_AMEEN_20261024";
    public const string Server = "tcp:AMEEN,1433";
    public static void RequireTarget(string connectionString)
    {
        var c = new SqlConnectionStringBuilder(connectionString);
        if (!string.Equals(c.DataSource, Server, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(c.InitialCatalog, Database, StringComparison.Ordinal) ||
            !c.IntegratedSecurity || c.Encrypt == SqlConnectionEncryptOption.Optional || c.TrustServerCertificate)
            throw new ArgumentException("Water registration requires the exact validated Windows-authenticated EQ001 UAT target.");
    }

    public static void Validate(WaterRegistrationRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.PointId <= 0 || r.WaterType is not ("Purified Water" or "Potable Water") ||
            r.TestIds is null || r.TestIds.Length is < 1 or > 64 || r.TestIds.Any(x => x <= 0) ||
            r.TestIds.Distinct().Count() != r.TestIds.Length || r.ContainerCount <= 0 || r.WaterPlanSampleId <= 0)
            throw new ArgumentException("Invalid Water registration identity or test selection.");
        if (r.SamplingAt < new DateTime(1753,1,1) || r.ReceivedAt < r.SamplingAt ||
            r.IncubationAt is DateTime start && start < r.ReceivedAt ||
            r.SamplingAt.Kind != DateTimeKind.Unspecified || r.ReceivedAt.Kind != DateTimeKind.Unspecified ||
            r.IncubationAt is DateTime at && at.Kind != DateTimeKind.Unspecified)
            throw new ArgumentException("Water timestamps must be actual local laboratory times in sampling/receipt/incubation order.");
        Text(r.SampledBy,100); Text(r.ReceivedBy,100); Text(r.SampleVolume,50);
        Text(r.ContainerCondition,100); Text(r.ReceiptTemperature,50);
        Text(r.ReceiptReason,2000,true,true); Text(r.IncubatorId,100,true); Text(r.IncubationProgram,200,true);
        if (r.ReceiptDecision is not ("Accepted" or "Rejected") ||
            r.ReceiptDecision == "Rejected" && string.IsNullOrWhiteSpace(r.ReceiptReason))
            throw new ArgumentException("A controlled receipt decision and rejection reason are required.");
        if (r.IncubationAt.HasValue)
        {
            if (r.ReceiptDecision != "Accepted" || string.IsNullOrWhiteSpace(r.IncubatorId))
                throw new ArgumentException("Accepted incubation requires an instrument.");
            _ = IncubationDays(r.IncubationProgram);
        }
        else if (!string.IsNullOrEmpty(r.IncubatorId) || !string.IsNullOrEmpty(r.IncubationProgram))
            throw new ArgumentException("Incubation details require an actual incubation start.");
    }

    public static int IncubationDays(string program) => program switch
    {
        "Water TAMC — 30–35 °C / 5 days" => 5,
        "TAMC — 30–35 °C / 3–5 days" => 5,
        "TYMC — 20–25 °C / 5–7 days" => 7,
        _ => throw new ArgumentException("Select a controlled Water incubation program.")
    };

    public static decimal? RequireExactLimit(decimal? value)
    {
        if (value is decimal n && (n < 0 || n > 99999999999999.9999m || decimal.Round(n,4) != n))
            throw new ArgumentException("Controlled Water limits must fit the exact decimal(18,4) sample snapshot; no rounding is permitted.");
        return value;
    }

    public static string CanonicalPayload(WaterRegistrationRequest r)
    {
        Validate(r);
        return JsonSerializer.Serialize(r with { TestIds = r.TestIds.Order().ToArray() });
    }
    public static string PayloadHash(WaterRegistrationRequest r) =>
        Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(CanonicalPayload(r))));

    public static void ValidateSnapshot(string name,string unit,string specification,decimal? alert,decimal? action)
    {
        RequireExactLimit(alert); RequireExactLimit(action);
        if (string.IsNullOrWhiteSpace(specification) ||
            PharmaLIMS.Services.WaterNumericResultEvaluator.RequiresNumericLimits(name,unit,specification) &&
            !PharmaLIMS.Services.WaterNumericResultEvaluator.HasCompleteLimits(name,alert,action))
            throw new ArgumentException("The effective approved Water numeric specification is incomplete.");
    }

    private static void Text(string? s,int max,bool empty=false,bool multiline=false)
    {
        if (s is null || s.Length > max || (!empty && string.IsNullOrWhiteSpace(s)) ||
            s.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t')))
            throw new ArgumentException("Invalid or oversized Water registration text.");
    }
}
