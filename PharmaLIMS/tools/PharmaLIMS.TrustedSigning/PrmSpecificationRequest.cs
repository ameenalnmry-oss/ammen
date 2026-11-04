using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace PharmaLIMS.TrustedSigning;

public sealed record PrmSpecificationTest(string TestCode, string TestName, string SpecificationText,
    decimal? SpecificationLimit, bool IsDefaultForCategory, string Unit, string ResultType,
    bool RequiredTest, decimal MinimumElapsedHours, int SortOrder);

public sealed record PrmSpecificationRequest(string SpecificationNo, string Category, int Version,
    string Reference, string ItemCode, string ProductionStage, DateTime? EffectiveDate,
    string ExpectedSnapshotHash, PrmSpecificationTest[] Tests,
    int SupersedesVersion = 0, string SupersededEvidenceHash = "");

public static class PrmSpecificationPolicy
{
    public static void Validate(PrmSpecificationRequest request, string action)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (action is not ("PrmSaveDraft" or "PrmReview" or "PrmApprove")) throw new ArgumentException("Unknown PRM action.");
        Text(request.SpecificationNo,120); Text(request.Category,40); Text(request.Reference,160); Text(request.ItemCode,80);
        Text(request.ProductionStage,80,request.Category != "Production / In-Process");
        if (request.Category is not ("Raw Material" or "Primary Packaging" or "Production / In-Process" or "Finished Product" or "Stability"))
            throw new ArgumentException("Unknown PRM category.");
        if (request.Version < 0 || (action != "PrmSaveDraft" && request.Version == 0) || request.ExpectedSnapshotHash is null ||
            (request.Version > 0 && (request.ExpectedSnapshotHash.Length != 64 || !request.ExpectedSnapshotHash.All(Uri.IsHexDigit))))
            throw new ArgumentException("A complete saved snapshot is required.");
        if (request.SupersedesVersion < 0 || request.SupersededEvidenceHash is null ||
            (request.SupersedesVersion == 0 && request.SupersededEvidenceHash.Length != 0) ||
            (request.SupersedesVersion > 0 && (action != "PrmApprove" || request.SupersedesVersion >= request.Version ||
                request.SupersededEvidenceHash.Length != 64 || !request.SupersededEvidenceHash.All(Uri.IsHexDigit))))
            throw new ArgumentException("Explicit approval and a complete predecessor evidence hash are required for supersession.");
        if (request.Tests is null || request.Tests.Length is < 1 or > 16) throw new ArgumentException("One to sixteen tests are required.");
        var codes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var test in request.Tests)
        {
            if(test is null) throw new ArgumentException("Missing test.");
            Text(test.TestCode,40); Text(test.TestName,160); Text(test.SpecificationText,500); Text(test.Unit,50,true);
            if(!codes.Add(test.TestCode.Trim()) || test.ResultType is not ("Numeric" or "Qualitative" or "Text") ||
                test.MinimumElapsedHours < 0 || test.MinimumElapsedHours > 9999999.99m || decimal.Round(test.MinimumElapsedHours,2)!=test.MinimumElapsedHours ||
                (test.RequiredTest && test.MinimumElapsedHours<=0) || test.SortOrder < 0 ||
                (test.SpecificationLimit is decimal value && (value<0 || value>999999999999999.999m || decimal.Round(value,3)!=value)))
                throw new ArgumentException("Invalid or inexact specification test.");
        }
    }

    private static void Text(string value,int limit,bool empty=false)
    {
        if(value is null || value.Length>limit || (!empty && string.IsNullOrWhiteSpace(value)) || value.Any(char.IsControl))
            throw new ArgumentException("Invalid PRM text field.");
    }

    public static string SnapshotHash(DataTable rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if(!rows.Columns.Contains("SpecificationTestID") || rows.Rows.Count is < 1 or > 16)
            throw new ArgumentException("Invalid complete specification snapshot.");
        var columns=rows.Columns.Cast<DataColumn>().OrderBy(x=>x.ColumnName,StringComparer.Ordinal).ToArray();
        var values=new List<string?[]> { columns.Select(x=>x.ColumnName+":"+x.DataType.FullName).ToArray() };
        var keys=new HashSet<int>();
        foreach(var row in rows.Rows.Cast<DataRow>().OrderBy(x=>Convert.ToInt32(x["SpecificationTestID"],CultureInfo.InvariantCulture)))
        {
            if(row.RowState==DataRowState.Deleted || !keys.Add(Convert.ToInt32(row["SpecificationTestID"],CultureInfo.InvariantCulture)))
                throw new ArgumentException("Invalid snapshot identity.");
            values.Add(columns.Select(c=>row[c] switch {
                DBNull=>null, DateTime date=>date.ToString("O",CultureInfo.InvariantCulture),
                byte[] bytes=>Convert.ToBase64String(bytes),
                object value=>Convert.ToString(value,CultureInfo.InvariantCulture)
            }).ToArray());
        }
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values)));
    }
}
