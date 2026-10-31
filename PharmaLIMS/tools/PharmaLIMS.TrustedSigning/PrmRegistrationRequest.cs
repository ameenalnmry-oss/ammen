using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace PharmaLIMS.TrustedSigning;

public sealed record PrmRegistrationRequest
{
    public string SampleCategory { get; init; } = "";
    public string SamplePurpose { get; init; } = "";
    public string MaterialCode { get; init; } = "";
    public string MaterialName { get; init; } = "";
    public string MaterialType { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Supplier { get; init; } = "";
    public string ManufacturerLotNo { get; init; } = "";
    public string SupplierLotNo { get; init; } = "";
    public string GRNNo { get; init; } = "";
    public DateTime? ReceivedDate { get; init; }
    public DateTime? ManufacturerExpiryDate { get; init; }
    public DateTime? RetestDate { get; init; }
    public string RetestReason { get; init; } = "";
    public string PreviousReportNo { get; init; } = "";
    public DateTime? PreviousRetestDate { get; init; }
    public DateTime? ProposedRetestDate { get; init; }
    public string RetestRemarks { get; init; } = "";
    public string ProductCode { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string BatchNo { get; init; } = "";
    public string BatchSize { get; init; } = "";
    public string DosageForm { get; init; } = "";
    public string ProductionStage { get; init; } = "";
    public string SampleSource { get; init; } = "";
    public string SampledFrom { get; init; } = "";
    public string MachineLineNo { get; init; } = "";
    public DateTime? ManufacturingDate { get; init; }
    public DateTime? PackagingDate { get; init; }
    public DateTime? ExpiryDate { get; init; }
    public string PackSize { get; init; } = "";
    public string SpecificationNo { get; init; } = "";
    public string TestsRequired { get; init; } = "";
    public decimal? SampleQuantity { get; init; }
    public string Unit { get; init; } = "";
    public string StorageCondition { get; init; } = "";
    public string StabilityChamberNo { get; init; } = "";
    public string StabilityProtocolNo { get; init; } = "";
    public DateTime SampleDateTime { get; init; }
    public string SampledBy { get; init; } = "";
    public string Department { get; init; } = "";
    public string Remarks { get; init; } = "";
}
public sealed record PrmRegistrationResult(Guid RequestId,string State,int SampleId,string SampleNumber,string Status,int TestCount);
public static class PrmRegistrationPolicy
{
    public const string Database="EQ001_UAT_AMEEN_20261024";
    public const string Server="tcp:AMEEN,1433";
    public static void RequireTarget(string connectionString,bool requireIntegratedSecurity=true)
    {
        var c=new SqlConnectionStringBuilder(connectionString);
        if(!string.Equals(c.DataSource,Server,StringComparison.OrdinalIgnoreCase) || c.InitialCatalog!=Database ||
            (requireIntegratedSecurity && !c.IntegratedSecurity) || c.Encrypt==SqlConnectionEncryptOption.Optional || c.TrustServerCertificate)
            throw new ArgumentException("PRM registration requires the exact validated Windows-authenticated EQ001 UAT target.");
    }
    public static PrmRegistrationRequest Parse(string json)
    {
        using var document=JsonDocument.Parse(json);
        if(document.RootElement.ValueKind!=JsonValueKind.Object || document.RootElement.EnumerateObject().GroupBy(p=>p.Name,StringComparer.Ordinal).Any(g=>g.Count()!=1))
            throw new ArgumentException("PRM payload must be an object with unique fields.");
        var r=JsonSerializer.Deserialize<PrmRegistrationRequest>(json,new JsonSerializerOptions { UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow })
            ?? throw new ArgumentException("Missing PRM registration.");
        Validate(r); return r;
    }
    public static void Validate(PrmRegistrationRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if(r.SampleCategory is not ("Raw Material" or "Primary Packaging" or "Production / In-Process" or "Finished Product" or "Stability"))
            throw new ArgumentException("Unknown PRM sample category.");
        if(r.SampleDateTime<new DateTime(1753,1,1) || r.SampleDateTime.Kind!=DateTimeKind.Unspecified || r.SampleDateTime.Ticks%TimeSpan.TicksPerSecond!=0)
            throw new ArgumentException("PRM sampling requires an exact local laboratory timestamp.");
        Required(r.SpecificationNo); Required(r.SampledBy);
        if(r.SampleQuantity is decimal q && (q<=0 || q>999999999999999.999m || decimal.Round(q,3)!=q || string.IsNullOrWhiteSpace(r.Unit)))
            throw new ArgumentException("PRM quantity must fit decimal(18,3) exactly and requires a unit.");
        if(r.SampleCategory is "Raw Material" or "Primary Packaging")
        {
            Required(r.MaterialCode); Required(r.MaterialName); Required(r.ManufacturerLotNo); Required(r.SamplePurpose);
            if(r.SamplePurpose=="Retest") Required(r.RetestReason);
        }
        else { Required(r.ProductCode); Required(r.ProductName); Required(r.BatchNo); }
        if(r.SampleCategory=="Production / In-Process") Required(r.ProductionStage);
        if(r.SampleCategory=="Stability") { Required(r.ProductionStage); Required(r.SampleSource); Required(r.StorageCondition); Required(r.StabilityChamberNo); Required(r.StabilityProtocolNo); }
        Text(r.SampleCategory,40);
        Text(r.SamplePurpose,80);
        Text(r.MaterialCode,80);
        Text(r.MaterialName,200);
        Text(r.MaterialType,80);
        Text(r.Manufacturer,160);
        Text(r.Supplier,160);
        Text(r.ManufacturerLotNo,120);
        Text(r.SupplierLotNo,120);
        Text(r.GRNNo,120);
        Text(r.RetestReason,300);
        Text(r.PreviousReportNo,80);
        Text(r.RetestRemarks,1000);
        Text(r.ProductCode,80);
        Text(r.ProductName,200);
        Text(r.BatchNo,120);
        Text(r.BatchSize,80);
        Text(r.DosageForm,80);
        Text(r.ProductionStage,80);
        Text(r.SampleSource,120);
        Text(r.SampledFrom,160);
        Text(r.MachineLineNo,120);
        Text(r.PackSize,120);
        Text(r.SpecificationNo,120);
        Text(r.TestsRequired,1000);
        Text(r.Unit,30);
        Text(r.StorageCondition,160);
        Text(r.StabilityChamberNo,120);
        Text(r.StabilityProtocolNo,120);
        Text(r.SampledBy,120);
        Text(r.Department,120);
        Text(r.Remarks,1000);
        Date(r.ReceivedDate);
        Date(r.ManufacturerExpiryDate);
        Date(r.RetestDate);
        Date(r.PreviousRetestDate);
        Date(r.ProposedRetestDate);
        Date(r.ManufacturingDate);
        Date(r.PackagingDate);
        Date(r.ExpiryDate);
    }
    public static string CanonicalPayload(PrmRegistrationRequest r)
    {
        Validate(r); string json=JsonSerializer.Serialize(r);
        if(Encoding.Unicode.GetByteCount(json)>32768) throw new ArgumentException("PRM registration exceeds the supported request size; reduce the input before saving.");
        return json;
    }
    public static string PayloadHash(PrmRegistrationRequest r)=>Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(CanonicalPayload(r))));
    private static void Required(string? s) { if(string.IsNullOrWhiteSpace(s)) throw new ArgumentException("Required PRM registration information is missing."); }
    private static void Text(string? s,int max) { if(s is null || s.Length>max || s.Any(c=>char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw new ArgumentException("Invalid or oversized PRM text."); }
    private static void Date(DateTime? d) { if(d is DateTime x && (x.Date!=x || x.Kind!=DateTimeKind.Unspecified)) throw new ArgumentException("PRM date-only values must be local dates."); }
}

/// <summary>Converts desktop calendar values to laboratory wall-clock values before strict transport validation.</summary>
public static class PrmRegistrationFormMapping
{
    public static PrmRegistrationRequest Capture(IReadOnlyDictionary<string,object?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var localFields=fields.ToDictionary(p=>p.Key,p=>p.Value is DateTime value
            ? (object)DateTime.SpecifyKind(value,DateTimeKind.Unspecified) : p.Value,StringComparer.Ordinal);
        return PrmRegistrationPolicy.Parse(JsonSerializer.Serialize(localFields));
    }
}
