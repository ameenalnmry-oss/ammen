SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_SpecificationTests', N'U') IS NULL
        THROW 52610, 'Required table dbo.PRM_SpecificationTests is missing.', 1;

    IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL
        THROW 52611, 'Required table dbo.PRM_Samples is missing.', 1;

    IF COL_LENGTH(N'dbo.PRM_Samples', N'AnalysisStartedDate') IS NULL
        ALTER TABLE dbo.PRM_Samples ADD AnalysisStartedDate DATETIME2(0) NULL;

    IF COL_LENGTH(N'dbo.PRM_Samples', N'AnalysisCompletedDate') IS NULL
        ALTER TABLE dbo.PRM_Samples ADD AnalysisCompletedDate DATETIME2(0) NULL;

    IF COL_LENGTH(N'dbo.PRM_SpecificationTests', N'CompendialReference') IS NULL
        ALTER TABLE dbo.PRM_SpecificationTests
            ADD CompendialReference NVARCHAR(160) NULL;

    IF COL_LENGTH(N'dbo.PRM_SpecificationTests', N'IsDefaultForCategory') IS NULL
        ALTER TABLE dbo.PRM_SpecificationTests
            ADD IsDefaultForCategory BIT NOT NULL
                CONSTRAINT DF_PRM_SpecificationTests_DefaultForCategory DEFAULT (0);

    /*
      Revision 2 of this development migration introduces version 2 of the
      controlled profiles. The site intentionally adopts a stricter panel than
      the compendial minimum for non-sterile oral solid products.

      Statements that reference columns added above are compiled separately so
      SQL Server cannot resolve them before ALTER TABLE completes.
    */
    EXEC sys.sp_executesql N'
IF OBJECT_ID(N''dbo.PRM_ElectronicSignatures'', N''U'') IS NOT NULL
BEGIN
    UPDATE sample
    SET AnalysisStartedDate = signatureDate.SignedAt
    FROM dbo.PRM_Samples sample
    CROSS APPLY
    (
        SELECT MIN(signature.SignedAt) SignedAt
        FROM dbo.PRM_ElectronicSignatures signature
        WHERE signature.SampleID = sample.SampleID
          AND signature.ActionType = N''Analysis Start''
    ) signatureDate
    WHERE sample.AnalysisStartedDate IS NULL
      AND signatureDate.SignedAt IS NOT NULL;

    UPDATE sample
    SET AnalysisCompletedDate = signatureDate.SignedAt
    FROM dbo.PRM_Samples sample
    CROSS APPLY
    (
        SELECT MAX(signature.SignedAt) SignedAt
        FROM dbo.PRM_ElectronicSignatures signature
        WHERE signature.SampleID = sample.SampleID
          AND signature.ActionType = N''Result Entry''
    ) signatureDate
    WHERE sample.AnalysisCompletedDate IS NULL
      AND signatureDate.SignedAt IS NOT NULL
      AND sample.SampleStatus IN
          (N''Results Entered'',N''Under Review'',N''Reviewed'',N''Approved'',N''Certificate Issued'');
END;';

    EXEC sys.sp_executesql N'
DECLARE @Profiles TABLE
(
    SpecificationNo NVARCHAR(120) NOT NULL,
    SampleCategory NVARCHAR(40) NOT NULL,
    TestCode NVARCHAR(40) NOT NULL,
    TestName NVARCHAR(160) NOT NULL,
    SpecificationText NVARCHAR(500) NOT NULL,
    Unit NVARCHAR(50) NULL,
    ResultType NVARCHAR(60) NOT NULL,
    SpecificationLimit DECIMAL(18,3) NULL,
    SortOrder INT NOT NULL
);

INSERT @Profiles
    (SpecificationNo,SampleCategory,TestCode,TestName,SpecificationText,Unit,ResultType,SpecificationLimit,SortOrder)
SELECT category.SpecificationNo,category.SampleCategory,test.TestCode,test.TestName,
       test.SpecificationText,test.Unit,test.ResultType,test.SpecificationLimit,test.SortOrder
FROM
(
    VALUES
    (N''MIC-RM-SUBSTANCES-001'',N''Raw Material''),
    (N''MIC-IP-ORAL-SOLID-001'',N''Production / In-Process''),
    (N''MIC-FP-ORAL-SOLID-001'',N''Finished Product''),
    (N''MIC-ST-ORAL-SOLID-001'',N''Stability'')
) category(SpecificationNo,SampleCategory)
CROSS APPLY
(
    VALUES
    (N''TAMC'',N''Total Aerobic Microbial Count (TAMC)'',
     N''NMT 1000 CFU/g or mL'',N''CFU/g or mL'',N''Numeric'',CONVERT(DECIMAL(18,3),1000),10),
    (N''TYMC'',N''Total Yeast and Mold Count (TYMC)'',
     N''NMT 100 CFU/g or mL'',N''CFU/g or mL'',N''Numeric'',CONVERT(DECIMAL(18,3),100),20),
    (N''SALMONELLA'',N''Salmonella spp.'',
     N''Absent in 10 g'',NULL,N''Qualitative'',NULL,30),
    (N''ECOLI'',N''Escherichia coli'',
     N''Absent in 1 g'',NULL,N''Qualitative'',NULL,40),
    (N''SAUREUS'',N''Staphylococcus aureus'',
     N''Absent in 1 g'',NULL,N''Qualitative'',NULL,50),
    (N''PAERUGINOSA'',N''Pseudomonas aeruginosa'',
     N''Absent in 1 g'',NULL,N''Qualitative'',NULL,60),
    (N''CALBICANS'',N''Candida albicans'',
     N''Absent in 1 g'',NULL,N''Qualitative'',NULL,70)
) test(TestCode,TestName,SpecificationText,Unit,ResultType,SpecificationLimit,SortOrder);

INSERT dbo.PRM_SpecificationTests
(
    SpecificationNo,SampleCategory,CompendialReference,VersionNo,
    TestCode,TestName,SpecificationText,Unit,ResultType,SpecificationLimit,
    RequiredTest,SortOrder,ApprovalStatus,ApprovedBy,ApprovedDate,
    EffectiveDate,IsActive,CreatedBy,IsDefaultForCategory
)
SELECT
    source.SpecificationNo,source.SampleCategory,
    N''Site Internal Stricter Specification; USP <61>/<62>; Non-Sterile Oral Solid'',
    2,source.TestCode,source.TestName,source.SpecificationText,source.Unit,
    source.ResultType,source.SpecificationLimit,1,source.SortOrder,
    N''Approved'',N''Controlled Development Import 20260723'',SYSDATETIME(),
    CAST(SYSDATETIME() AS date),1,N''Controlled Development Import 20260723'',1
FROM @Profiles source
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.PRM_SpecificationTests target
    WHERE target.SpecificationNo=source.SpecificationNo
      AND target.SampleCategory=source.SampleCategory
      AND target.VersionNo=2
      AND target.TestCode=source.TestCode
);

IF EXISTS
(
    SELECT 1
    FROM @Profiles source
    JOIN dbo.PRM_SpecificationTests target
      ON target.SpecificationNo=source.SpecificationNo
     AND target.SampleCategory=source.SampleCategory
     AND target.VersionNo=2
     AND target.TestCode=source.TestCode
    WHERE target.ApprovalStatus<>N''Approved''
       OR target.SpecificationText<>source.SpecificationText
       OR ISNULL(target.RequiredTest,1)<>1
)
    THROW 52620, ''The stricter oral-tablet profile conflicts with controlled version 2 data.'', 1;

UPDATE dbo.PRM_SpecificationTests
SET IsActive=CASE WHEN VersionNo=2 THEN 1 ELSE 0 END,
    IsDefaultForCategory=CASE WHEN VersionNo=2 THEN 1 ELSE 0 END
WHERE SpecificationNo IN
(
    N''MIC-RM-SUBSTANCES-001'',
    N''MIC-IP-ORAL-SOLID-001'',
    N''MIC-FP-ORAL-SOLID-001'',
    N''MIC-ST-ORAL-SOLID-001''
);';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
