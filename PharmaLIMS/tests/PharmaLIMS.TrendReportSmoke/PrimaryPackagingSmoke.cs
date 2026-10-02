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
        VerifyWorkflowReset();
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
    private static void VerifyWorkflowReset()
    {
        var window = new ProductionRawMaterialSamples(); // Never shown: no database startup.
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = window.GetType();
        T Control<T>(string name) => (T)window.FindName(name);
        var categories = new[] { "Primary Packaging", "Raw Material", "Production / In-Process", "Stability" };
        foreach (string category in categories)
        {
            Control<System.Windows.Controls.TextBox>("TxtMaterialCode").Text = "AL";
            Control<System.Windows.Controls.TextBox>("TxtSupplier").Text = "Previous supplier";
            Control<System.Windows.Controls.TextBox>("TxtSampleQty").Text = "99";
            Control<System.Windows.Controls.ComboBox>("CmbMaterialType").Text = "Aluminium Foil";
            Control<System.Windows.Controls.ComboBox>("CmbStabilityStudyType").SelectedIndex = 1;
            type.GetField("_selectedSampleId", flags)!.SetValue(window, 123);
            type.GetMethod("SelectWorkflow", flags)!.Invoke(window, new object[] { category });
            if (Control<System.Windows.Controls.TextBox>("TxtMaterialCode").Text != "" ||
                Control<System.Windows.Controls.TextBox>("TxtSupplier").Text != "" ||
                Control<System.Windows.Controls.TextBox>("TxtSampleQty").Text != "" ||
                Control<System.Windows.Controls.ComboBox>("CmbMaterialType").Text != "" ||
                Control<System.Windows.Controls.ComboBox>("CmbStabilityStudyType").SelectedIndex != -1 ||
                (int)type.GetField("_selectedSampleId", flags)!.GetValue(window)! != 0)
                throw new InvalidOperationException("Workflow leaked previous sample fields: " + category);
        }
        window.Close();
        Console.WriteLine("PASS PRM workflow reset: packaging, raw, production and stability do not inherit sample fields.");
    }

}
