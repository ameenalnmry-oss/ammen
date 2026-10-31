using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PharmaLIMS.TrustedSigning;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WaterWorkflowRequest(string TargetServer, string TargetDatabase, string Operation,
    int SampleId, string SampleNumber, string ExpectedStatus, string ExpectedSnapshot,
    DateTime? AnalysisStartedAt, WaterWorkflowTimes? Times, WaterWorkflowRow[] Results);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WaterWorkflowTimes(DateTime? SamplingDateTime, DateTime? ReceivedDateTime,
    DateTime? IncubationStartedDateTime, DateTime? IncubationEndDate, DateTime? AnalysisStartedDateTime);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WaterWorkflowRow(int SampleTestId, string ResultValue, string Remarks,
    int? EquipmentId, string SupportingEquipmentCodes, string TestKitCode, string TestKitLot,
    DateTime? TestKitExpiry, string LesSampleTemperatureC, string LesVerificationReference,
    bool LesVerificationConfirmed, string LesExecutionRemarks);
public sealed record WaterWorkflowResult(Guid RequestId, string State, int SampleId, string SampleNumber,
    string Status, long SignatureId, int CompletedTests, int PendingTests, string QualityEventSummary);

public static class WaterWorkflowPolicy
{
    public static bool IsOperation(string? operation) => operation is "StartAnalysis" or "SaveResults" or
        "SubmitReview" or "Review" or "Approve" or "CorrectTimes";
    public static void Validate(WaterWorkflowRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        Text(r.TargetServer,256); Text(r.TargetDatabase,128); Text(r.SampleNumber,100); Text(r.ExpectedStatus,50);
        if (!IsOperation(r.Operation) || r.SampleId <= 0 || r.ExpectedSnapshot is null ||
            !Regex.IsMatch(r.ExpectedSnapshot,"^[0-9A-Fa-f]{64}$",RegexOptions.CultureInvariant) ||
            r.Results is null || r.Results.Length > 128 || r.Results.Any(x=>x is null || x.SampleTestId<=0) ||
            r.Results.Select(x=>x.SampleTestId).Distinct().Count()!=r.Results.Length)
            throw new ArgumentException("Invalid Water workflow operation, identity, snapshot or result keys.");
        if (!(r.TargetDatabase.StartsWith("EQ001_UAT_",StringComparison.Ordinal) ||
              r.TargetDatabase.StartsWith("PharmaLIMS_EQ001_UAT_",StringComparison.Ordinal)))
            throw new ArgumentException("Water analysis is restricted to isolated UAT catalogs.");
        Date(r.AnalysisStartedAt);
        if (r.Operation=="StartAnalysis" ? !r.AnalysisStartedAt.HasValue : r.AnalysisStartedAt.HasValue)
            throw new ArgumentException("Only StartAnalysis accepts an analysis start time.");
        if ((r.Operation=="CorrectTimes") != (r.Times is not null))
            throw new ArgumentException("CorrectTimes requires the complete persisted timestamp shape.");
        if (r.Operation is "StartAnalysis" or "CorrectTimes" && r.Results.Length!=0)
            throw new ArgumentException("This operation does not accept result rows.");
        if (r.Times is { } t) { Date(t.SamplingDateTime); Date(t.ReceivedDateTime); Date(t.IncubationStartedDateTime); Date(t.IncubationEndDate); Date(t.AnalysisStartedDateTime); }
        foreach(var row in r.Results)
        {
            Text(row.ResultValue,200,true); Text(row.Remarks,4000,true,true);
            Text(row.SupportingEquipmentCodes,1000,true,true); Text(row.TestKitCode,100,true); Text(row.TestKitLot,100,true);
            Text(row.LesSampleTemperatureC,50,true); Text(row.LesVerificationReference,200,true); Text(row.LesExecutionRemarks,1000,true,true);
            if(row.EquipmentId<=0) throw new ArgumentException("Invalid equipment identity.");
            Date(row.TestKitExpiry);
        }
        if(JsonSerializer.Serialize(r).Length>32768) throw new ArgumentException("Water payload exceeds the transport limit.");
    }
    public static string CanonicalPayload(WaterWorkflowRequest r)
    {
        Validate(r);
        return JsonSerializer.Serialize(r with { ExpectedSnapshot=r.ExpectedSnapshot.ToUpperInvariant(), Results=r.Results.OrderBy(x=>x.SampleTestId).ToArray() });
    }
    public static string PayloadHash(WaterWorkflowRequest r) => Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(CanonicalPayload(r))));
    public static void RequireTarget(string connectionString, WaterWorkflowRequest r, bool requireIntegratedSecurity=true)
    {
        Validate(r);
        var c=new SqlConnectionStringBuilder(connectionString);
        if(!string.Equals(c.DataSource,r.TargetServer,StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(c.InitialCatalog,r.TargetDatabase,StringComparison.Ordinal) ||
           requireIntegratedSecurity && !c.IntegratedSecurity ||
           c.Encrypt==SqlConnectionEncryptOption.Optional || c.TrustServerCertificate)
            throw new ArgumentException("Water workflow requires the exact private TLS-validated UAT target and Windows service identity.");
    }
    private static void Date(DateTime? d)
    {
        if(d is DateTime t && (t.Kind!=DateTimeKind.Unspecified || t<new DateTime(1753,1,1)))
            throw new ArgumentException("Water times must be actual local laboratory times within the SQL datetime range.");
    }
    private static void Text(string? s,int max,bool empty=false,bool multiline=false)
    {
        if(s is null || s.Length>max || !empty && string.IsNullOrWhiteSpace(s) ||
            s.Any(c=>char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t')))
            throw new ArgumentException("Invalid or oversized Water workflow text.");
    }
}
