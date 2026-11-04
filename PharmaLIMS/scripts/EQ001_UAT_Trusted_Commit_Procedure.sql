/* EQ-001 controlled UAT-only procedure staging.
DO NOT run on Production or an approved database. The operator must verify an
isolated UAT clone and set the local opt-in below. This file grants NO permissions.
Do not run the separate SQL permission cutover until end-to-end UAT passes.
The procedure derives signatures/audit evidence FROM SQL state and enforces
rowversion, unique request IDs, and current signer permission transactionally. */
SET NOCOUNT ON;
DECLARE @VerifiedIsolatedUat bit = 0; -- manually change to 1 only after isolated UAT preflight
IF @VerifiedIsolatedUat <> 1
 THROW 56150, 'EQ-001: UAT procedure staging is locked until isolated UAT is confirmed.', 1;
IF OBJECT_ID(N'dbo.LabEquipment',N'U') IS NULL OR
   OBJECT_ID(N'dbo.LabEquipmentSignatures',N'U') IS NULL
 THROW 56151, 'EQ-001: Required equipment schema is missing.', 1;

EXEC(N'CREATE OR ALTER PROCEDURE dbo.EQ001_CommitEquipmentSignature
 @TrustedRequestId uniqueidentifier,
 @ActionType nvarchar(50),
 @EquipmentID int = NULL,
 @EquipmentCode nvarchar(50),
 @Reason nvarchar(500),
 @RequestedStateJson nvarchar(max),
 @ExpectedRowVersion binary(8) = NULL,
 @VerifiedSigner nvarchar(100),
 @SourceWorkstation nvarchar(100) = NULL
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON;
 SET XACT_ABORT ON;

 IF @TrustedRequestId IS NULL OR @TrustedRequestId = ''00000000-0000-0000-0000-000000000000''
    THROW 56130, ''EQ-001: RequestId is required.'', 1;
 IF @ActionType NOT IN (N''EquipmentCreate'',N''EquipmentUpdate'')
    THROW 56131, ''EQ-001: Unsupported action.'', 1;
 IF @ActionType = N''EquipmentCreate'' AND (@EquipmentID IS NOT NULL OR @ExpectedRowVersion IS NOT NULL)
    THROW 56132, ''EQ-001: Create must not supply an existing equipment identity.'', 1;
 IF @ActionType = N''EquipmentUpdate'' AND (@EquipmentID IS NULL OR @EquipmentID<=0 OR @ExpectedRowVersion IS NULL)
    THROW 56133, ''EQ-001: Update requires EquipmentID and original RowVersion.'', 1;
 IF NULLIF(LTRIM(RTRIM(@Reason)),N'''') IS NULL OR LEN(LTRIM(RTRIM(@Reason)))<10
    THROW 56134, ''EQ-001: Reason is required.'', 1;
 IF @RequestedStateJson IS NULL OR ISJSON(@RequestedStateJson)<>1 OR
    LEFT(LTRIM(@RequestedStateJson),1)<>N''{'' OR LEN(@RequestedStateJson)>32768
    THROW 56135, ''EQ-001: Expected a bounded equipment JSON object.'', 1;
 IF OBJECT_ID(N''dbo.LabEquipmentSignatures'',N''U'') IS NULL OR
    COL_LENGTH(N''dbo.LabEquipmentSignatures'',N''TrustedRequestId'') IS NULL OR
    NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N''dbo.LabEquipmentSignatures'')
                AND name=N''UX_LabEquipmentSignatures_TrustedRequestId'' AND is_unique=1)
    THROW 56136, ''EQ-001: Replay protection is not installed.'', 1;
 IF OBJECT_ID(N''dbo.AuditTrail'',N''U'') IS NULL
    THROW 56137, ''EQ-001: Required audit trail table is missing.'', 1;

 DECLARE @proposed TABLE
 (
    EquipmentCode nvarchar(50),EquipmentName nvarchar(200),EquipmentType nvarchar(100),
    Department nvarchar(100),Location nvarchar(200),Manufacturer nvarchar(150),
    Model nvarchar(100),SerialNumber nvarchar(100),
    GmpCritical bit,EquipmentStatus nvarchar(30),QualificationStatus nvarchar(30),
    LastQualificationDate date,NextQualificationDate date,
    CalibrationRequired bit,CalibrationStatus nvarchar(30),
    LastCalibrationDate date,NextCalibrationDate date,
    MethodReference nvarchar(200),Notes nvarchar(500)
 );
 INSERT INTO @proposed
 SELECT * FROM OPENJSON(@RequestedStateJson)
 WITH (
    EquipmentCode nvarchar(50) ''$.EquipmentCode'',
    EquipmentName nvarchar(200) ''$.EquipmentName'',
    EquipmentType nvarchar(100) ''$.EquipmentType'',
    Department nvarchar(100) ''$.Department'',
    Location nvarchar(200) ''$.Location'',
    Manufacturer nvarchar(150) ''$.Manufacturer'',
    Model nvarchar(100) ''$.Model'',
    SerialNumber nvarchar(100) ''$.SerialNumber'',
    GmpCritical bit ''$.GmpCritical'',
    EquipmentStatus nvarchar(30) ''$.EquipmentStatus'',
    QualificationStatus nvarchar(30) ''$.QualificationStatus'',
    LastQualificationDate date ''$.LastQualificationDate'',
    NextQualificationDate date ''$.NextQualificationDate'',
    CalibrationRequired bit ''$.CalibrationRequired'',
    CalibrationStatus nvarchar(30) ''$.CalibrationStatus'',
    LastCalibrationDate date ''$.LastCalibrationDate'',
    NextCalibrationDate date ''$.NextCalibrationDate'',
    MethodReference nvarchar(200) ''$.MethodReference'',
    Notes nvarchar(500) ''$.Notes''
 );
 IF (SELECT COUNT(*) FROM @proposed)<>1 OR
    NOT EXISTS (
       SELECT 1 FROM @proposed WHERE EquipmentCode=@EquipmentCode
          AND NULLIF(LTRIM(RTRIM(EquipmentName)),N'''') IS NOT NULL
          AND NULLIF(LTRIM(RTRIM(EquipmentType)),N'''') IS NOT NULL
          AND EquipmentStatus IS NOT NULL AND QualificationStatus IS NOT NULL
          AND CalibrationStatus IS NOT NULL AND GmpCritical IS NOT NULL
          AND CalibrationRequired IS NOT NULL
    )
    THROW 56138, ''EQ-001: Equipment proposal is incomplete or inconsistent.'', 1;

 DECLARE @SignerRole nvarchar(100),@OldState nvarchar(max)=NULL,@NewState nvarchar(max),
         @SavedID int;
 BEGIN TRY
    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
    BEGIN TRANSACTION;

    SELECT @SignerRole=Role FROM dbo.Users WITH (UPDLOCK,HOLDLOCK)
    WHERE Username=@VerifiedSigner AND ISNULL(IsActive,1)=1
      AND ISNULL(MustChangePassword,0)=0 AND ISNULL(CanManageSettings,0)=1
      AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()));
    IF @SignerRole IS NULL
       THROW 56139, ''EQ-001: Signer is no longer authorized.'', 1;
    IF EXISTS (SELECT 1 FROM dbo.LabEquipmentSignatures WITH (UPDLOCK,HOLDLOCK)
               WHERE TrustedRequestId=@TrustedRequestId)
       THROW 56140, ''EQ-001: Duplicate signature request.'', 1;

    IF @ActionType=N''EquipmentUpdate''
    BEGIN
       IF NOT EXISTS (SELECT 1 FROM dbo.LabEquipment WITH (UPDLOCK,HOLDLOCK)
                      WHERE EquipmentID=@EquipmentID AND RowVersion=@ExpectedRowVersion)
           THROW 56141, ''EQ-001: Equipment version has changed.'', 1;
       SELECT @OldState=(SELECT EquipmentCode,EquipmentName,EquipmentType,
          Department,Location,Manufacturer,Model,SerialNumber,GmpCritical,
          EquipmentStatus,QualificationStatus,LastQualificationDate,NextQualificationDate,
          CalibrationRequired,CalibrationStatus,LastCalibrationDate,NextCalibrationDate,
          MethodReference,Notes,IsActive
          FROM dbo.LabEquipment WHERE EquipmentID=@EquipmentID
          FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
       UPDATE e SET EquipmentCode=d.EquipmentCode,EquipmentName=d.EquipmentName,
          EquipmentType=d.EquipmentType,Department=d.Department,Location=d.Location,
          Manufacturer=d.Manufacturer,Model=d.Model,SerialNumber=d.SerialNumber,
          GmpCritical=d.GmpCritical,EquipmentStatus=d.EquipmentStatus,
          QualificationStatus=d.QualificationStatus,
          LastQualificationDate=d.LastQualificationDate,NextQualificationDate=d.NextQualificationDate,
          CalibrationRequired=d.CalibrationRequired,CalibrationStatus=d.CalibrationStatus,
          LastCalibrationDate=d.LastCalibrationDate,NextCalibrationDate=d.NextCalibrationDate,
          MethodReference=d.MethodReference,Notes=d.Notes,
          IsActive=CASE WHEN d.EquipmentStatus=N''Retired'' THEN 0 ELSE 1 END,
          UpdatedBy=@VerifiedSigner,UpdatedAt=SYSUTCDATETIME()
       FROM dbo.LabEquipment e CROSS JOIN @proposed d
       WHERE e.EquipmentID=@EquipmentID AND e.RowVersion=@ExpectedRowVersion;
       IF @@ROWCOUNT<>1
          THROW 56142, ''EQ-001: Concurrent equipment modification detected.'', 1;
       SET @SavedID=@EquipmentID;
    END
    ELSE
    BEGIN
       INSERT dbo.LabEquipment
       (EquipmentCode,EquipmentName,EquipmentType,Department,Location,Manufacturer,
        Model,SerialNumber,GmpCritical,EquipmentStatus,QualificationStatus,
        LastQualificationDate,NextQualificationDate,CalibrationRequired,
        CalibrationStatus,LastCalibrationDate,NextCalibrationDate,
        MethodReference,Notes,IsActive,CreatedBy)
       SELECT EquipmentCode,EquipmentName,EquipmentType,Department,Location,Manufacturer,
        Model,SerialNumber,GmpCritical,EquipmentStatus,QualificationStatus,
        LastQualificationDate,NextQualificationDate,CalibrationRequired,
        CalibrationStatus,LastCalibrationDate,NextCalibrationDate,
        MethodReference,Notes,
        CASE WHEN EquipmentStatus=N''Retired'' THEN 0 ELSE 1 END,@VerifiedSigner
       FROM @proposed;
       SET @SavedID=CONVERT(int,SCOPE_IDENTITY());
       IF @SavedID IS NULL THROW 56143, ''EQ-001: Equipment creation failed.'', 1;
    END;

    SELECT @NewState=(SELECT EquipmentCode,EquipmentName,EquipmentType,
       Department,Location,Manufacturer,Model,SerialNumber,GmpCritical,
       EquipmentStatus,QualificationStatus,LastQualificationDate,NextQualificationDate,
       CalibrationRequired,CalibrationStatus,LastCalibrationDate,NextCalibrationDate,
       MethodReference,Notes,IsActive
       FROM dbo.LabEquipment WHERE EquipmentID=@SavedID
       FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
    IF @NewState IS NULL
       THROW 56144, ''EQ-001: Post-save evidence could not be verified.'', 1;

    INSERT dbo.LabEquipmentSignatures
    (EquipmentID,ActionType,MeaningOfSignature,ActionReason,OldStateJson,
     NewStateJson,SignedBy,UserRole,SignedAt,SourceWorkstation,TrustedRequestId)
    VALUES
    (@SavedID,CASE WHEN @ActionType=N''EquipmentCreate'' THEN N''Create'' ELSE N''Update'' END,
     CASE WHEN @ActionType=N''EquipmentCreate''
          THEN N''Laboratory Equipment Master Create'' ELSE N''Laboratory Equipment Master Update'' END,
     @Reason,@OldState,@NewState,@VerifiedSigner,@SignerRole,SYSUTCDATETIME(),
     @SourceWorkstation,@TrustedRequestId);

    INSERT dbo.AuditTrail
    (TableName,RecordID,Action,OldValue,NewValue,Reason,ChangedBy,PerformedBy,
     ModuleName,SourceApplication,SourceWorkstation)
    VALUES
    (N''LabEquipment'',@SavedID,
      CASE WHEN @ActionType=N''EquipmentCreate'' THEN N''Laboratory Equipment Created''
           ELSE N''Laboratory Equipment Updated'' END,
      @OldState,@NewState,@Reason,@VerifiedSigner,@VerifiedSigner,
      N''Laboratory'',N''PharmaLIMS.TrustedSigning'',@SourceWorkstation);

    COMMIT TRANSACTION;
 END TRY
 BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
    THROW;
 END CATCH;
END');
PRINT N'EQ-001: Trusted signature procedure staged in isolated UAT; no EXECUTE permission granted.';
