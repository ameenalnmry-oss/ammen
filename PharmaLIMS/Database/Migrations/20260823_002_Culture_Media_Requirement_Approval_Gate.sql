SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.CultureMediaQualificationRequirements', N'U') IS NULL
        THROW 53200, 'Required table dbo.CultureMediaQualificationRequirements is missing.', 1;

    IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements', N'ApprovalStatus') IS NULL
        ALTER TABLE dbo.CultureMediaQualificationRequirements ADD ApprovalStatus NVARCHAR(30) NOT NULL
            CONSTRAINT DF_CultureMediaQualificationRequirements_Status_20260823 DEFAULT N'Draft';
    IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements', N'ReviewedBy') IS NULL
        ALTER TABLE dbo.CultureMediaQualificationRequirements ADD ReviewedBy NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements', N'ReviewedAt') IS NULL
        ALTER TABLE dbo.CultureMediaQualificationRequirements ADD ReviewedAt DATETIME2(0) NULL;

    UPDATE dbo.CultureMediaQualificationRequirements
    SET ApprovalStatus=N'Approved'
    WHERE ApprovedBy IS NOT NULL
      AND ApprovedAt IS NOT NULL
      AND ApprovedBy<>N'System Baseline - QA confirmation required';

    /* System text is not a human QA approval. Preserve the records as inactive drafts. */
    UPDATE dbo.CultureMediaQualificationRequirements
    SET ApprovalStatus=N'Draft',
        IsActive=0,
        ReviewedBy=NULL,
        ReviewedAt=NULL,
        ApprovedBy=NULL,
        ApprovedAt=NULL
    WHERE ApprovedBy=N'System Baseline - QA confirmation required';

    IF EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
          AND name=N'CK_CultureMediaQualificationRequirements_Approval_20260823'
    )
        ALTER TABLE dbo.CultureMediaQualificationRequirements
            DROP CONSTRAINT CK_CultureMediaQualificationRequirements_Approval_20260823;

    ALTER TABLE dbo.CultureMediaQualificationRequirements WITH CHECK
    ADD CONSTRAINT CK_CultureMediaQualificationRequirements_Approval_20260823 CHECK
    (
        (ApprovalStatus=N'Draft' AND IsActive=0 AND ApprovedBy IS NULL AND ApprovedAt IS NULL)
        OR
        (ApprovalStatus=N'Reviewed' AND ReviewedBy IS NOT NULL AND ReviewedAt IS NOT NULL AND IsActive=0)
        OR
        (ApprovalStatus=N'Approved' AND ApprovedBy IS NOT NULL AND ApprovedAt IS NOT NULL)
        OR
        (ApprovalStatus=N'Obsolete' AND IsActive=0)
    );

    IF EXISTS
    (
        SELECT 1
        FROM dbo.CultureMediaQualificationRequirements
        WHERE IsActive=1
          AND (ApprovalStatus<>N'Approved' OR ApprovedBy IS NULL OR ApprovedAt IS NULL)
    )
        THROW 53201, 'An active Culture Media qualification requirement lacks controlled QA approval.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
