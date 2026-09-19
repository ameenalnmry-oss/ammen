SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Problems TABLE(Problem NVARCHAR(500) NOT NULL);

IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SpecificationNumericLimit') IS NULL
    INSERT @Problems VALUES(N'Missing QualityEventAffectedResults.SpecificationNumericLimit.');

IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'EvidenceSchemaVersion') IS NULL
    INSERT @Problems VALUES(N'Missing QualityEventAffectedResults.EvidenceSchemaVersion.');

IF EXISTS
(
    SELECT 1
    FROM dbo.QualityEventAffectedResults affected
    INNER JOIN dbo.QualityEvents qualityEvent
        ON qualityEvent.QualityEventID = affected.QualityEventID
    LEFT JOIN dbo.PRM_SampleTests currentResult
        ON currentResult.SampleTestID = affected.SourceResultID
       AND currentResult.SampleID = qualityEvent.SourceRecordID
    WHERE affected.SourceModule = N'PRM'
      AND qualityEvent.SourceModule = N'PRM'
      AND qualityEvent.CurrentStatus = N'Closed'
      AND ISNULL(affected.EvidenceSchemaVersion, 0) = 1
      AND
      (
          currentResult.SampleTestID IS NULL
          OR CONVERT(VARBINARY(MAX), ISNULL(affected.TestName, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(currentResult.TestName, N''))
          OR CONVERT(VARBINARY(MAX), ISNULL(affected.ResultValue, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(currentResult.ResultValue, N''))
          OR CONVERT(VARBINARY(MAX), ISNULL(affected.SpecificationLimit, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(currentResult.SpecificationText, N''))
          OR NOT (affected.SpecificationNumericLimit = currentResult.SpecificationLimit OR (affected.SpecificationNumericLimit IS NULL AND currentResult.SpecificationLimit IS NULL))
          OR CONVERT(VARBINARY(MAX), ISNULL(affected.Unit, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(currentResult.Unit, N''))
          OR CONVERT(VARBINARY(MAX), ISNULL(affected.FailureType, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(currentResult.Interpretation, N''))
      )
)
    INSERT @Problems VALUES(N'One or more closed version-1 PRM investigations have changed affected-result evidence. Approval must remain fail-closed until a later controlled investigation is completed.');

SELECT Problem FROM @Problems ORDER BY Problem;

IF EXISTS (SELECT 1 FROM @Problems)
    THROW 53719, 'PRM investigation evidence verification failed. Review the returned problem list.', 1;

PRINT N'PRM investigation evidence verification PASS.';
