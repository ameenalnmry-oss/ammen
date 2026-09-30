using System;
using System.Collections.Generic;
using System.Data;

namespace PharmaLIMS.Services
{
    public static class PrmCertificateSignatureChain
    {
        public static bool IsCompleteForLatestResultCycle(
            DataTable signatures, int sampleId, string issueAction)
        {
            if (signatures == null || sampleId <= 0 ||
                (issueAction != "Certificate Issuance" && issueAction != "Certificate Reissue"))
                return false;

            foreach (string column in new[] { "SignatureID", "SampleID", "ActionType", "SignedBy", "SignedAt" })
                if (!signatures.Columns.Contains(column))
                    return false;

            DataRow? entry = null, review = null, approval = null, issue = null;
            var identities = new HashSet<int>();
            foreach (DataRow row in signatures.Rows)
            {
                if (row.RowState == DataRowState.Deleted ||
                    row["SignatureID"] is not int identity || identity <= 0 ||
                    !identities.Add(identity) ||
                    row["SampleID"] is not int rowSampleId || rowSampleId != sampleId)
                    return false;

                string action = (row["ActionType"] as string ?? string.Empty).Trim();
                if (action.Equals("Result Entry", StringComparison.OrdinalIgnoreCase))
                    KeepLatest(ref entry, row);
                else if (action.Equals("Review", StringComparison.OrdinalIgnoreCase))
                    KeepLatest(ref review, row);
                else if (action.Equals("Approval", StringComparison.OrdinalIgnoreCase))
                    KeepLatest(ref approval, row);
                else if (action.Equals("Certificate Issuance", StringComparison.OrdinalIgnoreCase) ||
                         action.Equals("Certificate Reissue", StringComparison.OrdinalIgnoreCase))
                    KeepLatest(ref issue, row);
            }

            if (entry == null || review == null || approval == null || issue == null ||
                !string.Equals((issue["ActionType"] as string)?.Trim(), issueAction, StringComparison.OrdinalIgnoreCase))
                return false;

            // An old approval cannot authorize a later result entry or technical review.
            // SQL identity order distinguishes signatures whose timestamps are equal.
            int previousId = 0;
            DateTime previousTime = DateTime.MinValue;
            foreach (DataRow row in new[] { entry, review, approval, issue })
            {
                int identity = (int)row["SignatureID"];
                if (identity <= previousId ||
                    string.IsNullOrWhiteSpace(row["SignedBy"] as string) ||
                    row["SignedAt"] is not DateTime signedAt ||
                    signedAt == DateTime.MinValue || signedAt < previousTime)
                    return false;

                previousId = identity;
                previousTime = signedAt;
            }

            return true;
        }

        private static void KeepLatest(ref DataRow? latest, DataRow candidate)
        {
            if (latest == null || (int)candidate["SignatureID"] > (int)latest["SignatureID"])
                latest = candidate;
        }
    }
}
