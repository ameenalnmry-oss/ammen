-- TEST FIXTURE ONLY: disposable EQ001_UAT_* databases; never a site migration.
-- Column types and SQL modules originate from user-supplied AMEEN metadata.
-- Defaults/keys below are synthetic fixture setup. Calibration computed-column
-- expressions and full site constraints were not exported; these tests cover
-- submitted Use activities, not calibration qualification or full site schema.
IF DB_NAME() NOT LIKE N'EQ001[_]UAT[_]%' THROW 56764,'Disposable UAT fixture only.',1;
GO
CREATE TABLE dbo.MicroEquipmentActivities(
 [ActivityID] bigint IDENTITY(1,1) NOT NULL,
 [EquipmentID] int NOT NULL,
 [ActivityType] nvarchar(60) NOT NULL,
 [ActivityStatus] nvarchar(30) NOT NULL,
 [PerformedBy] nvarchar(100) NOT NULL,
 [ActualStartAt] datetimeoffset(0) NULL,
 [ActualEndAt] datetimeoffset(0) NULL,
 [MethodReference] nvarchar(200) NULL,
 [ActivityReason] nvarchar(500) NULL,
 [EquipmentCodeSnapshot] nvarchar(50) NOT NULL,
 [EquipmentNameSnapshot] nvarchar(200) NOT NULL,
 [CalibrationStatusSnapshot] nvarchar(30) NULL,
 [QualificationStatusSnapshot] nvarchar(30) NULL,
 [CreatedAt] datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
 [VersionToken] timestamp,
 PRIMARY KEY(ActivityID)
);
GO
CREATE TABLE dbo.MicroEquipmentActivityAudit(
 [AuditID] bigint IDENTITY(1,1) NOT NULL,
 [ActivityID] bigint NOT NULL,
 [ActionType] nvarchar(60) NOT NULL,
 [OldStateJson] nvarchar(max) NULL,
 [NewStateJson] nvarchar(max) NOT NULL,
 [ActionReason] nvarchar(500) NOT NULL,
 [ChangedBy] nvarchar(100) NOT NULL,
 [ChangedAt] datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
 PRIMARY KEY(AuditID)
);
GO
CREATE TABLE dbo.MicroEquipmentActivityLinks(
 [LinkID] bigint IDENTITY(1,1) NOT NULL,
 [ActivityID] bigint NOT NULL,
 [Module] nvarchar(20) NOT NULL,
 [ParentRecordID] int NOT NULL,
 [ResultRecordID] int NULL,
 [SampleNumberSnapshot] nvarchar(100) NULL,
 [TestCodeSnapshot] nvarchar(100) NULL,
 [LinkedBy] nvarchar(100) NOT NULL,
 [LinkedAt] datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
 PRIMARY KEY(LinkID)
);
GO
CREATE TABLE dbo.MicroEquipmentActivitySignatures(
 [SignatureID] bigint IDENTITY(1,1) NOT NULL,
 [ActivityID] bigint NOT NULL,
 [SignatureMeaning] nvarchar(200) NOT NULL,
 [SignatureReason] nvarchar(500) NOT NULL,
 [ContentHash] varbinary(32) NOT NULL,
 [SignedBy] nvarchar(100) NOT NULL,
 [SignedAt] datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
 PRIMARY KEY(SignatureID)
);
GO
CREATE TABLE dbo.MicroEquipmentCalibrationPoints(
 [ActivityID] bigint NOT NULL,
 [PointNo] int NOT NULL,
 [ReferenceValue] decimal(18,6) NULL,
 [ObservedValue] decimal(18,6) NOT NULL,
 [MaxAbsoluteError] decimal(18,6) NULL,
 [NormalMinimum] decimal(18,6) NULL,
 [NormalMaximum] decimal(18,6) NULL,
 [AbsoluteError] decimal(19,6) NULL,
 [Outcome] nvarchar(4) NOT NULL,
 PRIMARY KEY(ActivityID,PointNo)
);
GO
CREATE TABLE dbo.MicroEquipmentLifecycleDetails(
 [ActivityID] bigint NOT NULL,
 [ProcedureReference] nvarchar(200) NOT NULL,
 [WorkDetails] nvarchar(max) NOT NULL,
 [MaterialName] nvarchar(200) NULL,
 [MaterialLot] nvarchar(100) NULL,
 [Concentration] nvarchar(100) NULL,
 [ContactTimeMinutes] decimal(10,2) NULL,
 [RecordedBy] nvarchar(100) NOT NULL,
 [RecordedAt] datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
 PRIMARY KEY(ActivityID)
);
GO
CREATE TABLE dbo.MicroEquipmentStructuredEvidence(
 [ActivityID] bigint NOT NULL,
 [EvidenceType] nvarchar(30) NOT NULL,
 [ReferenceValue] decimal(18,6) NULL,
 [ObservedValue] decimal(18,6) NULL,
 [MaximumAbsoluteError] decimal(18,6) NULL,
 [AbsoluteError] decimal(19,6) NULL,
 [NormalMinimum] decimal(18,6) NULL,
 [NormalMaximum] decimal(18,6) NULL,
 [CleaningAgentLot] nvarchar(90) NULL,
 [Outcome] nvarchar(10) NOT NULL,
 [RecordedBy] nvarchar(100) NOT NULL,
 [RecordedAt] datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
 PRIMARY KEY(ActivityID)
);
GO
CREATE   VIEW dbo.MicroEquipmentSignerEligibility AS
SELECT Username,Role,IsActive,IsLocked,LockedUntil,MustChangePassword,CanReviewResults,CanApproveResults,
 CONVERT(bit,CASE WHEN PasswordHashNew IS NOT NULL AND PasswordSalt IS NOT NULL
 AND PasswordChangedAt IS NOT NULL THEN 1 ELSE 0 END) AS CredentialInitialized
FROM dbo.Users;
GO
CREATE   PROCEDURE dbo.MicroEquipment_AssertTrustedBoundary
WITH EXECUTE AS OWNER AS
BEGIN
 SET NOCOUNT ON;
 IF NOT EXISTS(SELECT 1 FROM sys.sql_modules WHERE object_id=OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage')
  AND definition LIKE N'%56750%' AND definition LIKE N'%ORIGINAL_LOGIN()%')
  THROW 56762,'Trusted signing procedure hardening is absent.',1;
 IF EXISTS(SELECT 1 FROM (VALUES
  (N'TRG_MicroEquipmentActivities_ImmutableSigned'),(N'TRG_MicroEquipmentActivityLinks_SignedGuard'),
  (N'TRG_MicroEquipmentActivitySignatures_TrustedOrigin'),(N'TRG_MicroEquipmentActivities_SignedAudit')) required(Name)
  LEFT JOIN sys.triggers t ON t.object_id=OBJECT_ID(N'dbo.'+required.Name,N'TR')
  WHERE t.object_id IS NULL OR t.is_disabled=1)
  THROW 56763,'Trusted signing integrity guards are absent or disabled.',1;
END;
GO
CREATE   PROCEDURE dbo.MicroEquipment_GetSignedContent
 @Activity bigint,@Stage nvarchar(30),@Snapshot nvarchar(max) OUTPUT
WITH EXECUTE AS OWNER AS
BEGIN
 SET NOCOUNT ON;
 SET @Snapshot=(SELECT ActivityID,EquipmentID,ActivityType,@Stage AS ActivityStatus,PerformedBy,
         ActualStartAt,ActualEndAt,MethodReference,ActivityReason,
         EquipmentCodeSnapshot,EquipmentNameSnapshot,CalibrationStatusSnapshot,QualificationStatusSnapshot,
         JSON_QUERY((SELECT EvidenceType,ReferenceValue,ObservedValue,MaximumAbsoluteError,
                            AbsoluteError,NormalMinimum,NormalMaximum,CleaningAgentLot,Outcome,RecordedBy,RecordedAt
                     FROM dbo.MicroEquipmentStructuredEvidence WITH(HOLDLOCK)
                     WHERE ActivityID=@Activity FOR JSON PATH,INCLUDE_NULL_VALUES)) AS StructuredEvidence,
         JSON_QUERY((SELECT PointNo,ReferenceValue,ObservedValue,MaxAbsoluteError,NormalMinimum,NormalMaximum,AbsoluteError,Outcome
                     FROM dbo.MicroEquipmentCalibrationPoints WITH(HOLDLOCK) WHERE ActivityID=@Activity
                     ORDER BY PointNo FOR JSON PATH,INCLUDE_NULL_VALUES)) AS CalibrationPoints
  FROM dbo.MicroEquipmentActivities WHERE ActivityID=@Activity
  FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
END;
GO
CREATE   PROCEDURE dbo.MicroEquipment_SignActivityStage
 @Activity bigint,@Version binary(8),@Actor nvarchar(100),@Stage nvarchar(30),@Reason nvarchar(500)
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON;
 SET XACT_ABORT ON;
 DECLARE @OwnTransaction bit=CASE WHEN @@TRANCOUNT=0 THEN 1 ELSE 0 END;
 IF @OwnTransaction=1 BEGIN TRANSACTION;
 BEGIN TRY
 IF NOT (EXISTS (SELECT 1 FROM sys.database_principals p
 WHERE p.name=N'PharmaLIMS_TrustedEquipmentSigner' AND p.type=N'U'
 AND p.sid=SUSER_SID(ORIGINAL_LOGIN()))
 AND ISNULL(IS_SRVROLEMEMBER(N'sysadmin',ORIGINAL_LOGIN()),1)=0)
  THROW 56750,'Equipment signing requires the dedicated authenticated Windows service origin.',1;
 IF @Actor IS NULL OR LEN(LTRIM(RTRIM(@Actor)))=0 OR @Version IS NULL
    OR @Version=0x0000000000000000 OR @Reason LIKE N'%'+NCHAR(10)+N'%'
    OR @Reason LIKE N'%'+NCHAR(13)+N'%'
  THROW 56751,'Invalid signer, version or signature reason.',1;
 IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentSignerEligibility WITH(UPDLOCK,HOLDLOCK)
   WHERE Username=@Actor AND IsActive=1 AND ISNULL(MustChangePassword,0)=0
   AND CredentialInitialized=1
   AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()))
   AND ((@Stage=N'Reviewed' AND (CanReviewResults=1 OR Role IN(N'Admin',N'Administrator')))
     OR (@Stage=N'Approved' AND (CanApproveResults=1 OR Role IN(N'Admin',N'Administrator')))))
  THROW 56752,'Signer account is not currently eligible for this stage.',1;

 IF @Stage NOT IN(N'Reviewed',N'Approved') OR NULLIF(LTRIM(RTRIM(@Reason)),N'') IS NULL THROW 56515,'Invalid signed stage or reason.',1;

DECLARE @Prior nvarchar(30)=CASE WHEN @Stage=N'Reviewed' THEN N'Submitted' ELSE N'Reviewed' END;
IF NOT EXISTS(
 SELECT 1 FROM dbo.MicroEquipmentActivities WITH(XLOCK,HOLDLOCK)
 WHERE ActivityID=@Activity AND ActivityStatus=@Prior AND VersionToken=@Version
 AND PerformedBy<>@Actor AND ActualStartAt IS NOT NULL AND ActualEndAt IS NOT NULL
 AND NULLIF(LTRIM(RTRIM(MethodReference)),N'') IS NOT NULL
 AND (ActivityType<>N'Calibration' OR
      (EXISTS (SELECT 1 FROM dbo.MicroEquipmentStructuredEvidence se
               WHERE se.ActivityID=@Activity AND se.EvidenceType=N'Calibration')
       AND EXISTS (SELECT 1 FROM dbo.MicroEquipmentCalibrationPoints cp WHERE cp.ActivityID=@Activity)
       AND EXISTS (SELECT 1 FROM dbo.MicroEquipmentCalibrationPoints p
                   JOIN dbo.MicroEquipmentStructuredEvidence s ON s.ActivityID=p.ActivityID
                   WHERE p.ActivityID=@Activity AND p.PointNo=1
                   AND p.ObservedValue=s.ObservedValue
                   AND ((p.NormalMinimum=s.NormalMinimum AND p.NormalMaximum=s.NormalMaximum
                         AND p.ReferenceValue IS NULL AND s.ReferenceValue IS NULL)
                     OR (p.ReferenceValue=s.ReferenceValue AND p.MaxAbsoluteError=s.MaximumAbsoluteError
                         AND p.NormalMinimum IS NULL AND s.NormalMinimum IS NULL)))
       AND NOT EXISTS (SELECT 1 FROM dbo.MicroEquipmentCalibrationPoints cp
                       WHERE cp.ActivityID=@Activity AND (cp.Outcome IS NULL OR
                        (cp.NormalMinimum IS NULL AND EXISTS
                         (SELECT 1 FROM dbo.MicroEquipmentStructuredEvidence se WHERE se.ActivityID=@Activity AND se.NormalMinimum IS NOT NULL)) OR
                        (cp.NormalMinimum IS NOT NULL AND EXISTS
                         (SELECT 1 FROM dbo.MicroEquipmentStructuredEvidence se WHERE se.ActivityID=@Activity AND se.NormalMinimum IS NULL)))))
 AND (@Stage<>N'Approved' OR ActivityType NOT IN (N'Calibration',N'Cleaning',N'Sanitization')
      OR (EXISTS
          (SELECT 1 FROM dbo.MicroEquipmentStructuredEvidence se
           WHERE se.ActivityID=@Activity AND se.EvidenceType=ActivityType AND se.Outcome=N'PASS')
          AND (ActivityType<>N'Calibration' OR NOT EXISTS
              (SELECT 1 FROM dbo.MicroEquipmentCalibrationPoints cp
               WHERE cp.ActivityID=@Activity AND cp.Outcome<>N'PASS'))))
 AND (@Stage<>N'Approved' OR EXISTS(
 SELECT 1 FROM dbo.MicroEquipmentActivitySignatures
 WHERE ActivityID=@Activity AND SignatureMeaning=N'Reviewed' AND SignedBy<>@Actor))
)) THROW 56513,'Activity stage is stale or segregation/record requirements are unmet.',1;

IF @Stage=N'Approved' EXEC dbo.MicroEquipment_ValidateActivitySignatures @Activity;
DECLARE @Snapshot nvarchar(max);
EXEC dbo.MicroEquipment_GetSignedContent @Activity,@Stage,@Snapshot OUTPUT;
IF @Snapshot IS NULL THROW 56514,'Signed equipment record is missing.',1;
INSERT dbo.MicroEquipmentActivitySignatures
 (ActivityID,SignatureMeaning,SignatureReason,ContentHash,SignedBy)
 VALUES(@Activity,@Stage,@Reason,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Snapshot)),@Actor);
UPDATE dbo.MicroEquipmentActivities SET ActivityStatus=@Stage WHERE ActivityID=@Activity;
IF @OwnTransaction=1 COMMIT TRANSACTION;
 END TRY
 BEGIN CATCH
  IF @OwnTransaction=1 AND @@TRANCOUNT>0 ROLLBACK TRANSACTION;
  THROW;
 END CATCH;
END;
GO
CREATE   PROCEDURE dbo.MicroEquipment_ValidateActivitySignatures @Activity bigint
WITH EXECUTE AS OWNER AS
BEGIN
 SET NOCOUNT ON;
 DECLARE @Status nvarchar(30),@Snapshot nvarchar(max),@Review varbinary(32),@Approval varbinary(32);
 SELECT @Status=ActivityStatus FROM dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK) WHERE ActivityID=@Activity;
 IF @Status IS NULL THROW 56753,'Equipment activity is missing.',1;
 IF @Status NOT IN(N'Reviewed',N'Approved')
 BEGIN
  IF EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivitySignatures WHERE ActivityID=@Activity)
   THROW 56754,'Signed activity has an inconsistent workflow status.',1;
  RETURN;
 END;
 EXEC dbo.MicroEquipment_GetSignedContent @Activity,N'Reviewed',@Snapshot OUTPUT;
 SET @Review=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Snapshot));
 IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivitySignatures
  WHERE ActivityID=@Activity AND SignatureMeaning=N'Reviewed' AND ContentHash=@Review)
  OR EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivitySignatures
  WHERE ActivityID=@Activity AND SignatureMeaning=N'Reviewed' AND ContentHash<>@Review)
  THROW 56755,'Review signature does not match the recorded equipment content.',1;
 IF @Status=N'Approved'
 BEGIN
  EXEC dbo.MicroEquipment_GetSignedContent @Activity,N'Approved',@Snapshot OUTPUT;
  SET @Approval=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Snapshot));
  IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivitySignatures
   WHERE ActivityID=@Activity AND SignatureMeaning=N'Approved' AND ContentHash=@Approval)
   OR EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivitySignatures
   WHERE ActivityID=@Activity AND SignatureMeaning=N'Approved' AND ContentHash<>@Approval)
   THROW 56756,'Approval signature does not match the recorded equipment content.',1;
 END;
END;
GO
CREATE   TRIGGER dbo.TRG_MicroEquipmentActivities_ImmutableSigned
ON dbo.MicroEquipmentActivities AFTER INSERT, UPDATE, DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.ActivityID=i.ActivityID
  WHERE d.ActivityID IS NULL AND i.ActivityStatus IN(N'Reviewed',N'Approved'))
  THROW 56757,'New equipment activities cannot start in a signed state.',1;
 IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.ActivityID=d.ActivityID
  WHERE (d.ActivityStatus IN(N'Reviewed',N'Approved') OR EXISTS
   (SELECT 1 FROM dbo.MicroEquipmentActivitySignatures s WHERE s.ActivityID=d.ActivityID))
  AND (i.ActivityID IS NULL OR
   CONVERT(varbinary(max),(SELECT d.ActivityID,d.EquipmentID,d.ActivityType,d.PerformedBy,d.ActualStartAt,d.ActualEndAt,d.MethodReference,d.ActivityReason,d.EquipmentCodeSnapshot,d.EquipmentNameSnapshot,d.CalibrationStatusSnapshot,d.QualificationStatusSnapshot,d.CreatedAt FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES))<>CONVERT(varbinary(max),(SELECT i.ActivityID,i.EquipmentID,i.ActivityType,i.PerformedBy,i.ActualStartAt,i.ActualEndAt,i.MethodReference,i.ActivityReason,i.EquipmentCodeSnapshot,i.EquipmentNameSnapshot,i.CalibrationStatusSnapshot,i.QualificationStatusSnapshot,i.CreatedAt FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES))
   OR (d.ActivityStatus=N'Approved' AND i.ActivityStatus<>N'Approved')
   OR (d.ActivityStatus=N'Reviewed' AND i.ActivityStatus NOT IN(N'Reviewed',N'Approved'))))
  THROW 56758,'Signed equipment content is immutable; use a controlled correction retaining the original.',1;
 IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON d.ActivityID=i.ActivityID
  WHERE i.ActivityStatus<>d.ActivityStatus AND i.ActivityStatus IN(N'Reviewed',N'Approved'))
 BEGIN
  IF NOT (EXISTS (SELECT 1 FROM sys.database_principals p
 WHERE p.name=N'PharmaLIMS_TrustedEquipmentSigner' AND p.type=N'U'
 AND p.sid=SUSER_SID(ORIGINAL_LOGIN()))
 AND ISNULL(IS_SRVROLEMEMBER(N'sysadmin',ORIGINAL_LOGIN()),1)=0) THROW 56759,'Only the trusted signing origin may advance signed activity stages.',1;
  DECLARE @Activity bigint;
  DECLARE stages CURSOR LOCAL FAST_FORWARD FOR
   SELECT i.ActivityID FROM inserted i JOIN deleted d ON d.ActivityID=i.ActivityID
   WHERE i.ActivityStatus<>d.ActivityStatus AND i.ActivityStatus IN(N'Reviewed',N'Approved');
  OPEN stages; FETCH NEXT FROM stages INTO @Activity;
  WHILE @@FETCH_STATUS=0
  BEGIN
   EXEC dbo.MicroEquipment_ValidateActivitySignatures @Activity;
   FETCH NEXT FROM stages INTO @Activity;
  END;
  CLOSE stages; DEALLOCATE stages;
 END;
END;
GO
CREATE   TRIGGER dbo.TRG_MicroEquipmentActivities_MicroScope ON dbo.MicroEquipmentActivities AFTER INSERT, UPDATE AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM inserted i LEFT JOIN dbo.LabEquipment e ON e.EquipmentID=i.EquipmentID WHERE e.EquipmentID IS NULL OR LEN(e.EquipmentCode)<>10 OR e.EquipmentCode NOT LIKE N'MIC-EQ-[0-9][0-9][0-9]' OR TRY_CONVERT(int,SUBSTRING(e.EquipmentCode,8,3)) IS NULL OR TRY_CONVERT(int,SUBSTRING(e.EquipmentCode,8,3))<1 OR UPPER(LTRIM(RTRIM(COALESCE(e.Department,N''))))<>N'MICROBIOLOGY' OR e.IsActive<>1) THROW 56504, 'Only registered microbiology equipment may be recorded.', 1; END;
GO
CREATE   TRIGGER dbo.TRG_MicroEquipmentActivities_SignedAudit
      ON dbo.MicroEquipmentActivities AFTER UPDATE AS
      BEGIN
        SET NOCOUNT ON;
        IF NOT UPDATE(ActivityStatus) RETURN;
        INSERT dbo.MicroEquipmentActivityAudit
          (ActivityID,ActionType,NewStateJson,ActionReason,ChangedBy)
        SELECT i.ActivityID,UPPER(i.ActivityStatus),
          (SELECT i.ActivityID,i.EquipmentID,i.ActivityType,i.ActivityStatus,
             i.PerformedBy,i.ActualStartAt,i.ActualEndAt,i.EquipmentCodeSnapshot,
             i.CalibrationStatusSnapshot,i.QualificationStatusSnapshot,
             i.ActivityReason,i.MethodReference
             FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES),
          sig.SignatureReason,sig.SignedBy
        FROM inserted i
        JOIN deleted d ON d.ActivityID=i.ActivityID
        CROSS APPLY(
          SELECT TOP(1) s.SignatureReason,s.SignedBy
          FROM dbo.MicroEquipmentActivitySignatures s
          WHERE s.ActivityID=i.ActivityID
            AND s.SignatureMeaning=i.ActivityStatus
          ORDER BY s.SignatureID DESC
        ) sig
        WHERE i.ActivityStatus<>d.ActivityStatus
          AND i.ActivityStatus IN(N'Reviewed',N'Approved');
      END;
GO
CREATE   TRIGGER dbo.TRG_MicroEquipmentActivities_SignedStatus ON dbo.MicroEquipmentActivities AFTER UPDATE AS
BEGIN
 SET NOCOUNT ON;
 IF NOT UPDATE(ActivityStatus) RETURN;
 IF EXISTS(
  SELECT 1 FROM inserted i JOIN deleted d ON i.ActivityID=d.ActivityID
  WHERE i.ActivityStatus<>d.ActivityStatus
    AND i.ActivityStatus IN(N'Reviewed',N'Approved')
    AND (NOT ((d.ActivityStatus=N'Submitted' AND i.ActivityStatus=N'Reviewed') OR
              (d.ActivityStatus=N'Reviewed' AND i.ActivityStatus=N'Approved'))
       OR NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivitySignatures s
             WHERE s.ActivityID=i.ActivityID AND s.SignatureMeaning=i.ActivityStatus
               AND NULLIF(LTRIM(RTRIM(s.SignedBy)),N'') IS NOT NULL
               AND NULLIF(LTRIM(RTRIM(s.SignatureReason)),N'') IS NOT NULL
               AND s.ContentHash IS NOT NULL)))
 THROW 56521,'Signed micro equipment status transition requires prior controlled signature.',1;
END;
GO
CREATE   TRIGGER dbo.TRG_MicroEquipmentActivityAudit_AppendOnly ON dbo.MicroEquipmentActivityAudit AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 56502, 'Micro equipment audit is append-only.', 1; END;
GO
CREATE   TRIGGER dbo.TRG_MicroEquipmentActivityLinks_SignedGuard
ON dbo.MicroEquipmentActivityLinks AFTER INSERT, UPDATE, DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivities a WITH(UPDLOCK,HOLDLOCK)
  JOIN (SELECT ActivityID FROM deleted UNION SELECT ActivityID FROM inserted) touched ON touched.ActivityID=a.ActivityID
  WHERE a.ActivityStatus IN(N'Reviewed',N'Approved') OR EXISTS
   (SELECT 1 FROM dbo.MicroEquipmentActivitySignatures s WHERE s.ActivityID=a.ActivityID))
  THROW 56760,'Links of a signed equipment activity cannot change.',1;
END;
GO
CREATE   TRIGGER dbo.TRG_MicroEquipmentActivitySignatures_AppendOnly ON dbo.MicroEquipmentActivitySignatures AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 56503, 'Micro equipment signatures are append-only.', 1; END;
GO
CREATE   TRIGGER dbo.TRG_MicroEquipmentActivitySignatures_TrustedOrigin
ON dbo.MicroEquipmentActivitySignatures AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF NOT (EXISTS (SELECT 1 FROM sys.database_principals p
 WHERE p.name=N'PharmaLIMS_TrustedEquipmentSigner' AND p.type=N'U'
 AND p.sid=SUSER_SID(ORIGINAL_LOGIN()))
 AND ISNULL(IS_SRVROLEMEMBER(N'sysadmin',ORIGINAL_LOGIN()),1)=0) THROW 56609,'Direct or untrusted equipment signature insertion is forbidden.',1;
END;
GO
CREATE   TRIGGER dbo.TRG_MicroEquipmentCalibrationPoints_AppendOnly ON dbo.MicroEquipmentCalibrationPoints AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 56604, 'Calibration point evidence is append-only.',1; END;
GO
CREATE   TRIGGER dbo.TRG_MicroEquipmentCalibrationPoints_Identity ON dbo.MicroEquipmentCalibrationPoints AFTER INSERT AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.MicroEquipmentActivities a ON a.ActivityID=i.ActivityID WHERE a.ActivityType<>N'Calibration' OR a.ActivityStatus<>N'Submitted') THROW 56605, 'Calibration points require a submitted calibration activity.',1; END;
GO
CREATE   TRIGGER dbo.TRG_MicroEquipmentLifecycleDetails_Immutable ON dbo.MicroEquipmentLifecycleDetails AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; THROW 56521,'Lifecycle evidence is append-only; use a controlled correction',1; END;
GO
CREATE   TRIGGER dbo.TRG_MicroEquipmentLifecycleDetails_SignedGuard
ON dbo.MicroEquipmentLifecycleDetails AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.MicroEquipmentActivities a WITH(UPDLOCK,HOLDLOCK) ON a.ActivityID=i.ActivityID
  WHERE a.ActivityStatus IN(N'Reviewed',N'Approved') OR EXISTS
   (SELECT 1 FROM dbo.MicroEquipmentActivitySignatures s WHERE s.ActivityID=a.ActivityID))
  THROW 56761,'Lifecycle evidence cannot be appended to a signed equipment activity.',1;
END;
GO
CREATE   TRIGGER dbo.TRG_MicroEquipmentStructuredEvidence_AppendOnly ON dbo.MicroEquipmentStructuredEvidence AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 56602, 'Signed equipment execution evidence cannot be updated or deleted.',1; END;
GO
CREATE   TRIGGER dbo.TRG_MicroEquipmentStructuredEvidence_Identity ON dbo.MicroEquipmentStructuredEvidence AFTER INSERT AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM inserted i LEFT JOIN dbo.MicroEquipmentActivities a ON a.ActivityID=i.ActivityID WHERE a.ActivityID IS NULL OR a.ActivityType<>i.EvidenceType OR a.ActivityStatus<>N'Submitted' OR a.PerformedBy<>i.RecordedBy) THROW 56603, 'Equipment evidence must match submitted activity, type and performer.',1; END;
GO
CREATE   PROCEDURE dbo.EQ001_WindowsUat_AssertActor
 @Username nvarchar(256)=NULL, @UserID int=NULL,
 @Actor nvarchar(256) OUTPUT, @ActorID int OUTPUT
WITH EXECUTE AS OWNER AS
BEGIN
 SET NOCOUNT ON;
 SET @Actor=ORIGINAL_LOGIN(); SET @ActorID=NULL;
 IF DB_NAME()<>N'PharmaLIMS_UAT_20261001'
  AND DB_NAME() NOT LIKE N'PharmaLIMS_Integration[_]%'
  AND DB_NAME() NOT LIKE N'PharmaLIMS_UAT_CLEAN[_]%'
  THROW 56873,'Windows signing authentication is restricted to isolated UAT.',1;
 DECLARE @Principal sysname;
 SELECT @Principal=p.name FROM sys.database_principals p
 WHERE p.type=N'U' AND p.sid=SUSER_SID(ORIGINAL_LOGIN());
 IF @Principal IS NULL OR ISNULL(IS_ROLEMEMBER(N'PharmaLIMS_EquipmentWindowsUat',@Principal),0)<>1
  OR ISNULL(IS_SRVROLEMEMBER(N'sysadmin',ORIGINAL_LOGIN()),1)<>0
  OR LEN(@Actor)>100 OR CHARINDEX(N'\',@Actor)=0
  THROW 56874,'An independently authenticated restricted Windows principal is required.',1;
 IF @Username IS NOT NULL AND @Username<>@Actor
  THROW 56875,'Cross-account authentication is forbidden.',1;
 IF (SELECT COUNT_BIG(*) FROM dbo.Users WHERE Username=@Actor)<>1
  THROW 56876,'An exact existing PharmaLIMS Windows identity is required.',1;
 SELECT @ActorID=UserID FROM dbo.Users WHERE Username=@Actor;
 IF @UserID IS NOT NULL AND @UserID<>@ActorID
  THROW 56875,'Cross-account authentication is forbidden.',1;
END;
GO
CREATE   PROCEDURE dbo.EQ001_WindowsUat_CommitSuccess
 @UserID int,@Username nvarchar(256),@ExpectedVersion binary(8),@IsLogin bit
WITH EXECUTE AS OWNER AS
BEGIN
 SET NOCOUNT ON;
 DECLARE @Actor nvarchar(256),@ActorID int;
 EXEC dbo.EQ001_WindowsUat_AssertActor @Username,@UserID,@Actor OUTPUT,@ActorID OUTPUT;
DECLARE @Committed TABLE (AuthenticationRowVersion binary(8) NOT NULL);
UPDATE dbo.Users
SET LastLogin=CASE WHEN @IsLogin=1 THEN SYSDATETIME() ELSE LastLogin END,
    FailedLoginAttempts=0, IsLocked=0, LockedUntil=NULL
OUTPUT inserted.AuthenticationRowVersion INTO @Committed
WHERE UserID=@ActorID AND Username=@Actor
 AND AuthenticationRowVersion=@ExpectedVersion AND ISNULL(IsActive,1)=1 AND MustChangePassword=0 AND PasswordChangedAt IS NOT NULL
 AND NULLIF(PasswordHashNew,N'') IS NOT NULL AND NULLIF(PasswordSalt,N'') IS NOT NULL
 AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()));
SELECT AuthenticationRowVersion FROM @Committed;
END;
GO
CREATE   PROCEDURE dbo.EQ001_WindowsUat_ReadOwnUser
 @Username nvarchar(256)=NULL, @UserID int=NULL
WITH EXECUTE AS OWNER AS
BEGIN
 SET NOCOUNT ON;
 DECLARE @Actor nvarchar(256),@ActorID int;
 EXEC dbo.EQ001_WindowsUat_AssertActor @Username,@UserID,@Actor OUTPUT,@ActorID OUTPUT;
SELECT UserID, Username, FullName, Role, Department, Section,
                    PasswordHash, PasswordHashNew, PasswordSalt, AuthenticationRowVersion,
                    IsActive, LastLogin, FailedLoginAttempts,
                    CONVERT(bit, CASE
                        WHEN ISNULL(IsLocked, 0) = 1
                         AND (LockedUntil IS NULL OR LockedUntil > SYSDATETIME()) THEN 1
                        ELSE 0
                    END) AS IsLocked,
                    LockedUntil,
                    MustChangePassword, PasswordChangedAt,
                    CanAccessWater, CanAccessEM, CanRegisterSamples, CanEnterResults,
                    CanReviewResults, CanApproveResults, CanIssueCOA, CanCancelCOA,
                    CanAccessReports, CanManageUsers, CanManageSettings FROM Users WHERE UserID = @ActorID AND Username = @Actor
 AND IsActive=1 AND MustChangePassword=0 AND PasswordChangedAt IS NOT NULL
 AND NULLIF(PasswordHashNew,N'') IS NOT NULL AND NULLIF(PasswordSalt,N'') IS NOT NULL
 AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()));
END;
GO
CREATE   PROCEDURE dbo.EQ001_WindowsUat_RecordFailure
 @VerifiedUserID int,@Username nvarchar(256),@VerifiedLegacyHash nvarchar(max),
 @VerifiedHash nvarchar(max),@VerifiedSalt nvarchar(max)
WITH EXECUTE AS OWNER AS
BEGIN
 SET NOCOUNT OFF;
 DECLARE @Actor nvarchar(256),@ActorID int;
 EXEC dbo.EQ001_WindowsUat_AssertActor @Username,@VerifiedUserID,@Actor OUTPUT,@ActorID OUTPUT;

UPDATE dbo.Users SET
    FailedLoginAttempts = CASE
        WHEN ISNULL(IsLocked,0)=1 AND LockedUntil<=SYSDATETIME() THEN 1
        ELSE ISNULL(FailedLoginAttempts,0)+1 END,
    IsLocked = CASE
        WHEN ISNULL(IsLocked,0)=1 AND LockedUntil<=SYSDATETIME() THEN 0
        WHEN ISNULL(FailedLoginAttempts,0)+1>=5 THEN 1
        ELSE 0 END,
    LockedUntil = CASE
        WHEN ISNULL(IsLocked,0)=1 AND LockedUntil<=SYSDATETIME() THEN NULL
        WHEN ISNULL(FailedLoginAttempts,0)+1>=5 THEN DATEADD(MINUTE,15,SYSDATETIME())
        ELSE LockedUntil END
WHERE UserID=@ActorID AND Username=@Actor AND ISNULL(IsActive,1)=1 AND MustChangePassword=0 AND PasswordChangedAt IS NOT NULL
  AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()))
  AND CONVERT(varbinary(max),ISNULL(PasswordHash,N''))=CONVERT(varbinary(max),@VerifiedLegacyHash)
  AND CONVERT(varbinary(max),ISNULL(PasswordHashNew,N''))=CONVERT(varbinary(max),@VerifiedHash)
  AND CONVERT(varbinary(max),ISNULL(PasswordSalt,N''))=CONVERT(varbinary(max),@VerifiedSalt);
END;
GO
