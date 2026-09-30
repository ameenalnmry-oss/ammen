using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace PharmaLIMS.Services;

internal static class SampleReceiptRegisterQuery
{
    internal static readonly string[] Types = { "All", "Purified Water", "Potable Water", "Environmental Monitoring", "Raw Material", "Primary Packaging", "Production / In-Process", "Finished Product", "Stability" };
    internal static string NormalizeType(string value) => value.Trim().ToUpperInvariant() switch
    { "PW" or "PURIFIED WATER" => "Purified Water", "PTW" or "POTABLE WATER" => "Potable Water", _ => value.Trim() };

    // One source record per row. Tests and plates are aggregated in correlated subqueries,
    // never joined into the register grain. Missing laboratory receipt stays NULL.
    internal const string Query = @"
WITH RegisterRows AS (
 SELECT 'WATER' RecordKind, s.SampleID, s.SampleNumber,
 CASE UPPER(LTRIM(RTRIM(s.SampleType))) WHEN 'PW' THEN 'Purified Water' WHEN 'PURIFIED WATER' THEN 'Purified Water'
 WHEN 'PTW' THEN 'Potable Water' WHEN 'POTABLE WATER' THEN 'Potable Water' ELSE s.SampleType END SampleType,
 COALESCE(NULLIF(s.PointCodeSnapshot,N''),wp.PointCode,sp.PointCode,N'') + N' | ' +
 COALESCE(NULLIF(s.PointLocationSnapshot,N''),wp.Location,sp.Location,N'') Description,
 CAST(N'' AS nvarchar(200)) BatchOrLot, ISNULL(s.SampleVolume,N'') Quantity,
 s.SamplingDateTime, s.ReceivedDateTime, s.CreatedDate RegisteredDateTime,
 s.SampledBy, s.ReceivedBy,
 (SELECT TOP 1 es.SignedBy FROM dbo.ElectronicSignatures es WHERE es.SampleID=s.SampleID AND es.ActionType=N'Water Sample Registration' ORDER BY es.SignedAt,es.SignatureID) RegisteredBy,
 s.ReceiptDecision, s.Status,
 STUFF((SELECT N'; ' + COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName,N'Unrecorded test')
 FROM dbo.SampleTests st LEFT JOIN dbo.Tests t ON t.TestID=st.TestID WHERE st.SampleID=s.SampleID
 ORDER BY st.SampleTestID FOR XML PATH(''),TYPE).value('.','nvarchar(max)'),1,2,N'') Tests
 FROM dbo.Samples s LEFT JOIN dbo.WaterSamplingPoints wp ON wp.Id=s.PointID
 LEFT JOIN dbo.SamplingPoints sp ON sp.PointID=s.PointID
 UNION ALL
 SELECT 'PRM',ps.SampleID,ps.SampleNumber,ps.SampleCategory,
 CASE WHEN ps.SampleCategory IN (N'Raw Material', N'Primary Packaging') THEN ISNULL(ps.MaterialCode,N'')+N' | '+ISNULL(ps.MaterialName,N'')
 ELSE ISNULL(ps.ProductCode,N'')+N' | '+ISNULL(ps.ProductName,N'') END + N' | '+ISNULL(ps.Department,N'')+N' | '+ISNULL(ps.SampleSource,N''),
 CASE WHEN ps.SampleCategory IN (N'Raw Material', N'Primary Packaging') THEN ISNULL(ps.ManufacturerLotNo,N'') ELSE ISNULL(ps.BatchNo,N'') END,
 COALESCE(CONVERT(nvarchar(60),ps.SampleQuantity),N'')+N' '+ISNULL(ps.Unit,N''),
 ps.SampleDateTime,pr.ReceivedDateTime,ps.CreatedDate,ps.SampledBy,pr.ReceivedBy,ps.CreatedBy,
 pr.ReceiptDecision,ps.SampleStatus,
 STUFF((SELECT N'; '+ISNULL(pt.TestName,N'Unrecorded test') FROM dbo.PRM_SampleTests pt WHERE pt.SampleID=ps.SampleID
 ORDER BY pt.SampleTestID FOR XML PATH(''),TYPE).value('.','nvarchar(max)'),1,2,N'')
 FROM dbo.PRM_Samples ps LEFT JOIN dbo.LaboratoryReceipts pr ON pr.PrmSampleID=ps.SampleID
 UNION ALL
 SELECT 'EM',e.Id,e.EventNo,N'Environmental Monitoring',
 COALESCE(NULLIF(e.AreaCodeSnapshot,N''),a.AreaCode,N'')+N' | '+COALESCE(NULLIF(e.AreaNameSnapshot,N''),a.AreaName,N'')+
 N' | Grade: '+COALESCE(NULLIF(e.GradeSnapshot,N''),a.Grade,N'Not recorded'),
 ISNULL(e.BatchNo,N''),CONVERT(nvarchar(60),(SELECT COUNT(*) FROM dbo.EM_EventPlates p WHERE p.EventId=e.Id))+N' plate(s)',
 CAST(e.EventDate AS datetime2),er.ReceivedDateTime,e.CreatedAt,CAST(NULL AS nvarchar(100)),er.ReceivedBy,
 (SELECT TOP 1 es.SignedBy FROM dbo.EM_EventSignatures es WHERE es.EventID=e.Id AND es.ActionType=N'EM Registration' ORDER BY es.SignedAt,es.SignatureID),
 er.ReceiptDecision,ISNULL(NULLIF(e.WorkflowStatus,N''),N'Registered'),
 STUFF((SELECT N'; '+p.Method+N' ['+p.PlateCode+N']' FROM dbo.EM_EventPlates p WHERE p.EventId=e.Id
 ORDER BY p.Id FOR XML PATH(''),TYPE).value('.','nvarchar(max)'),1,2,N'')
 FROM dbo.EM_Events e LEFT JOIN dbo.EM_Areas a ON a.Id=e.AreaId
 LEFT JOIN dbo.LaboratoryReceipts er ON er.EmEventID=e.Id
)
SELECT * FROM RegisterRows
WHERE (@Type=N'' OR SampleType=@Type) AND
 ((@ReceiptOnly=0 AND RegisteredDateTime>=@From AND RegisteredDateTime<@To) OR
  (@ReceiptOnly=1 AND ReceivedDateTime>=@From AND ReceivedDateTime<@To))
ORDER BY SampleType,RegisteredDateTime,SampleNumber,RecordKind,SampleID;";

    internal static void ValidateDates(DateTime from, DateTime to)
    {
        if (from.Date > to.Date) throw new InvalidOperationException("Date From must be on or before Date To.");
        if (to.Date == DateTime.MaxValue.Date) throw new InvalidOperationException("Date To is outside the supported range.");
    }
    internal static SqlParameter[] Parameters(string type, DateTime from, DateTime to, bool receiptOnly)
    {
        ValidateDates(from,to);
        return new[] { new SqlParameter("@Type",SqlDbType.NVarChar,100) { Value=type=="All" ? "" : NormalizeType(type) },
            new SqlParameter("@From",SqlDbType.DateTime2) { Value=from.Date },
            new SqlParameter("@To",SqlDbType.DateTime2) { Value=to.Date.AddDays(1) },
            new SqlParameter("@ReceiptOnly",SqlDbType.Bit) { Value=receiptOnly } };
    }
}
