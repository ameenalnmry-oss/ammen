SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_SpecificationTests', N'U') IS NULL
        THROW 53100, 'Required table dbo.PRM_SpecificationTests is missing.', 1;

    IF COL_LENGTH(N'dbo.PRM_SpecificationTests', N'ReviewedBy') IS NULL
        ALTER TABLE dbo.PRM_SpecificationTests ADD ReviewedBy NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.PRM_SpecificationTests', N'ReviewedDate') IS NULL
        ALTER TABLE dbo.PRM_SpecificationTests ADD ReviewedDate DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.PRM_SpecificationTests', N'IsDefaultForCategory') IS NULL
        ALTER TABLE dbo.PRM_SpecificationTests ADD IsDefaultForCategory BIT NOT NULL
            CONSTRAINT DF_PRM_SpecificationTests_Default_20260823 DEFAULT (0);

    /* Align the database state machine with the application review step. */
    IF EXISTS
    (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.PRM_SpecificationTests')
          AND name = N'CK_PRM_SpecificationTests_Approval'
    )
        ALTER TABLE dbo.PRM_SpecificationTests DROP CONSTRAINT CK_PRM_SpecificationTests_Approval;

    /*
       Retire only the known system-approved category-wide version 2 records.
       Historical PRM_SampleTests snapshots are not changed.
    */
    UPDATE dbo.PRM_SpecificationTests
    SET ApprovalStatus=N'Obsolete',
        IsActive=0,
        IsDefaultForCategory=0
    WHERE VersionNo=2
      AND SpecificationNo IN
      (
          N'MIC-RM-SUBSTANCES-001',
          N'MIC-IP-ORAL-SOLID-001',
          N'MIC-FP-ORAL-SOLID-001',
          N'MIC-ST-ORAL-SOLID-001'
      )
      AND CreatedBy=N'Controlled Development Import 20260723';

    IF EXISTS
    (
        SELECT 1
        FROM dbo.PRM_SpecificationTests
        WHERE NOT
        (
            (ApprovalStatus=N'Draft' AND IsActive=0 AND ApprovedBy IS NULL AND ApprovedDate IS NULL)
            OR
            (ApprovalStatus=N'Reviewed' AND ReviewedBy IS NOT NULL AND ReviewedDate IS NOT NULL
                AND IsActive=0 AND ApprovedBy IS NULL AND ApprovedDate IS NULL)
            OR
            (ApprovalStatus=N'Approved' AND ReviewedBy IS NOT NULL AND ReviewedDate IS NOT NULL
                AND ApprovedBy IS NOT NULL AND ApprovedDate IS NOT NULL)
            OR
            (ApprovalStatus=N'Obsolete' AND IsActive=0)
        )
    )
        THROW 53102, 'Legacy PRM specification states require controlled reconciliation before the approval constraint can be trusted.', 1;

    ALTER TABLE dbo.PRM_SpecificationTests WITH CHECK
    ADD CONSTRAINT CK_PRM_SpecificationTests_Approval CHECK
    (
        (ApprovalStatus=N'Draft'
            AND IsActive=0
            AND ApprovedBy IS NULL AND ApprovedDate IS NULL)
        OR
        (ApprovalStatus=N'Reviewed'
            AND ReviewedBy IS NOT NULL AND ReviewedDate IS NOT NULL
            AND IsActive=0
            AND ApprovedBy IS NULL AND ApprovedDate IS NULL)
        OR
        (ApprovalStatus=N'Approved'
            AND ReviewedBy IS NOT NULL AND ReviewedDate IS NOT NULL
            AND ApprovedBy IS NOT NULL AND ApprovedDate IS NOT NULL)
        OR
        (ApprovalStatus=N'Obsolete' AND IsActive=0)
    );

    DECLARE @Draft TABLE
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

    /* Oral-tablet product profiles only. Raw materials and in-process stages
       require their own item/stage-specific, QA-approved specifications. */
    INSERT @Draft
        (SpecificationNo,SampleCategory,TestCode,TestName,SpecificationText,Unit,ResultType,SpecificationLimit,SortOrder)
    SELECT category.SpecificationNo,category.SampleCategory,test.TestCode,test.TestName,
           test.SpecificationText,test.Unit,test.ResultType,test.SpecificationLimit,test.SortOrder
    FROM
    (
        VALUES
        (N'MIC-FP-ORAL-TABLET-001',N'Finished Product'),
        (N'MIC-ST-ORAL-TABLET-001',N'Stability')
    ) category(SpecificationNo,SampleCategory)
    CROSS APPLY
    (
        VALUES
        (N'TAMC',N'Total Aerobic Microbial Count (TAMC)',
         N'NMT 100 CFU/g',N'CFU/g',N'Numeric',CONVERT(DECIMAL(18,3),100),10),
        (N'TYMC',N'Total Yeast and Mold Count (TYMC)',
         N'NMT 10 CFU/g',N'CFU/g',N'Numeric',CONVERT(DECIMAL(18,3),10),20),
        (N'SALMONELLA',N'Salmonella spp.',N'Absent in 10 g',NULL,N'Qualitative',NULL,30),
        (N'ECOLI',N'Escherichia coli',N'Absent in 1 g',NULL,N'Qualitative',NULL,40),
        (N'SAUREUS',N'Staphylococcus aureus',N'Absent in 1 g',NULL,N'Qualitative',NULL,50),
        (N'PAERUGINOSA',N'Pseudomonas aeruginosa',N'Absent in 1 g',NULL,N'Qualitative',NULL,60),
        (N'CALBICANS',N'Candida albicans',N'Absent in 1 g',NULL,N'Qualitative',NULL,70)
    ) test(TestCode,TestName,SpecificationText,Unit,ResultType,SpecificationLimit,SortOrder);

    INSERT dbo.PRM_SpecificationTests
    (
        SpecificationNo,SampleCategory,CompendialReference,VersionNo,
        TestCode,TestName,SpecificationText,Unit,ResultType,SpecificationLimit,
        RequiredTest,SortOrder,ApprovalStatus,ReviewedBy,ReviewedDate,
        ApprovedBy,ApprovedDate,EffectiveDate,IsActive,CreatedBy,IsDefaultForCategory
    )
    SELECT
        source.SpecificationNo,source.SampleCategory,
        N'Site-approved internal oral-tablet specification; USP <61>/<62>',
        3,source.TestCode,source.TestName,source.SpecificationText,source.Unit,
        source.ResultType,source.SpecificationLimit,1,source.SortOrder,
        N'Draft',NULL,NULL,NULL,NULL,NULL,0,N'Controlled Remediation 20260823',0
    FROM @Draft source
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM dbo.PRM_SpecificationTests target
        WHERE target.SpecificationNo=source.SpecificationNo
          AND target.SampleCategory=source.SampleCategory
          AND target.VersionNo=3
          AND target.TestCode=source.TestCode
    );

    IF EXISTS
    (
        SELECT 1
        FROM dbo.PRM_SpecificationTests
        WHERE SpecificationNo IN(N'MIC-FP-ORAL-TABLET-001',N'MIC-ST-ORAL-TABLET-001')
          AND VersionNo=3
          AND (ApprovalStatus<>N'Draft' OR IsActive<>0 OR IsDefaultForCategory<>0)
    )
        THROW 53101, 'The remediated oral-tablet version 3 profile must remain Draft and inactive until QA approval.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
