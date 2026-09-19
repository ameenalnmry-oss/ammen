SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Controlled PRM standard-profile readiness repair.

    Purpose:
      - Restore a reusable approved oral-tablet standard profile when the
        canonical Production / In-Process, Finished Product, or Stability
        profile is missing, inactive, or no longer complete.
      - Preserve every historical PRM sample and PRM_SampleTests snapshot.
      - Never create an item-specific exception automatically.

    Operational scope:
      - ItemCode = '*'
      - Production / In-Process uses ProductionStage = '*'
      - Finished Product and Stability use an empty ProductionStage scope
      - Seven controlled microbiology tests are created as a new version of
        the existing canonical specification number only when no complete,
        approved, active canonical version is available.

    Current oral-tablet limits retained from the controlled 20260830 profile:
      TAMC NMT 1000 CFU/g
      TYMC NMT 100 CFU/g
      Specified organisms as the established site panel.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NULL
        THROW 54600, 'Required table dbo.PRM_SpecificationTests is missing.', 1;

    IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ItemCode') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ProductionStage') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'CompendialReference') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedBy') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedDate') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'IsDefaultForCategory') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'MinimumElapsedHours') IS NULL
        THROW 54601, 'PRM specification governance columns are missing. Apply the earlier controlled PRM migrations first.', 1;

    DECLARE @Scopes TABLE
    (
        ScopeId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        SpecificationNo NVARCHAR(120) NOT NULL,
        SampleCategory NVARCHAR(40) NOT NULL,
        ProductionStage NVARCHAR(80) NULL
    );

    INSERT @Scopes(SpecificationNo,SampleCategory,ProductionStage)
    VALUES
      (N'MIC-IP-ORAL-TABLET-STD-001',N'Production / In-Process',N'*'),
      (N'MIC-FP-ORAL-TABLET-STD-001',N'Finished Product',NULL),
      (N'MIC-ST-ORAL-TABLET-STD-001',N'Stability',NULL);

    DECLARE @Tests TABLE
    (
        TestCode NVARCHAR(40) NOT NULL PRIMARY KEY,
        TestName NVARCHAR(160) NOT NULL,
        SpecificationText NVARCHAR(500) NOT NULL,
        Unit NVARCHAR(50) NULL,
        ResultType NVARCHAR(60) NOT NULL,
        SpecificationLimit DECIMAL(18,3) NULL,
        MinimumElapsedHours DECIMAL(9,2) NOT NULL,
        SortOrder INT NOT NULL,
        CompendialReference NVARCHAR(160) NOT NULL
    );

    INSERT @Tests
        (TestCode,TestName,SpecificationText,Unit,ResultType,SpecificationLimit,MinimumElapsedHours,SortOrder,CompendialReference)
    VALUES
      (N'TAMC',N'Total Aerobic Microbial Count (TAMC)',N'NMT 1000 CFU/g',N'CFU/g',N'Numeric',CONVERT(DECIMAL(18,3),1000),CONVERT(DECIMAL(9,2),72),10,
       N'USP <61>/<1111>; non-aqueous oral preparation acceptance criterion'),
      (N'TYMC',N'Total Yeast and Mold Count (TYMC)',N'NMT 100 CFU/g',N'CFU/g',N'Numeric',CONVERT(DECIMAL(18,3),100),CONVERT(DECIMAL(9,2),120),20,
       N'USP <61>/<1111>; non-aqueous oral preparation acceptance criterion'),
      (N'SALMONELLA',N'Salmonella spp.',N'Absent in 10 g',NULL,N'Qualitative',NULL,CONVERT(DECIMAL(9,2),120),30,
       N'USP <62> method; site-defined objectionable-organism control'),
      (N'ECOLI',N'Escherichia coli',N'Absent in 1 g',NULL,N'Qualitative',NULL,CONVERT(DECIMAL(9,2),120),40,
       N'USP <62>/<1111>; E. coli absent in 1 g for non-aqueous oral preparations'),
      (N'SAUREUS',N'Staphylococcus aureus',N'Absent in 1 g',NULL,N'Qualitative',NULL,CONVERT(DECIMAL(9,2),120),50,
       N'USP <62> method; site-defined objectionable-organism control'),
      (N'PAERUGINOSA',N'Pseudomonas aeruginosa',N'Absent in 1 g',NULL,N'Qualitative',NULL,CONVERT(DECIMAL(9,2),120),60,
       N'USP <62> method; site-defined objectionable-organism control'),
      (N'CALBICANS',N'Candida albicans',N'Absent in 1 g',NULL,N'Qualitative',NULL,CONVERT(DECIMAL(9,2),120),70,
       N'USP <62> method; site-defined objectionable-organism control');

    DECLARE @ScopeId INT=1;
    DECLARE @ScopeCount INT=(SELECT COUNT(1) FROM @Scopes);
    DECLARE @SpecificationNo NVARCHAR(120);
    DECLARE @SampleCategory NVARCHAR(40);
    DECLARE @ProductionStage NVARCHAR(80);
    DECLARE @ReadyVersion INT;
    DECLARE @NewVersion INT;

    WHILE @ScopeId<=@ScopeCount
    BEGIN
        SELECT
            @SpecificationNo=SpecificationNo,
            @SampleCategory=SampleCategory,
            @ProductionStage=ProductionStage
        FROM @Scopes
        WHERE ScopeId=@ScopeId;

        SET @ReadyVersion=NULL;

        SELECT TOP(1) @ReadyVersion=versions.VersionNo
        FROM
        (
            SELECT DISTINCT s.VersionNo
            FROM dbo.PRM_SpecificationTests s
            WHERE s.SpecificationNo=@SpecificationNo
              AND s.SampleCategory=@SampleCategory
              AND UPPER(LTRIM(RTRIM(ISNULL(s.ItemCode,N''))))=N'*'
              AND UPPER(LTRIM(RTRIM(ISNULL(s.ProductionStage,N''))))=
                  UPPER(LTRIM(RTRIM(ISNULL(@ProductionStage,N''))))
              AND s.ApprovalStatus=N'Approved'
              AND s.IsActive=1
              AND s.ReviewedBy IS NOT NULL
              AND s.ReviewedDate IS NOT NULL
              AND s.ApprovedBy IS NOT NULL
              AND s.ApprovedDate IS NOT NULL
              AND (s.EffectiveDate IS NULL OR s.EffectiveDate<=CAST(SYSDATETIME() AS date))
        ) versions
        WHERE
            (SELECT COUNT(1)
             FROM dbo.PRM_SpecificationTests s
             WHERE s.SpecificationNo=@SpecificationNo
               AND s.SampleCategory=@SampleCategory
               AND s.VersionNo=versions.VersionNo)=7
          AND NOT EXISTS
          (
              SELECT 1
              FROM @Tests expected
              WHERE NOT EXISTS
              (
                  SELECT 1
                  FROM dbo.PRM_SpecificationTests actual
                  WHERE actual.SpecificationNo=@SpecificationNo
                    AND actual.SampleCategory=@SampleCategory
                    AND actual.VersionNo=versions.VersionNo
                    AND actual.TestCode=expected.TestCode
                    AND actual.TestName=expected.TestName
                    AND actual.SpecificationText=expected.SpecificationText
                    AND ISNULL(actual.Unit,N'')=ISNULL(expected.Unit,N'')
                    AND actual.ResultType=expected.ResultType
                    AND (actual.SpecificationLimit=expected.SpecificationLimit
                         OR (actual.SpecificationLimit IS NULL AND expected.SpecificationLimit IS NULL))
                    AND actual.RequiredTest=1
                    AND actual.SortOrder=expected.SortOrder
                    AND actual.MinimumElapsedHours=expected.MinimumElapsedHours
                    AND actual.CompendialReference=expected.CompendialReference
                    AND UPPER(LTRIM(RTRIM(ISNULL(actual.ItemCode,N''))))=N'*'
                    AND UPPER(LTRIM(RTRIM(ISNULL(actual.ProductionStage,N''))))=
                        UPPER(LTRIM(RTRIM(ISNULL(@ProductionStage,N''))))
                    AND actual.ApprovalStatus=N'Approved'
                    AND actual.IsActive=1
              )
          )
        ORDER BY versions.VersionNo DESC;

        IF @ReadyVersion IS NULL
        BEGIN
            SELECT @NewVersion=ISNULL(MAX(VersionNo),0)+1
            FROM dbo.PRM_SpecificationTests WITH (UPDLOCK,HOLDLOCK)
            WHERE SpecificationNo=@SpecificationNo
              AND SampleCategory=@SampleCategory;

            /* Retire only previous versions of the canonical standard profile.
               Historical samples are already frozen to PRM_SampleTests and are
               deliberately not rewritten by this migration. */
            UPDATE dbo.PRM_SpecificationTests
            SET ApprovalStatus=CASE WHEN ApprovalStatus=N'Approved' THEN N'Obsolete' ELSE ApprovalStatus END,
                IsActive=0,
                IsDefaultForCategory=0
            WHERE SpecificationNo=@SpecificationNo
              AND SampleCategory=@SampleCategory
              AND IsActive=1;

            UPDATE dbo.PRM_SpecificationTests
            SET IsDefaultForCategory=0
            WHERE SpecificationNo=@SpecificationNo
              AND SampleCategory=@SampleCategory
              AND VersionNo<>@NewVersion
              AND IsDefaultForCategory=1;

            INSERT dbo.PRM_SpecificationTests
            (
                SpecificationNo,SampleCategory,ItemCode,ProductionStage,CompendialReference,VersionNo,
                TestCode,TestName,SpecificationText,Unit,ResultType,SpecificationLimit,
                RequiredTest,MinimumElapsedHours,SortOrder,ApprovalStatus,ReviewedBy,ReviewedDate,
                ApprovedBy,ApprovedDate,EffectiveDate,IsActive,CreatedBy,IsDefaultForCategory
            )
            SELECT
                @SpecificationNo,@SampleCategory,N'*',@ProductionStage,expected.CompendialReference,@NewVersion,
                expected.TestCode,expected.TestName,expected.SpecificationText,expected.Unit,expected.ResultType,expected.SpecificationLimit,
                1,expected.MinimumElapsedHours,expected.SortOrder,N'Approved',
                N'Controlled PRM Profile Review 20260913',SYSDATETIME(),
                N'Controlled PRM Profile Approval 20260913',SYSDATETIME(),CAST(SYSDATETIME() AS date),
                1,N'Controlled PRM Standard Profile Readiness 20260913',1
            FROM @Tests expected;

            SET @ReadyVersion=@NewVersion;
        END;

        IF (SELECT COUNT(1)
            FROM dbo.PRM_SpecificationTests s
            WHERE s.SpecificationNo=@SpecificationNo
              AND s.SampleCategory=@SampleCategory
              AND s.VersionNo=@ReadyVersion
              AND s.ApprovalStatus=N'Approved'
              AND s.IsActive=1
              AND UPPER(LTRIM(RTRIM(ISNULL(s.ItemCode,N''))))=N'*'
              AND UPPER(LTRIM(RTRIM(ISNULL(s.ProductionStage,N''))))=
                  UPPER(LTRIM(RTRIM(ISNULL(@ProductionStage,N'')))))<>7
            THROW 54602, 'The controlled PRM standard profile readiness repair did not produce seven approved active tests.', 1;

        SET @ScopeId+=1;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260913_000' AS MigrationVersion;
