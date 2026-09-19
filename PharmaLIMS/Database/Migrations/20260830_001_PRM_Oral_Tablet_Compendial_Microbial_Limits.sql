SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Controlled PRM compendial-limit correction.

    Scope:
      - Production / In-Process oral-tablet standard profile
      - Finished Product oral-tablet standard profile
      - Stability oral-tablet standard profile

    New controlled profile version 2 uses the harmonized USP <1111> acceptance
    criteria for non-aqueous oral preparations:
      TAMC: NMT 10^3 CFU/g (1000 CFU/g)
      TYMC: NMT 10^2 CFU/g (100 CFU/g)

    Historical PRM_SampleTests snapshots are deliberately not changed.
    Existing samples remain bound to their recorded specification version.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NULL
        THROW 53500, 'Required table dbo.PRM_SpecificationTests is missing.', 1;
    IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ItemCode') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ProductionStage') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedBy') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'IsDefaultForCategory') IS NULL
        THROW 53501, 'PRM specification governance columns are missing. Apply the controlled PRM migrations first.', 1;

    DECLARE @Profiles TABLE
    (
        SpecificationNo NVARCHAR(120) NOT NULL PRIMARY KEY,
        SampleCategory NVARCHAR(40) NOT NULL
    );

    INSERT @Profiles(SpecificationNo,SampleCategory)
    VALUES
      (N'MIC-IP-ORAL-TABLET-STD-001',N'Production / In-Process'),
      (N'MIC-FP-ORAL-TABLET-STD-001',N'Finished Product'),
      (N'MIC-ST-ORAL-TABLET-STD-001',N'Stability');

    IF EXISTS
    (
        SELECT 1
        FROM @Profiles p
        WHERE (SELECT COUNT(1)
               FROM dbo.PRM_SpecificationTests s
               WHERE s.SpecificationNo=p.SpecificationNo
                 AND s.SampleCategory=p.SampleCategory
                 AND s.VersionNo=1) <> 7
    )
        THROW 53502, 'The controlled oral-tablet standard profile version 1 is missing or incomplete.', 1;

    /* Never overwrite a manually-created or partially-created version 2. */
    IF EXISTS
    (
        SELECT 1
        FROM dbo.PRM_SpecificationTests s
        INNER JOIN @Profiles p
            ON p.SpecificationNo=s.SpecificationNo
           AND p.SampleCategory=s.SampleCategory
        WHERE s.VersionNo=2
    )
    BEGIN
        IF EXISTS
        (
            SELECT 1
            FROM @Profiles p
            WHERE (SELECT COUNT(1)
                   FROM dbo.PRM_SpecificationTests s
                   WHERE s.SpecificationNo=p.SpecificationNo
                     AND s.SampleCategory=p.SampleCategory
                     AND s.VersionNo=2) <> 7
        )
            THROW 53503, 'An incomplete oral-tablet standard profile version 2 already exists; controlled reconciliation is required.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM dbo.PRM_SpecificationTests s
            INNER JOIN @Profiles p
                ON p.SpecificationNo=s.SpecificationNo
               AND p.SampleCategory=s.SampleCategory
            WHERE s.VersionNo=2
              AND
              (
                  (UPPER(LTRIM(RTRIM(s.TestCode)))=N'TAMC' AND
                      (ISNULL(s.SpecificationLimit,-1)<>CONVERT(DECIMAL(18,3),1000)
                       OR s.SpecificationText<>N'NMT 1000 CFU/g'))
                  OR
                  (UPPER(LTRIM(RTRIM(s.TestCode)))=N'TYMC' AND
                      (ISNULL(s.SpecificationLimit,-1)<>CONVERT(DECIMAL(18,3),100)
                       OR s.SpecificationText<>N'NMT 100 CFU/g'))
              )
        )
            THROW 53504, 'Oral-tablet standard profile version 2 conflicts with the requested compendial limits.', 1;
    END
    ELSE
    BEGIN
        INSERT dbo.PRM_SpecificationTests
        (
            SpecificationNo,SampleCategory,ItemCode,ProductionStage,CompendialReference,VersionNo,
            TestCode,TestName,SpecificationText,Unit,ResultType,SpecificationLimit,
            RequiredTest,SortOrder,ApprovalStatus,ReviewedBy,ReviewedDate,
            ApprovedBy,ApprovedDate,EffectiveDate,IsActive,CreatedBy,IsDefaultForCategory
        )
        SELECT
            s.SpecificationNo,
            s.SampleCategory,
            s.ItemCode,
            s.ProductionStage,
            N'USP <61>/<62>/<1111>; non-aqueous oral preparations: TAMC NMT 10^3 CFU/g; TYMC NMT 10^2 CFU/g',
            2,
            s.TestCode,
            s.TestName,
            CASE UPPER(LTRIM(RTRIM(s.TestCode)))
                WHEN N'TAMC' THEN N'NMT 1000 CFU/g'
                WHEN N'TYMC' THEN N'NMT 100 CFU/g'
                ELSE s.SpecificationText
            END,
            CASE UPPER(LTRIM(RTRIM(s.TestCode)))
                WHEN N'TAMC' THEN N'CFU/g'
                WHEN N'TYMC' THEN N'CFU/g'
                ELSE s.Unit
            END,
            s.ResultType,
            CASE UPPER(LTRIM(RTRIM(s.TestCode)))
                WHEN N'TAMC' THEN CONVERT(DECIMAL(18,3),1000)
                WHEN N'TYMC' THEN CONVERT(DECIMAL(18,3),100)
                ELSE s.SpecificationLimit
            END,
            s.RequiredTest,
            s.SortOrder,
            N'Approved',
            N'Controlled Release Review 20260830',
            SYSDATETIME(),
            N'Controlled Release Approval 20260830',
            SYSDATETIME(),
            CAST(SYSDATETIME() AS date),
            1,
            N'Controlled Release Compendial Correction 20260830',
            s.IsDefaultForCategory
        FROM dbo.PRM_SpecificationTests s
        INNER JOIN @Profiles p
            ON p.SpecificationNo=s.SpecificationNo
           AND p.SampleCategory=s.SampleCategory
        WHERE s.VersionNo=1;
    END;

    /* Version 2 becomes the current reusable standard; version 1 remains historical evidence. */
    UPDATE s
    SET s.ApprovalStatus=N'Obsolete',
        s.IsActive=0,
        s.IsDefaultForCategory=0
    FROM dbo.PRM_SpecificationTests s
    INNER JOIN @Profiles p
        ON p.SpecificationNo=s.SpecificationNo
       AND p.SampleCategory=s.SampleCategory
    WHERE s.VersionNo=1
      AND s.ApprovalStatus=N'Approved';

    IF EXISTS
    (
        SELECT 1
        FROM @Profiles p
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM dbo.PRM_SpecificationTests s
            WHERE s.SpecificationNo=p.SpecificationNo
              AND s.SampleCategory=p.SampleCategory
              AND s.VersionNo=2
              AND s.ApprovalStatus=N'Approved'
              AND s.IsActive=1
              AND UPPER(LTRIM(RTRIM(s.TestCode)))=N'TAMC'
              AND s.SpecificationLimit=CONVERT(DECIMAL(18,3),1000)
              AND s.SpecificationText=N'NMT 1000 CFU/g'
        )
        OR NOT EXISTS
        (
            SELECT 1
            FROM dbo.PRM_SpecificationTests s
            WHERE s.SpecificationNo=p.SpecificationNo
              AND s.SampleCategory=p.SampleCategory
              AND s.VersionNo=2
              AND s.ApprovalStatus=N'Approved'
              AND s.IsActive=1
              AND UPPER(LTRIM(RTRIM(s.TestCode)))=N'TYMC'
              AND s.SpecificationLimit=CONVERT(DECIMAL(18,3),100)
              AND s.SpecificationText=N'NMT 100 CFU/g'
        )
    )
        THROW 53505, 'The oral-tablet compendial standard profile could not be activated correctly.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
