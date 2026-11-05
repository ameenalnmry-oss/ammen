-- MEDICA EQ-001 / trusted service equipment draft / source review only.
-- Do not remove SECURITY HOLD until independent QA/security review of the complete
-- Draft + Link + Confirm + Submit lifecycle and native identity-based acceptance.
SET NOCOUNT ON;
SET XACT_ABORT ON;
THROW 57198, 'SECURITY HOLD: Equipment draft installer requires review and controlled UAT approval.', 1;
IF DB_NAME() <> N'EQ001_UAT_AMEEN_20261024'
 OR ORIGINAL_LOGIN() <> N'AMEEN\PharmaLIMSSigner'
 THROW 57209, 'Wrong controlled SQL installation identity or target.', 1;
IF OBJECT_ID(N'dbo.EQ001_TrustedEquipmentDraft',N'P') IS NOT NULL
 OR OBJECT_ID(N'dbo.EQ001_EquipmentDraftReceipts',N'U') IS NOT NULL
 OR DATABASE_PRINCIPAL_ID(N'PharmaLIMS_TrustedEquipmentSigner') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentActivityAudit',N'U') IS NULL
 THROW 57210, 'Existing objects or missing UAT prerequisites require independent review.', 1;
BEGIN TRY
  BEGIN TRANSACTION;
  CREATE TABLE dbo.EQ001_EquipmentDraftReceipts
  (
    RequestId uniqueidentifier NOT NULL CONSTRAINT PK_EQ001_EquipmentDraftReceipts PRIMARY KEY,
    ActivityID bigint NOT NULL CONSTRAINT FK_EQ001_EquipmentDraftReceipts_Activity
      FOREIGN KEY REFERENCES dbo.MicroEquipmentActivities(ActivityID),
    Actor nvarchar(100) NOT NULL,
    CallerSid nvarchar(184) NOT NULL,
    AuthenticationVersion binary(8) NOT NULL,
    PayloadHash binary(32) NOT NULL,
    Reason nvarchar(500) NOT NULL,
    Meaning nvarchar(200) NOT NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_EQ001_EquipmentDraftReceipts_CreatedAt DEFAULT SYSUTCDATETIME()
  );
  EXEC(N'CREATE TRIGGER dbo.TRG_EQ001_EquipmentDraftReceipts_AppendOnly
  ON dbo.EQ001_EquipmentDraftReceipts AFTER UPDATE, DELETE AS
  BEGIN SET NOCOUNT ON;
      THROW 57211, ''Equipment draft receipts are immutable.'', 1;
  END;');
  EXEC sys.sp_executesql N'CREATE PROCEDURE dbo.EQ001_TrustedEquipmentDraft
    @RequestId uniqueidentifier, @EquipmentID int, @ActivityType nvarchar(60),
    @MethodReference nvarchar(200), @Payload nvarchar(max),
    @Actor nvarchar(100), @AuthenticationVersion binary(8),
    @CallerSid nvarchar(184), @Reason nvarchar(500), @Meaning nvarchar(200)
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF DB_NAME() <> N''EQ001_UAT_AMEEN_20261024''
        OR ORIGINAL_LOGIN() <> N''AMEEN\PharmaLIMSSigner''
        OR @@TRANCOUNT < 1
        THROW 57201, ''Trusted equipment draft requires the authorized signer and a controlled transaction.'', 1;
    IF @RequestId IS NULL OR @RequestId=''00000000-0000-0000-0000-000000000000''
        OR @EquipmentID <= 0 OR @Actor IS NULL OR LEN(@Actor)=0
        OR @CallerSid IS NULL OR LEN(@CallerSid)<10
        OR @AuthenticationVersion IS NULL OR DATALENGTH(@AuthenticationVersion)<>8
        OR @ActivityType NOT IN (N''Use'',N''Incubation'',N''Calibration'',N''Periodic Verification'',
                                 N''Cleaning'',N''Sanitization'',N''Maintenance'',N''Breakdown'',N''Repair'')
        OR NULLIF(LTRIM(RTRIM(@MethodReference)),N'''') IS NULL
        OR NULLIF(LTRIM(RTRIM(@Reason)),N'''') IS NULL
        OR NULLIF(LTRIM(RTRIM(@Meaning)),N'''') IS NULL
        OR LEN(@MethodReference)>200 OR LEN(@Reason)>500 OR LEN(@Meaning)>200
        OR @Payload IS NULL OR LEN(@Payload)>2048 OR ISJSON(@Payload)<>1
        THROW 57202, ''Invalid authenticated equipment draft values.'', 1;

    IF TRY_CONVERT(int,JSON_VALUE(@Payload,N''$.EquipmentId''))<>@EquipmentID
       OR JSON_VALUE(@Payload,N''$.ActivityType'')<>@ActivityType
       OR JSON_VALUE(@Payload,N''$.MethodReference'')<>@MethodReference
       OR (SELECT COUNT(*) FROM OPENJSON(@Payload))<>3
       THROW 57203, ''Canonical equipment payload and parameters differ.'', 1;
    IF NOT EXISTS
      (SELECT 1 FROM dbo.Users WITH(UPDLOCK,HOLDLOCK)
       WHERE Username=@Actor AND IsActive=1 AND IsLocked=0 AND MustChangePassword=0
         AND CanEnterResults=1 AND AuthenticationRowVersion=@AuthenticationVersion)
        THROW 57204, ''Authenticated actor or current permissions changed.'', 1;

    DECLARE @PayloadHash binary(32)=HASHBYTES(''SHA2_256'',CONVERT(varbinary(max),@Payload));
    DECLARE @ActivityID bigint;
    SELECT @ActivityID=ActivityID
    FROM dbo.EQ001_EquipmentDraftReceipts WITH(UPDLOCK,HOLDLOCK)
    WHERE RequestId=@RequestId;
    IF @ActivityID IS NOT NULL
    BEGIN
        IF NOT EXISTS
           (SELECT 1 FROM dbo.EQ001_EquipmentDraftReceipts
            WHERE RequestId=@RequestId AND Actor=@Actor AND CallerSid=@CallerSid
              AND AuthenticationVersion=@AuthenticationVersion
              AND PayloadHash=@PayloadHash AND Reason=@Reason AND Meaning=@Meaning)
            THROW 57205, ''Reused request identity differs from its immutable receipt.'', 1;
        SELECT @ActivityID AS ActivityID;
        RETURN;
    END;

    DECLARE @Created table(ActivityID bigint);
    INSERT dbo.MicroEquipmentActivities
        (EquipmentID,ActivityType,PerformedBy,MethodReference,
         EquipmentCodeSnapshot,EquipmentNameSnapshot,CalibrationStatusSnapshot,QualificationStatusSnapshot)
    OUTPUT INSERTED.ActivityID INTO @Created
    SELECT e.EquipmentID,@ActivityType,@Actor,@MethodReference,
           e.EquipmentCode,e.EquipmentName,e.CalibrationStatus,e.QualificationStatus
    FROM dbo.LabEquipment e WITH(UPDLOCK,HOLDLOCK)
    WHERE e.EquipmentID=@EquipmentID AND e.IsActive=1
      AND e.EquipmentCode LIKE N''MIC-EQ-[0-9][0-9][0-9]''
      AND LEN(e.EquipmentCode)=10
      AND TRY_CONVERT(int,SUBSTRING(e.EquipmentCode,8,3))>=1
      AND UPPER(LTRIM(RTRIM(e.Department)))=N''MICROBIOLOGY'';
    SELECT @ActivityID=ActivityID FROM @Created;
    IF @ActivityID IS NULL
       THROW 57206, ''Selected equipment does not meet controlled microbiology scope.'', 1;

    INSERT dbo.MicroEquipmentActivityAudit
       (ActivityID,ActionType,NewStateJson,ActionReason,ChangedBy)
    SELECT a.ActivityID,N''DRAFT CREATED'',
           (SELECT a.ActivityID,a.EquipmentID,a.ActivityType,a.ActivityStatus,a.PerformedBy,
                   a.ActualStartAt,a.ActualEndAt,a.EquipmentCodeSnapshot,
                   a.CalibrationStatusSnapshot,a.QualificationStatusSnapshot,
                   a.ActivityReason,a.MethodReference
            FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES),
           @Reason,@Actor
    FROM dbo.MicroEquipmentActivities a WITH(UPDLOCK,HOLDLOCK)
    WHERE a.ActivityID=@ActivityID;
    IF @@ROWCOUNT<>1 THROW 57207, ''Draft audit was not recorded.'', 1;

    INSERT dbo.EQ001_EquipmentDraftReceipts
       (RequestId,ActivityID,Actor,CallerSid,AuthenticationVersion,
        PayloadHash,Reason,Meaning)
    VALUES(@RequestId,@ActivityID,@Actor,@CallerSid,@AuthenticationVersion,
           @PayloadHash,@Reason,@Meaning);
    IF @@ROWCOUNT<>1 THROW 57208, ''Equipment draft receipt was not recorded.'', 1;
    SELECT @ActivityID AS ActivityID;
END';

  GRANT EXECUTE ON dbo.EQ001_TrustedEquipmentDraft TO PharmaLIMS_TrustedEquipmentSigner;
  IF EXISTS
  (
    SELECT 1 FROM sys.database_permissions p
    WHERE p.major_id=OBJECT_ID(N'dbo.EQ001_TrustedEquipmentDraft')
    AND p.grantee_principal_id=DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentWindowsUat')
    AND p.permission_name='EXECUTE' AND p.state='G'
  )
      THROW 57212, 'Windows client role must not execute trusted equipment draft.', 1;
  COMMIT TRANSACTION;
  PRINT N'Trusted equipment draft procedure and append-only receipt installed.';
END TRY
BEGIN CATCH
  IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;