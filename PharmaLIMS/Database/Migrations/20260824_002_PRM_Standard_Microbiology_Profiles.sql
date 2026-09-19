SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Controlled reusable microbiology profiles.

    The wildcard scope (*) means that the approved profile can be reused for
    the category instead of creating the same test panel for every product and
    production stage. An exact item/stage profile remains the preferred match
    and therefore overrides these standards when one is configured.

    Product limits are the site's stricter oral-tablet limits. Raw-material
    limits are the general pharmaceutical-substance panel; an exact material
    monograph/profile can override it when required.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NULL
        THROW 53220, 'Required table dbo.PRM_SpecificationTests is missing.', 1;
    IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ItemCode') IS NULL
        THROW 53221, 'PRM standard profiles require migration 20260824_001 first.', 1;
    IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ProductionStage') IS NULL
        THROW 53222, 'PRM standard profiles require the controlled ProductionStage column.', 1;
    IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedBy') IS NULL
        THROW 53223, 'PRM standard profiles require the controlled review columns.', 1;

    DECLARE @Profiles TABLE
    (
        SpecificationNo NVARCHAR(120) NOT NULL,
        SampleCategory NVARCHAR(40) NOT NULL,
        ProductionStage NVARCHAR(80) NULL,
        CompendialReference NVARCHAR(160) NOT NULL,
        TestCode NVARCHAR(40) NOT NULL,
        TestName NVARCHAR(160) NOT NULL,
        SpecificationText NVARCHAR(500) NOT NULL,
        Unit NVARCHAR(50) NULL,
        ResultType NVARCHAR(60) NOT NULL,
        SpecificationLimit DECIMAL(18,3) NULL,
        SortOrder INT NOT NULL
    );

    INSERT @Profiles
    (
        SpecificationNo,SampleCategory,ProductionStage,CompendialReference,
        TestCode,TestName,SpecificationText,Unit,ResultType,SpecificationLimit,SortOrder
    )
    SELECT
        category.SpecificationNo,
        category.SampleCategory,
        category.ProductionStage,
        category.CompendialReference,
        test.TestCode,
        test.TestName,
        CASE
            WHEN test.TestCode=N'TAMC' AND category.SampleCategory=N'Raw Material' THEN N'NMT 1000 CFU/g or mL'
            WHEN test.TestCode=N'TYMC' AND category.SampleCategory=N'Raw Material' THEN N'NMT 100 CFU/g or mL'
            WHEN test.TestCode=N'TAMC' THEN N'NMT 100 CFU/g'
            WHEN test.TestCode=N'TYMC' THEN N'NMT 10 CFU/g'
            ELSE test.SpecificationText
        END,
        CASE WHEN test.TestCode IN(N'TAMC',N'TYMC')
             THEN CASE WHEN category.SampleCategory=N'Raw Material' THEN N'CFU/g or mL' ELSE N'CFU/g' END
             ELSE NULL END,
        test.ResultType,
        CASE
            WHEN test.TestCode=N'TAMC' AND category.SampleCategory=N'Raw Material' THEN CONVERT(DECIMAL(18,3),1000)
            WHEN test.TestCode=N'TYMC' AND category.SampleCategory=N'Raw Material' THEN CONVERT(DECIMAL(18,3),100)
            WHEN test.TestCode=N'TAMC' THEN CONVERT(DECIMAL(18,3),100)
            WHEN test.TestCode=N'TYMC' THEN CONVERT(DECIMAL(18,3),10)
            ELSE NULL
        END,
        test.SortOrder
    FROM
    (
        VALUES
        (N'MIC-RM-STANDARD-001',N'Raw Material',CONVERT(NVARCHAR(80),NULL),
         N'USP <61>/<62>/<1111>; site risk-based objectionable-organism panel'),
        (N'MIC-IP-ORAL-TABLET-STD-001',N'Production / In-Process',N'*',
         N'USP <61>/<62>/<1111>; site stricter non-sterile oral-tablet limits'),
        (N'MIC-FP-ORAL-TABLET-STD-001',N'Finished Product',CONVERT(NVARCHAR(80),NULL),
         N'USP <61>/<62>/<1111>; site stricter non-sterile oral-tablet limits'),
        (N'MIC-ST-ORAL-TABLET-STD-001',N'Stability',CONVERT(NVARCHAR(80),NULL),
         N'USP <61>/<62>/<1111>; site stricter non-sterile oral-tablet limits')
    ) category(SpecificationNo,SampleCategory,ProductionStage,CompendialReference)
    CROSS APPLY
    (
        VALUES
        (N'TAMC',N'Total Aerobic Microbial Count (TAMC)',N'',N'Numeric',10),
        (N'TYMC',N'Total Yeast and Mold Count (TYMC)',N'',N'Numeric',20),
        (N'SALMONELLA',N'Salmonella spp.',N'Absent in 10 g',N'Qualitative',30),
        (N'ECOLI',N'Escherichia coli',N'Absent in 1 g',N'Qualitative',40),
        (N'SAUREUS',N'Staphylococcus aureus',N'Absent in 1 g',N'Qualitative',50),
        (N'PAERUGINOSA',N'Pseudomonas aeruginosa',N'Absent in 1 g',N'Qualitative',60),
        (N'CALBICANS',N'Candida albicans',N'Absent in 1 g',N'Qualitative',70)
    ) test(TestCode,TestName,SpecificationText,ResultType,SortOrder);

    IF EXISTS
    (
        SELECT 1
        FROM dbo.PRM_SpecificationTests existing
        INNER JOIN @Profiles source
            ON source.SpecificationNo=existing.SpecificationNo
           AND source.SampleCategory=existing.SampleCategory
           AND existing.VersionNo=1
           AND source.TestCode=existing.TestCode
        WHERE existing.SpecificationText<>source.SpecificationText
           OR ISNULL(existing.Unit,N'')<>ISNULL(source.Unit,N'')
           OR existing.ResultType<>source.ResultType
           OR ISNULL(existing.SpecificationLimit,-1)<>ISNULL(source.SpecificationLimit,-1)
           OR UPPER(LTRIM(RTRIM(ISNULL(existing.ItemCode,N''))))<>N'*'
           OR UPPER(LTRIM(RTRIM(ISNULL(existing.ProductionStage,N''))))<>
                UPPER(LTRIM(RTRIM(ISNULL(source.ProductionStage,N''))))
    )
        THROW 53224, 'A controlled PRM standard profile number conflicts with existing master data.', 1;

    INSERT dbo.PRM_SpecificationTests
    (
        SpecificationNo,SampleCategory,ItemCode,ProductionStage,CompendialReference,VersionNo,
        TestCode,TestName,SpecificationText,Unit,ResultType,SpecificationLimit,
        RequiredTest,SortOrder,ApprovalStatus,ReviewedBy,ReviewedDate,
        ApprovedBy,ApprovedDate,EffectiveDate,IsActive,CreatedBy,IsDefaultForCategory
    )
    SELECT
        source.SpecificationNo,source.SampleCategory,N'*',source.ProductionStage,
        source.CompendialReference,1,source.TestCode,source.TestName,
        source.SpecificationText,source.Unit,source.ResultType,source.SpecificationLimit,
        1,source.SortOrder,N'Approved',N'Controlled Release Review 20260824',SYSDATETIME(),
        N'Controlled Release Approval 20260824',SYSDATETIME(),CAST(SYSDATETIME() AS date),
        1,N'Controlled Release Master Data 20260824',1
    FROM @Profiles source
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM dbo.PRM_SpecificationTests existing
        WHERE existing.SpecificationNo=source.SpecificationNo
          AND existing.SampleCategory=source.SampleCategory
          AND existing.VersionNo=1
          AND existing.TestCode=source.TestCode
    );

    IF EXISTS
    (
        SELECT source.SpecificationNo,source.SampleCategory
        FROM @Profiles source
        LEFT JOIN dbo.PRM_SpecificationTests existing
            ON existing.SpecificationNo=source.SpecificationNo
           AND existing.SampleCategory=source.SampleCategory
           AND existing.VersionNo=1
           AND existing.TestCode=source.TestCode
        GROUP BY source.SpecificationNo,source.SampleCategory
        HAVING COUNT(existing.SpecificationTestID)<>7
            OR MIN(CASE WHEN existing.ApprovalStatus=N'Approved' AND existing.IsActive=1
                              AND existing.IsDefaultForCategory=1 THEN 1 ELSE 0 END)<>1
    )
        THROW 53225, 'The controlled PRM standard microbiology profiles are incomplete.', 1;

    UPDATE existing
    SET IsDefaultForCategory=0
    FROM dbo.PRM_SpecificationTests existing
    WHERE existing.IsDefaultForCategory=1
      AND existing.SpecificationNo NOT IN
      (
          N'MIC-RM-STANDARD-001',
          N'MIC-IP-ORAL-TABLET-STD-001',
          N'MIC-FP-ORAL-TABLET-STD-001',
          N'MIC-ST-ORAL-TABLET-STD-001'
      )
      AND existing.SampleCategory IN
      (N'Raw Material',N'Production / In-Process',N'Finished Product',N'Stability');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260824_002' AS MigrationVersion;
