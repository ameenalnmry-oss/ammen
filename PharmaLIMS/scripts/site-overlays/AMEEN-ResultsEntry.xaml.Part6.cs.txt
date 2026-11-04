#nullable disable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PharmaLIMS.Infrastructure;

namespace PharmaLIMS
{
    public partial class ResultsEntry
    {
        private string BuildLegacyReissueReason(string reconciliationReason)
        {
            string baseReason = reconciliationReason == null ? string.Empty : reconciliationReason.Trim();
            string trace = $"Legacy Certificate Evidence Reconciliation #{_legacyReconciliationId}; legacy certificate {_legacyCertificateNumber}.";
            return string.IsNullOrWhiteSpace(baseReason) ? trace : baseReason + " " + trace;
        }

        private (bool TargetActive, bool TargetCancelled, bool ReplacementComplete) GetLegacyReissueRouteState()
        {
            if (!_legacyCertificateReissueRouteActive || currentSampleId <= 0)
                return (false, false, false);

            SqlParameter[] parameters =
            {
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = currentSampleId },
                new SqlParameter("@CertificateID", SqlDbType.Int) { Value = _legacyReissueCertificateId }
            };

            DataTable state = DatabaseHelper.ExecuteQuery(@"
SELECT
    CASE WHEN EXISTS
    (
        SELECT 1 FROM dbo.Certificates c
        WHERE c.CertificateID=@CertificateID AND c.SampleID=@SampleID
          AND ISNULL(c.IsCancelled,0)=0
          AND UPPER(ISNULL(c.CertificateStatus,ISNULL(c.Status,N''))) IN(N'ACTIVE',N'ISSUED')
    ) THEN 1 ELSE 0 END AS TargetActive,
    CASE WHEN EXISTS
    (
        SELECT 1 FROM dbo.Certificates c
        WHERE c.CertificateID=@CertificateID AND c.SampleID=@SampleID
          AND (ISNULL(c.IsCancelled,0)=1 OR UPPER(ISNULL(c.CertificateStatus,ISNULL(c.Status,N'')))=N'CANCELLED')
    ) THEN 1 ELSE 0 END AS TargetCancelled,
    CASE WHEN EXISTS
    (
        SELECT 1
        FROM dbo.Certificates r
        INNER JOIN dbo.CertificateDocumentSnapshots s ON s.CertificateID=r.CertificateID
        WHERE r.SampleID=@SampleID
          AND r.ReissuedFromCertificateID=@CertificateID
          AND ISNULL(r.IsCancelled,0)=0
          AND UPPER(ISNULL(r.CertificateStatus,ISNULL(r.Status,N''))) IN(N'ACTIVE',N'ISSUED')
          AND LEN(ISNULL(r.ReportHash,N''))=64
          AND LEN(ISNULL(s.SnapshotHash,N''))=64
    ) THEN 1 ELSE 0 END AS ReplacementComplete;", parameters);

            if (state.Rows.Count != 1)
                throw new InvalidOperationException("The legacy certificate reissue state could not be verified.");

            DataRow row = state.Rows[0];
            return (
                Convert.ToInt32(row["TargetActive"], CultureInfo.InvariantCulture) == 1,
                Convert.ToInt32(row["TargetCancelled"], CultureInfo.InvariantCulture) == 1,
                Convert.ToInt32(row["ReplacementComplete"], CultureInfo.InvariantCulture) == 1);
        }

        private void ApplyLegacyReissueRouteUi(string status, bool canCertificate, bool hasOpenQualityEvent)
        {
            if (!_legacyCertificateReissueRouteActive || currentSampleId != _legacyReissueSampleId)
                return;

            var routeState = GetLegacyReissueRouteState();
            string documentName = IsPotableWaterSample() ? "Report" : "COA";

            if (routeState.ReplacementComplete)
            {
                BtnCancelCOA.Content = $"Legacy {documentName} Reissued";
                BtnCancelCOA.IsEnabled = false;
                BtnCertificate.Content = $"Open Replacement {documentName}";
                BtnCertificate.IsEnabled = canCertificate;
                return;
            }

            if (routeState.TargetActive)
            {
                BtnCancelCOA.Content = $"Cancel Legacy {documentName} (Step 1)";
                BtnCancelCOA.IsEnabled = CanCancelCertificate();
                BtnCertificate.Content = $"Issue Replacement {documentName} (Step 2)";
                BtnCertificate.IsEnabled = false;
                return;
            }

            if (routeState.TargetCancelled)
            {
                BtnCancelCOA.Content = $"Legacy {documentName} Cancelled";
                BtnCancelCOA.IsEnabled = false;
                BtnCertificate.Content = $"Issue Replacement {documentName} (Step 2)";
                BtnCertificate.IsEnabled = canCertificate && !hasOpenQualityEvent && status == "COA Cancelled";
                return;
            }

            BtnCancelCOA.Content = "Legacy Reissue Blocked";
            BtnCancelCOA.IsEnabled = false;
            BtnCertificate.Content = "Legacy Reissue Blocked";
            BtnCertificate.IsEnabled = false;
            lblStatus.Text = "Controlled legacy reissue is blocked because the signed target certificate state no longer matches the routed workflow.";
        }

    }
}
