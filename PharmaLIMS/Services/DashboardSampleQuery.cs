namespace PharmaLIMS.Services;

internal static class DashboardSampleQuery
{
    // Retain the existing seven-day workload rule, using the stored registration timestamp.
    // A schema/query failure is surfaced as N/A by the dashboard rather than an invented zero.
    internal const string Overdue = @"
DECLARE @cutoff datetime2 = DATEADD(DAY,-7,SYSDATETIME());
SELECT
 (SELECT COUNT(*) FROM dbo.Samples
  WHERE CreatedDate<@cutoff AND UPPER(LTRIM(RTRIM(ISNULL(Status,N''))))
    NOT IN(N'APPROVED',N'COA ISSUED',N'CERTIFICATE ISSUED',N'CANCELLED',N'REJECTED'))
 +
 (SELECT COUNT(*) FROM dbo.PRM_Samples
  WHERE CreatedDate<@cutoff AND UPPER(LTRIM(RTRIM(ISNULL(SampleStatus,N''))))
    NOT IN(N'APPROVED',N'COA ISSUED',N'CERTIFICATE ISSUED',N'CANCELLED',N'REJECTED'));";
}
