using System.Data;
using System.Reflection;
using PharmaLIMS;
using PharmaLIMS.Repositories;
using PharmaLIMS.Services.Investigations;

internal static class PrimaryPackagingSmoke
{
    internal static void Verify()
    {
        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("Primary packaging: " + message); }
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        string Prefix(string category) => (string)typeof(ProductionRawMaterialResults).GetMethod("GetCertificatePrefix", flags)!.Invoke(null, new object[] { category })!;
        Require(Prefix("Primary Packaging") == "COA-PP" && Prefix("Raw Material") == "COA-RM", "independent certificate numbering");
        Require(PRMQualityEventInvestigationService.ResolvePRMChecklistCategory("PP-2026-0001 | Primary Packaging | supplier lot") == "PRM General", "controlled general checklist without raw-material misclassification");
        var group = typeof(PrmSpecificationRepository).GetMethod("GetCategoryGroup", flags)!;
        Require(!Equals(group.Invoke(null, new object[] { "Primary Packaging" }), group.Invoke(null, new object[] { "Raw Material" })), "independent specification category");
        DataTable samples = new();
        foreach (string name in new[] { "SampleCategory", "SampleNumber", "MaterialCode", "MaterialName", "ManufacturerLotNo", "Manufacturer", "Supplier", "ProductName", "BatchNo", "ResultInterpretation" }) samples.Columns.Add(name);
        samples.Rows.Add("Primary Packaging", "PP-2026-0001", "PP-BOTTLE-01", "Fixture bottle", "PP-LOT-01", "Fixture manufacturer", "Fixture supplier", "WRONG-PRODUCT", "WRONG-BATCH", "Conforms");
        DataTable certificates = new(); certificates.Columns.Add("CertificateNumber"); certificates.Rows.Add("COA-PP-2026-0001");
        string html = PRMCertificateTemplate.Build(samples.Rows[0], new DataTable(), certificates.Rows[0], new DataTable());
        Require(html.Contains("CERTIFICATE OF ANALYSIS") && html.Contains("PRIMARY PACKAGING") && html.Contains("Primary Packaging Microbiological Certificate of Analysis"), "packaging COA titles");
        Require(html.Contains("Fixture bottle") && html.Contains("PP-BOTTLE-01") && html.Contains("PP-LOT-01") && html.Contains("Fixture supplier"), "packaging traceability");
        Require(!html.Contains("WRONG-PRODUCT") && !html.Contains("WRONG-BATCH"), "product fields do not replace material identity");
        Console.WriteLine("PASS Primary Packaging: independent certificate/specification categories, material identity, lot, titles and controlled investigation checklist.");
    }
}
