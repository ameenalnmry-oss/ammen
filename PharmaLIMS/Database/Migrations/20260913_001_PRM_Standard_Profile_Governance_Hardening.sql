SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    PRM standard-profile governance hardening.

    This migration supersedes 20260913_000 for databases where that migration
    has not yet run. It intentionally does NOT create an approved PRM master
    specification and does NOT retire a differing site-approved profile.

    Governance rules:
      1. Existing active Approved canonical profiles are preserved exactly as-is,
         even when their approved limits differ from the draft template below.
      2. Any still-active profile auto-approved by the retired 20260913_000
         migration is removed from authoritative use. If it has never been used
         by a PRM sample it is demoted to Draft and its synthetic review/approval
         identities are cleared. If it has already been referenced, it is made
         Obsolete/inactive so historical traceability is preserved without
         allowing new registrations to consume it.
      3. When no active Approved canonical profile exists, at most one Draft or
         Reviewed candidate is retained/created for QA processing through the
         normal Specification Master Review -> Approve workflow.
      4. This migration never inserts PRM_SpecificationSignatures and never
         substitutes for independent reviewer/approver electronic signatures.
      5. The oral-tablet values below are DRAFT TEMPLATE CONTENT ONLY. They must
         be reconciled against the approved Medica/product/site specification by
         the reviewer and approver before the profile can become authoritative.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NULL
        THROW 54610, 'Required table dbo.PRM_SpecificationTests is missing.', 1;

    IF OBJECT_ID(N'dbo.PRM_SpecificationSignatures',N'U') IS NULL
        THROW 54611, 'Required table dbo.PRM_SpecificationSignatures is missing. Apply the controlled PRM specification governance migrations first.', 1;

    IF OBJECT_ID(N'dbo.PRM_Samples',N'U') IS NULL
        THROW 54612, 'Required table dbo.PRM_Samples is missing.', 1;

    IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ItemCode') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ProductionStage') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'CompendialReference') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedBy') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedDate') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ApprovedBy') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ApprovedDate') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'IsDefaultForCategory') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'MinimumElapsedHours') IS NULL
        THROW 54613, 'PRM specification governance columns are missing. Apply the earlier controlled PRM migrations first.', 1;

    IF COL_LENGTH(N'dbo.PRM_Samples',N'SpecificationNo') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Samples',N'SpecificationVersionNo') IS NULL
        THROW 54614, 'PRM sample specification linkage columns are missing.', 1;

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

    DECLARE @DraftTemplate TABLE
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

    /* Draft-only values. QA must reconcile them against the current approved
       Medica/product/site specification before Review and Approval. */
    INSERT @DraftTemplate
        (TestCode,TestName,SpecificationText,Unit,ResultType,SpecificationLimit,MinimumElapsedHours,SortOrder,CompendialReference)
    VALUES
      (N'TAMC',N'Total Aerobic Microbial Count (TAMC)',N'NMT 1000 CFU/g',N'CFU/g',N'Numeric',CONVERT(DECIMAL(18,3),1000),CONVERT(DECIMAL(9,2),72),10,
       N'DRAFT TEMPLATE - verify approved site/product specification; USP <61>/<1111> reference'),
      (N'TYMC',N'Total Yeast and Mold Count (TYMC)',N'NMT 100 CFU/g',N'CFU/g',N'Numeric',CONVERT(DECIMAL(18,3),100),CONVERT(DECIMAL(9,2),120),20,
       N'DRAFT TEMPLATE - verify approved site/product specification; USP <61>/<1111> reference'),
      (N'SALMONELLA',N'Salmonella spp.',N'Absent in 10 g',NULL,N'Qualitative',NULL,CONVERT(DECIMAL(9,2),120),30,
       N'DRAFT TEMPLATE - verify approved site/product specification; USP <62> method'),
      (N'ECOLI',N'Escherichia coli',N'Absent in 1 g',NULL,N'Qualitative',NULL,CONVERT(DECIMAL(9,2),120),40,
       N'DRAFT TEMPLATE - verify approved site/product specification; USP <62>/<1111> reference'),
      (N'SAUREUS',N'Staphylococcus aureus',N'Absent in 1 g',NULL,N'Qualitative',NULL,CONVERT(DECIMAL(9,2),120),50,
       N'DRAFT TEMPLATE - verify approved site/product specification; USP <62> method'),
      (N'PAERUGINOSA',N'Pseudomonas aeruginosa',N'Absent in 1 g',NULL,N'Qualitative',NULL,CONVERT(DECIMAL(9,2),120),60,
       N'DRAFT TEMPLATE - verify approved site/product specification; USP <62> method'),
      (N'CALBICANS',N'Candida albicans',N'Absent in 1 g',NULL,N'Qualitative',NULL,CONVERT(DECIMAL(9,2),120),70,
       N'DRAFT TEMPLATE - verify approved site/product specification; USP <62> method');

    DECLARE @ScopeId INT=1;
    DECLARE @ScopeCount INT=(SELECT COUNT(1) FROM @Scopes);
    DECLARE @SpecificationNo NVARCHAR(120);
    DECLARE @SampleCategory NVARCHAR(40);
    DECLARE @ProductionStage NVARCHAR(80);
    DECLARE @GeneratedVersion INT;
    DECLARE @CandidateVersion INT;
    DECLARE @NewVersion INT;
    DECLARE @GeneratedReferenced BIT;

    WHILE @ScopeId<=@ScopeCount
    BEGIN
        SELECT
            @SpecificationNo=SpecificationNo,
            @SampleCategory=SampleCategory,
            @ProductionStage=ProductionStage
        FROM @Scopes
        WHERE ScopeId=@ScopeId;

        SET @GeneratedVersion=NULL;
        SET @GeneratedReferenced=0;

        /* Identify only the synthetic approval produced by retired migration
           20260913_000. User-reviewed/approved versions are never modified here. */
        SELECT TOP(1) @GeneratedVersion=s.VersionNo
        FROM dbo.PRM_SpecificationTests s WITH (UPDLOCK,HOLDLOCK)
        WHERE s.SpecificationNo=@SpecificationNo
          AND s.SampleCategory=@SampleCategory
          AND UPPER(LTRIM(RTRIM(ISNULL(s.ItemCode,N''))))=N'*'
          AND UPPER(LTRIM(RTRIM(ISNULL(s.ProductionStage,N''))))=
              UPPER(LTRIM(RTRIM(ISNULL(@ProductionStage,N''))))
          AND s.CreatedBy=N'Controlled PRM Standard Profile Readiness 20260913'
          AND (
                s.ReviewedBy=N'Controlled PRM Profile Review 20260913'
                OR s.ApprovedBy=N'Controlled PRM Profile Approval 20260913'
              )
        ORDER BY s.VersionNo DESC;

        IF @GeneratedVersion IS NOT NULL
        BEGIN
            IF EXISTS
            (
                SELECT 1
                FROM dbo.PRM_Samples p WITH (UPDLOCK,HOLDLOCK)
                WHERE LTRIM(RTRIM(ISNULL(p.SpecificationNo,N'')))=@SpecificationNo
                  AND p.SpecificationVersionNo=@GeneratedVersion
            )
                SET @GeneratedReferenced=1;

            IF @GeneratedReferenced=1
            BEGIN
                /* Preserve referenced historical rows, but remove them from all
                   future authoritative selection. */
                UPDATE dbo.PRM_SpecificationTests
                SET ApprovalStatus=N'Obsolete',
                    IsActive=0,
                    IsDefaultForCategory=0
                WHERE SpecificationNo=@SpecificationNo
                  AND SampleCategory=@SampleCategory
                  AND VersionNo=@GeneratedVersion
                  AND CreatedBy=N'Controlled PRM Standard Profile Readiness 20260913';
            END
            ELSE
            BEGIN
                /* Safe demotion: no sample ever consumed this synthetic approval,
                   so return it to the normal controlled Draft workflow. */
                UPDATE dbo.PRM_SpecificationTests
                SET ApprovalStatus=N'Draft',
                    ReviewedBy=NULL,
                    ReviewedDate=NULL,
                    ApprovedBy=NULL,
                    ApprovedDate=NULL,
                    EffectiveDate=NULL,
                    IsActive=0,
                    IsDefaultForCategory=0
                WHERE SpecificationNo=@SpecificationNo
                  AND SampleCategory=@SampleCategory
                  AND VersionNo=@GeneratedVersion
                  AND CreatedBy=N'Controlled PRM Standard Profile Readiness 20260913';
            END
        END

        /* A real active Approved site profile is authoritative. Do not compare
           it with this migration's draft template and never retire/replace it. */
        IF NOT EXISTS
        (
            SELECT 1
            FROM dbo.PRM_SpecificationTests s WITH (UPDLOCK,HOLDLOCK)
            WHERE s.SpecificationNo=@SpecificationNo
              AND s.SampleCategory=@SampleCategory
              AND UPPER(LTRIM(RTRIM(ISNULL(s.ItemCode,N''))))=N'*'
              AND UPPER(LTRIM(RTRIM(ISNULL(s.ProductionStage,N''))))=
                  UPPER(LTRIM(RTRIM(ISNULL(@ProductionStage,N''))))
              AND s.ApprovalStatus=N'Approved'
              AND s.IsActive=1
              AND (s.EffectiveDate IS NULL OR s.EffectiveDate<=CAST(SYSDATETIME() AS date))
              AND NOT (
                    s.CreatedBy=N'Controlled PRM Standard Profile Readiness 20260913'
                    AND (
                        s.ReviewedBy=N'Controlled PRM Profile Review 20260913'
                        OR s.ApprovedBy=N'Controlled PRM Profile Approval 20260913'
                    )
                  )
        )
        BEGIN
            SET @CandidateVersion=NULL;

            SELECT TOP(1) @CandidateVersion=s.VersionNo
            FROM dbo.PRM_SpecificationTests s WITH (UPDLOCK,HOLDLOCK)
            WHERE s.SpecificationNo=@SpecificationNo
              AND s.SampleCategory=@SampleCategory
              AND UPPER(LTRIM(RTRIM(ISNULL(s.ItemCode,N''))))=N'*'
              AND UPPER(LTRIM(RTRIM(ISNULL(s.ProductionStage,N''))))=
                  UPPER(LTRIM(RTRIM(ISNULL(@ProductionStage,N''))))
              AND s.ApprovalStatus IN (N'Draft',N'Reviewed')
            ORDER BY s.VersionNo DESC;

            IF @CandidateVersion IS NULL
            BEGIN
                SELECT @NewVersion=ISNULL(MAX(VersionNo),0)+1
                FROM dbo.PRM_SpecificationTests WITH (UPDLOCK,HOLDLOCK)
                WHERE SpecificationNo=@SpecificationNo
                  AND SampleCategory=@SampleCategory;

                INSERT dbo.PRM_SpecificationTests
                (
                    SpecificationNo,SampleCategory,ItemCode,ProductionStage,CompendialReference,VersionNo,
                    TestCode,TestName,SpecificationText,Unit,ResultType,SpecificationLimit,
                    RequiredTest,MinimumElapsedHours,SortOrder,ApprovalStatus,ReviewedBy,ReviewedDate,
                    ApprovedBy,ApprovedDate,EffectiveDate,IsActive,CreatedBy,IsDefaultForCategory
                )
                SELECT
                    @SpecificationNo,@SampleCategory,N'*',@ProductionStage,template.CompendialReference,@NewVersion,
                    template.TestCode,template.TestName,template.SpecificationText,template.Unit,template.ResultType,template.SpecificationLimit,
                    1,template.MinimumElapsedHours,template.SortOrder,N'Draft',NULL,NULL,NULL,NULL,NULL,
                    0,N'Controlled PRM Draft Template 20260913_001',0
                FROM @DraftTemplate template;

                SET @CandidateVersion=@NewVersion;
            END
        END

        SET @ScopeId+=1;
    END;

    /* Fail closed if the retired migration's synthetic approval remains usable. */
    IF EXISTS
    (
        SELECT 1
        FROM dbo.PRM_SpecificationTests s
        WHERE s.CreatedBy=N'Controlled PRM Standard Profile Readiness 20260913'
          AND s.ApprovalStatus=N'Approved'
          AND s.IsActive=1
          AND (
                s.ReviewedBy=N'Controlled PRM Profile Review 20260913'
                OR s.ApprovedBy=N'Controlled PRM Profile Approval 20260913'
              )
    )
        THROW 54615, 'Retired PRM migration synthetic approval remains active. PRM registration is blocked until governance reconciliation completes.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260913_001' AS MigrationVersion;
