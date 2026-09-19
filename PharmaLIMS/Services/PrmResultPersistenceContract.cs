namespace PharmaLIMS.Services
{
    internal static class PrmResultPersistenceContract
    {
        internal const string GuardedUpdateSql = @"
UPDATE dbo.PRM_SampleTests
SET ResultValue = CASE WHEN @ResultChanged = 1 THEN @ResultValue ELSE ResultValue END,
    Interpretation = CASE WHEN @InterpretationChanged = 1 THEN @Interpretation ELSE Interpretation END,
    Remarks = CASE WHEN @RemarksChanged = 1 THEN @Remarks ELSE Remarks END,
    EnteredBy = CASE
        WHEN @ResultChanged = 1 THEN CASE WHEN @ResultValue IS NULL THEN NULL ELSE @EnteredBy END
        ELSE EnteredBy
    END,
    EnteredDate = CASE
        WHEN @ResultChanged = 1 THEN CASE WHEN @ResultValue IS NULL THEN NULL ELSE SYSDATETIME() END
        ELSE EnteredDate
    END
OUTPUT
    deleted.ResultValue,
    deleted.Interpretation,
    deleted.Remarks,
    deleted.EnteredBy,
    deleted.EnteredDate,
    inserted.ResultValue,
    inserted.Interpretation,
    inserted.Remarks,
    inserted.EnteredBy,
    inserted.EnteredDate
WHERE SampleTestID = @SampleTestID
  AND SampleID = @SampleID
  AND ((ResultValue IS NULL AND @ExpectedResultValue IS NULL) OR
       (ResultValue IS NOT NULL AND @ExpectedResultValue IS NOT NULL AND CONVERT(VARBINARY(MAX), ResultValue) = CONVERT(VARBINARY(MAX), @ExpectedResultValue)))
  AND ((Interpretation IS NULL AND @ExpectedInterpretation IS NULL) OR
       (Interpretation IS NOT NULL AND @ExpectedInterpretation IS NOT NULL AND CONVERT(VARBINARY(MAX), Interpretation) = CONVERT(VARBINARY(MAX), @ExpectedInterpretation)))
  AND ((Remarks IS NULL AND @ExpectedRemarks IS NULL) OR
       (Remarks IS NOT NULL AND @ExpectedRemarks IS NOT NULL AND CONVERT(VARBINARY(MAX), Remarks) = CONVERT(VARBINARY(MAX), @ExpectedRemarks)))
  AND ((EnteredBy IS NULL AND @ExpectedEnteredBy IS NULL) OR
       (EnteredBy IS NOT NULL AND @ExpectedEnteredBy IS NOT NULL AND CONVERT(VARBINARY(MAX), EnteredBy) = CONVERT(VARBINARY(MAX), @ExpectedEnteredBy)))
  AND ((EnteredDate = @ExpectedEnteredDate) OR (EnteredDate IS NULL AND @ExpectedEnteredDate IS NULL))
  AND EXISTS
  (
      SELECT 1
      FROM dbo.PRM_Samples s WITH (UPDLOCK, HOLDLOCK)
      WHERE s.SampleID = @SampleID
        AND s.SampleStatus IN (N'Registered', N'In Progress', N'Results Entered')
  );";
    }
}
