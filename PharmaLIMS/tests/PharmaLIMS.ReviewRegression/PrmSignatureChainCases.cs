using System.Data;
using PharmaLIMS.Services;

internal static partial class Program
{
    private static void RunPrmSignatureChainCases()
    {
        Run("PRM complete issuance signature cycle", () =>
            Equal(true, ValidChain(SignatureChain())));

        Run("PRM issuance rejects each missing stage", () =>
        {
            foreach (int missing in new[] { 0, 1, 2, 3 })
            {
                DataTable rows = SignatureChain();
                rows.Rows.RemoveAt(missing);
                Equal(false, ValidChain(rows));
            }
        });

        Run("PRM old approval cannot cover new results", () =>
        {
            DataTable rows = SignatureChain();
            rows.Rows.Add(5, 17, "Result Entry", "analyst", ChainTime(5));
            rows.Rows.Add(6, 17, "Certificate Issuance", "issuer", ChainTime(6));
            Equal(false, ValidChain(rows));
        });

        Run("PRM new review needs a new approval", () =>
        {
            DataTable rows = SignatureChain();
            rows.Rows.Add(5, 17, "Review", "reviewer", ChainTime(5));
            rows.Rows.Add(6, 17, "Certificate Issuance", "issuer", ChainTime(6));
            Equal(false, ValidChain(rows));
        });

        Run("PRM completed replacement cycle is valid", () =>
        {
            DataTable rows = SignatureChain();
            rows.Rows.Add(5, 17, "Result Entry", "analyst", ChainTime(5));
            rows.Rows.Add(6, 17, "Review", "reviewer", ChainTime(6));
            rows.Rows.Add(7, 17, "Approval", "qa", ChainTime(7));
            rows.Rows.Add(8, 17, "Certificate Reissue", "issuer", ChainTime(8));
            Equal(true, ValidChain(rows, "Certificate Reissue"));
            Equal(false, ValidChain(rows));
        });

        Run("PRM reissue retains the approved cycle", () =>
        {
            DataTable rows = SignatureChain();
            rows.Rows.Add(5, 17, "Certificate Cancellation", "issuer", ChainTime(5));
            rows.Rows.Add(6, 17, "Certificate Reissue", "issuer", ChainTime(6));
            Equal(true, ValidChain(rows, "Certificate Reissue"));
        });

        Run("PRM database order handles identical timestamps", () =>
        {
            DataTable rows = SignatureChain();
            foreach (DataRow row in rows.Rows)
                row["SignedAt"] = ChainTime(1);
            Equal(true, ValidChain(rows));
        });

        Run("PRM backwards signature time is rejected", () =>
        {
            DataTable rows = SignatureChain();
            rows.Rows[2]["SignedAt"] = ChainTime(1);
            Equal(false, ValidChain(rows));
        });

        Run("PRM missing identity or signing evidence is rejected", () =>
        {
            foreach (string column in new[] { "SignatureID", "SignedBy", "SignedAt" })
            {
                DataTable rows = SignatureChain();
                rows.Rows[1][column] = DBNull.Value;
                Equal(false, ValidChain(rows));
            }
            DataTable blankSigner = SignatureChain();
            blankSigner.Rows[2]["SignedBy"] = " ";
            Equal(false, ValidChain(blankSigner));
        });

        Run("PRM mixed samples and duplicate identities are rejected", () =>
        {
            DataTable rows = SignatureChain();
            rows.Rows[1]["SampleID"] = 18;
            Equal(false, ValidChain(rows));
            rows = SignatureChain();
            rows.Rows[1]["SignatureID"] = 1;
            Equal(false, ValidChain(rows));
        });

        Run("PRM incomplete signature schema is rejected", () =>
        {
            DataTable rows = SignatureChain();
            rows.Columns.Remove("SignedAt");
            Equal(false, ValidChain(rows));
        });

        Run("PRM signature cycle does not depend on table row order", () =>
        {
            DataTable original = SignatureChain();
            DataTable rows = original.Clone();
            for (int i = original.Rows.Count - 1; i >= 0; i--)
                rows.ImportRow(original.Rows[i]);
            Equal(true, ValidChain(rows));
        });
    }

    private static DateTime ChainTime(int minute) => new DateTime(2026, 10, 1, 1, minute, 0);

    private static bool ValidChain(DataTable rows, string action = "Certificate Issuance") =>
        PrmCertificateSignatureChain.IsCompleteForLatestResultCycle(rows, 17, action);

    private static DataTable SignatureChain()
    {
        var rows = new DataTable();
        rows.Columns.Add("SignatureID", typeof(int));
        rows.Columns.Add("SampleID", typeof(int));
        rows.Columns.Add("ActionType", typeof(string));
        rows.Columns.Add("SignedBy", typeof(string));
        rows.Columns.Add("SignedAt", typeof(DateTime));
        rows.Rows.Add(1, 17, "Result Entry", "analyst", ChainTime(1));
        rows.Rows.Add(2, 17, "Review", "reviewer", ChainTime(2));
        rows.Rows.Add(3, 17, "Approval", "qa", ChainTime(3));
        rows.Rows.Add(4, 17, "Certificate Issuance", "issuer", ChainTime(4));
        return rows;
    }
}
