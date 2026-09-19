namespace PharmaLIMS.Services
{
    internal static class WaterResultSnapshotSql
    {
        internal static string Build(bool forUpdate)
        {
            string hint = forUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
            return @"
SELECT st.SampleTestID, st.SampleID, st.TestID, st.ResultRowVersion,
       ISNULL(NULLIF(st.TestNameSnapshot,N''),t.TestName) AS TestName,
       CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN ISNULL(st.UnitSnapshot,N'') ELSE ISNULL(t.Unit,N'') END AS Unit,
       CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.AlertLimitSnapshot ELSE t.AlertLimit END AS AlertLimit,
       CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.ActionLimitSnapshot ELSE t.ActionLimit END AS ActionLimit,
       CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN 1 ELSE 0 END AS HasSpecSnapshot,
       st.ResultValue, st.Remarks, st.ResultStatus, st.DeviationType, st.LimitDescription
FROM dbo.SampleTests st" + hint + @"
LEFT JOIN dbo.Tests t" + hint + @" ON st.TestID=t.TestID
WHERE st.SampleID=@sampleId
ORDER BY st.SampleTestID;";
        }
    }
}
