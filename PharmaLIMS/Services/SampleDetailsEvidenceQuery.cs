using System.Data;
using Microsoft.Data.SqlClient;

namespace PharmaLIMS.Services;

internal static class SampleDetailsEvidenceQuery
{
    internal const string Header = @"
                SELECT TOP 1
                    s.SampleID,
                    ISNULL(s.SampleNumber, '') AS SampleNumber,
                    ISNULL(s.SampleType, '') AS SampleType,
                    COALESCE(NULLIF(s.PointCodeSnapshot,N''),wp.PointCode, sp.PointCode, '') AS PointCode,
                    COALESCE(NULLIF(s.PointLocationSnapshot,N''),wp.Location, sp.Location, '') AS Location,
                    s.SamplingDateTime,
                    ISNULL(s.SampledBy, '') AS SampledBy,
                    ISNULL(s.Status, '') AS CurrentStatus,
                    s.CreatedDate,
                    ISNULL(s.Activity, '') AS Activity,
                    ISNULL(s.ReceiptDeviationReason, '') AS RejectionReason
                FROM Samples s
                LEFT JOIN WaterSamplingPoints wp ON s.PointID = wp.Id
                LEFT JOIN SamplingPoints sp ON s.PointID = sp.PointID
                WHERE s.SampleID = @sampleId";

    // Use the controlled schema. RecordID is scoped by table; child IDs are not sample IDs.
    internal const string AuditTrail = @"
SELECT a.AuditID,
 ISNULL(a.Action,N'') AS ActionType, ISNULL(a.FieldName,N'') AS FieldName,
 ISNULL(a.OldValue,N'') AS OldValue, ISNULL(a.NewValue,N'') AS NewValue,
 ISNULL(a.Reason,N'') AS Reason,
 COALESCE(NULLIF(a.PerformedBy,N''),a.ChangedBy,N'') AS PerformedBy,
 COALESCE(a.ChangeDate,a.UtcRecordedAt,a.CreatedUtc) AS PerformedAt,
 COALESCE(NULLIF(a.ComputerName,N''),a.SourceWorkstation,N'') AS ComputerName
FROM dbo.AuditTrail a
WHERE (a.TableName=N'Samples' AND a.RecordID=@sampleId)
 OR (a.TableName=N'SampleTests' AND EXISTS
     (SELECT 1 FROM dbo.SampleTests t WHERE t.SampleTestID=a.RecordID AND t.SampleID=@sampleId))
 OR (a.TableName=N'Certificates' AND EXISTS
     (SELECT 1 FROM dbo.Certificates c WHERE c.SampleID=@sampleId AND
       ((NULLIF(@sampleNumber,N'') IS NOT NULL AND a.SampleNumber=@sampleNumber)
        OR a.SampleNumber=c.CertificateNumber
        OR (NULLIF(a.SampleNumber,N'') IS NULL AND a.RecordID=c.CertificateID))))
 OR (a.TableName=N'QualityEvents' AND EXISTS
     (SELECT 1 FROM dbo.QualityEvents q WHERE q.QualityEventID=a.RecordID AND q.SampleID=@sampleId
      AND UPPER(LTRIM(RTRIM(ISNULL(q.SourceModule,N'')))) IN(N'WATER',N'PW',N'PTW',N'SAMPLE')))
ORDER BY COALESCE(a.ChangeDate,a.UtcRecordedAt,a.CreatedUtc) DESC,a.AuditID DESC;";

    internal const string Signatures = @"
SELECT SignatureID,ISNULL(ActionType,N'') AS ActionType,
 ISNULL(MeaningOfSignature,N'') AS MeaningOfSignature,ISNULL(SignedBy,N'') AS SignedBy,
 ISNULL(UserRole,N'') AS UserRole,SignedAt,ISNULL(ActionReason,N'') AS ActionReason
FROM dbo.ElectronicSignatures
WHERE SampleID=@sampleId
ORDER BY SignedAt DESC,SignatureID DESC;";

    internal static SqlParameter[] Parameters(int sampleId,string sampleNumber) => new[]
    {
        new SqlParameter("@sampleId",SqlDbType.Int) { Value=sampleId },
        new SqlParameter("@sampleNumber",SqlDbType.NVarChar,100) { Value=sampleNumber ?? string.Empty }
    };
}
