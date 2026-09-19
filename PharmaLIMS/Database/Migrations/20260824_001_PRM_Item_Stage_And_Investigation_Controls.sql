SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Controlled PRM hardening migration 20260824_001

    - Scope PRM specifications to the exact material/product and production stage.
    - Freeze specification-version provenance on samples and assigned tests.
    - Persist stability chamber and protocol traceability.
    - Provide a fail-closed PRM Quality Event checklist.
    - Add lookup support for serialized open-event creation.

    Historical samples and PRM_SampleTests are deliberately not rewritten.
    Any legacy provenance gap requires a documented, approved reconciliation.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_Samples',N'U') IS NULL
        THROW 53200, 'Required table dbo.PRM_Samples is missing.', 1;
    IF OBJECT_ID(N'dbo.PRM_SampleTests',N'U') IS NULL
        THROW 53201, 'Required table dbo.PRM_SampleTests is missing.', 1;
    IF OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NULL
        THROW 53202, 'Required table dbo.PRM_SpecificationTests is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEvents',N'U') IS NULL
        THROW 53203, 'Required table dbo.QualityEvents is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEventChecklistQuestions',N'U') IS NULL
        THROW 53204, 'Required table dbo.QualityEventChecklistQuestions is missing. Apply 20260823_003 first.', 1;

    IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ItemCode') IS NULL
        ALTER TABLE dbo.PRM_SpecificationTests ADD ItemCode NVARCHAR(80) NULL;
    IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ProductionStage') IS NULL
        ALTER TABLE dbo.PRM_SpecificationTests ADD ProductionStage NVARCHAR(80) NULL;

    IF COL_LENGTH(N'dbo.PRM_Samples',N'SpecificationVersionNo') IS NULL
        ALTER TABLE dbo.PRM_Samples ADD SpecificationVersionNo INT NULL;
    IF COL_LENGTH(N'dbo.PRM_Samples',N'StabilityChamberNo') IS NULL
        ALTER TABLE dbo.PRM_Samples ADD StabilityChamberNo NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.PRM_Samples',N'StabilityProtocolNo') IS NULL
        ALTER TABLE dbo.PRM_Samples ADD StabilityProtocolNo NVARCHAR(120) NULL;

    IF COL_LENGTH(N'dbo.PRM_SampleTests',N'SourceSpecificationTestID') IS NULL
        ALTER TABLE dbo.PRM_SampleTests ADD SourceSpecificationTestID INT NULL;
    IF COL_LENGTH(N'dbo.PRM_SampleTests',N'SpecificationVersionNo') IS NULL
        ALTER TABLE dbo.PRM_SampleTests ADD SpecificationVersionNo INT NULL;
    IF COL_LENGTH(N'dbo.PRM_SampleTests',N'SpecificationItemCode') IS NULL
        ALTER TABLE dbo.PRM_SampleTests ADD SpecificationItemCode NVARCHAR(80) NULL;
    IF COL_LENGTH(N'dbo.PRM_SampleTests',N'SpecificationProductionStage') IS NULL
        ALTER TABLE dbo.PRM_SampleTests ADD SpecificationProductionStage NVARCHAR(80) NULL;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.foreign_keys
        WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_SampleTests')
          AND name=N'FK_PRM_SampleTests_SourceSpecification_20260824'
    )
        ALTER TABLE dbo.PRM_SampleTests WITH CHECK
        ADD CONSTRAINT FK_PRM_SampleTests_SourceSpecification_20260824
            FOREIGN KEY(SourceSpecificationTestID)
            REFERENCES dbo.PRM_SpecificationTests(SpecificationTestID);

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_Samples')
          AND name=N'CK_PRM_Samples_SpecificationVersion_20260824'
    )
        ALTER TABLE dbo.PRM_Samples WITH CHECK
        ADD CONSTRAINT CK_PRM_Samples_SpecificationVersion_20260824
            CHECK(SpecificationVersionNo IS NULL OR SpecificationVersionNo>0);

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
          AND name=N'IX_PRM_SpecificationTests_ExactScope_20260824'
    )
        CREATE INDEX IX_PRM_SpecificationTests_ExactScope_20260824
            ON dbo.PRM_SpecificationTests
                (SampleCategory,ItemCode,ProductionStage,ApprovalStatus,IsActive,EffectiveDate,SpecificationNo,VersionNo)
            INCLUDE(TestCode,SpecificationTestID);

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.PRM_SampleTests')
          AND name=N'IX_PRM_SampleTests_FrozenSource_20260824'
    )
        CREATE INDEX IX_PRM_SampleTests_FrozenSource_20260824
            ON dbo.PRM_SampleTests(SampleID,SpecificationVersionNo,SourceSpecificationTestID);

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'IX_QualityEvents_SourceStatus_20260824'
    )
        CREATE INDEX IX_QualityEvents_SourceStatus_20260824
            ON dbo.QualityEvents(SourceModule,SourceRecordID,CurrentStatus,QualityEventID);

    DECLARE @Questions TABLE
    (
        SectionName NVARCHAR(200) NOT NULL,
        QuestionText NVARCHAR(1000) NOT NULL,
        SampleType NVARCHAR(100) NOT NULL,
        SortOrder INT NOT NULL
    );

    INSERT @Questions(SectionName,QuestionText,SampleType,SortOrder)
    VALUES
      (N'Phase I - Laboratory Review',N'Were analyst authorization, the current approved method, calculations, transcription, raw data, and audit trail independently reviewed?',N'All',9000),
      (N'Specification Traceability',N'Was the exact material/product code, production stage where applicable, specification number, and frozen specification version verified?',N'All',9010),
      (N'Media / Equipment / Incubation',N'Were media identity and GPT status, equipment status, controls, incubation conditions, and reading conditions acceptable?',N'All',9020),
      (N'Result Confirmation',N'Was the original result confirmed without unauthorized invalidation, undocumented repeat testing, or testing into compliance?',N'All',9030),
      (N'Impact / CAPA / Disposition',N'Were batch or material impact, recurrence, root cause, CAPA requirement, and final disposition scientifically documented?',N'All',9040),
      (N'Raw Material Traceability',N'Were manufacturer, supplier, manufacturer lot, GRN, receipt, storage, expiry/retest, and sampling traceability verified?',N'Raw Material',9100),
      (N'Raw Material Risk',N'Were supplier history, material microbiological risk, prior lots, and related deviations or complaints reviewed?',N'Raw Material',9110),
      (N'Raw Material Disposition',N'Was the effect on material status and any affected product batches assessed before disposition?',N'Raw Material',9120),
      (N'In-Process Traceability',N'Were product code, batch, exact production stage, sampled-from location, machine/line, and sampling time verified?',N'Production / In-Process',9200),
      (N'In-Process Conditions',N'Were process, hold-time, cleaning, environmental, personnel, and equipment conditions around sampling reviewed?',N'Production / In-Process',9210),
      (N'In-Process Impact',N'Were subsequent stages, finished-product risk, affected quantity, and batch disposition assessed?',N'Production / In-Process',9220),
      (N'Finished Product Traceability',N'Were product code, batch, dosage form, manufacturing, packaging, expiry, pack size, and sample identity verified?',N'Finished Product',9300),
      (N'Finished Product Risk',N'Were manufacturing history, environmental/in-process results, packaging, storage, and prior-batch trend reviewed?',N'Finished Product',9310),
      (N'Finished Product Disposition',N'Was the final batch-release impact and any market or stability impact documented before disposition?',N'Finished Product',9320),
      (N'Stability Traceability',N'Were protocol number, chamber number, study type, pull point, storage condition, product, and batch verified?',N'Stability',9400),
      (N'Stability Chamber Review',N'Were chamber mapping, calibration, monitoring records, alarms, excursions, and sample placement history reviewed?',N'Stability',9410),
      (N'Stability Impact',N'Were trend, previous/subsequent pull points, shelf-life impact, and protocol/reporting requirements assessed?',N'Stability',9420);

    UPDATE existing
    SET existing.SectionName=source.SectionName,
        existing.AppliesToEventType=N'All',
        existing.AppliesToSampleType=source.SampleType,
        existing.AppliesToTestCategory=N'PRM Microbiology',
        existing.AppliesToTestNameKeyword=N'All',
        existing.AnswerType=N'YesNoNA',
        existing.IsRequired=1,
        existing.ExpectedAnswer=N'Yes',
        existing.QuestionLogic=N'PositiveCheck',
        existing.SortOrder=source.SortOrder,
        existing.IsActive=1
    FROM dbo.QualityEventChecklistQuestions existing
    INNER JOIN @Questions source ON source.QuestionText=existing.QuestionText;

    INSERT dbo.QualityEventChecklistQuestions
    (
        SectionName,QuestionText,AppliesToEventType,AppliesToSampleType,
        AppliesToTestCategory,AppliesToTestNameKeyword,AnswerType,IsRequired,
        ExpectedAnswer,QuestionLogic,SortOrder,IsActive
    )
    SELECT
        source.SectionName,source.QuestionText,N'All',source.SampleType,
        N'PRM Microbiology',N'All',N'YesNoNA',1,N'Yes',N'PositiveCheck',source.SortOrder,1
    FROM @Questions source
    WHERE NOT EXISTS
    (
        SELECT 1 FROM dbo.QualityEventChecklistQuestions existing
        WHERE existing.QuestionText=source.QuestionText
    );

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260824_001' AS MigrationVersion;
