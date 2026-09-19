SET NOCOUNT ON;
SET XACT_ABORT ON;

/* Read-only verification. Run on a restored Validation/Development database
   after the manifest migrations. Do not use this script to modify Production. */

IF OBJECT_ID(N'dbo.LIMS_SchemaVersions', N'U') IS NULL
    THROW 53500, 'Migration ledger is missing.', 1;

IF 5 <> (
    SELECT COUNT(*)
    FROM dbo.LIMS_SchemaVersions
    WHERE VersionKey IN(N'20260823_001',N'20260823_002',N'20260823_003',N'20260823_004',N'20260823_005')
      AND NULLIF(LTRIM(RTRIM(MigrationChecksum)),N'') IS NOT NULL
)
    THROW 53501, 'One or more Phase 1 migration ledger records/checksums are missing.', 1;

IF OBJECT_ID(N'dbo.EMTrendReviewSnapshots',N'U') IS NULL
   OR COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'MethodName') IS NULL
   OR COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'UnitName') IS NULL
   OR COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'PeriodCount') IS NULL
   OR COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'PeriodDefinitionJson') IS NULL
   OR COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'SourceAnchorBatchID') IS NULL
   OR COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'SnapshotHashSha256') IS NULL
   OR NOT EXISTS (
       SELECT 1 FROM sys.triggers
       WHERE parent_id=OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
         AND name=N'TR_EMTrendReviewSnapshots_Immutable'
         AND is_disabled=0
   )
    THROW 53507, 'External Trend signed-snapshot schema reconciliation is incomplete.', 1;

IF OBJECT_ID(N'dbo.PRM_SpecificationTests', N'U') IS NULL
   OR NOT EXISTS (
       SELECT 1 FROM sys.check_constraints
       WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
         AND name=N'CK_PRM_SpecificationTests_Approval'
         AND is_disabled=0 AND is_not_trusted=0
   )
    THROW 53502, 'Trusted PRM approval-state constraint is missing.', 1;

IF 2 <> (
    SELECT COUNT(*)
    FROM (
        SELECT SpecificationNo
        FROM dbo.PRM_SpecificationTests
        WHERE SpecificationNo IN(N'MIC-FP-ORAL-TABLET-001',N'MIC-ST-ORAL-TABLET-001')
          AND VersionNo=3 AND ApprovalStatus=N'Draft' AND IsActive=0 AND IsDefaultForCategory=0
        GROUP BY SpecificationNo
        HAVING COUNT(*)=7
           AND SUM(CASE WHEN TestCode=N'TAMC' AND SpecificationLimit=100 AND Unit=N'CFU/g' THEN 1 ELSE 0 END)=1
           AND SUM(CASE WHEN TestCode=N'TYMC' AND SpecificationLimit=10 AND Unit=N'CFU/g' THEN 1 ELSE 0 END)=1
           AND SUM(CASE WHEN TestCode IN(N'SALMONELLA',N'ECOLI',N'SAUREUS',N'PAERUGINOSA',N'CALBICANS') THEN 1 ELSE 0 END)=5
    ) approved_shape
)
    THROW 53503, 'Controlled oral-tablet Version 3 drafts are incomplete or have unexpected limits.', 1;

IF EXISTS (
    SELECT 1 FROM dbo.CultureMediaQualificationRequirements
    WHERE IsActive=1
      AND (ApprovalStatus<>N'Approved' OR ApprovedBy IS NULL OR ApprovedAt IS NULL)
)
    THROW 53504, 'An active Culture Media requirement lacks a controlled QA approval.', 1;

IF EXISTS (
    SELECT required.TableName
    FROM (VALUES
        (N'QualityEventActions'),(N'QualityEventChecklistQuestions'),(N'QualityEventChecklistAnswers'),
        (N'QualityEventRootCauseWhys'),(N'QualityEventImpactAssessments'),(N'QualityEventCAPAItems'),
        (N'QualityEventRetesting'),(N'QualityEventDistribution'),(N'QualityEventRelatedItems'),
        (N'QualityEventSignatures'),(N'QualityEventPrintHistory'),
        (N'Water_Plans'),(N'Water_PlanSamples'),(N'Water_PlanSampleTests'),
        (N'Water_PlanSampleAttempts'),(N'Water_PlanSignatures')
    ) required(TableName)
    WHERE OBJECT_ID(N'dbo.'+required.TableName,N'U') IS NULL
)
    THROW 53505, 'A required Quality Event or Water planning table is missing.', 1;

IF 3 <> (
    SELECT COUNT(*)
    FROM sys.triggers
    WHERE parent_id IN(
        OBJECT_ID(N'dbo.QualityEventSignatures'),
        OBJECT_ID(N'dbo.QualityEventPrintHistory'),
        OBJECT_ID(N'dbo.Water_PlanSignatures')
    )
      AND name IN(
        N'TRG_QualityEventSignatures_AppendOnly',
        N'TRG_QualityEventPrintHistory_AppendOnly',
        N'TRG_Water_PlanSignatures_AppendOnly'
    )
      AND is_disabled=0
)
    THROW 53506, 'A required append-only evidence trigger is missing or disabled.', 1;

SELECT N'PASS' AS VerificationStatus,
       N'PharmaLIMS 2026.8.23.135 database controls verified.' AS VerificationMessage;
