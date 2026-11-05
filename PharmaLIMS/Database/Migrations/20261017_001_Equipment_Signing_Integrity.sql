-- Additive F01/F02 hardening. DOES NOT cut over shared-role EXECUTE permissions.
-- Preserve all historical activity/signature/audit rows and historical canonical hashes.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage',N'P') IS NULL
 THROW 56749,'Apply the controlled equipment signing prerequisite first.',1;
EXEC(N'CREATE OR ALTER VIEW dbo.MicroEquipmentSignerEligibility AS
SELECT Username,Role,IsActive,IsLocked,LockedUntil,MustChangePassword,CanReviewResults,CanApproveResults,
 CONVERT(bit,CASE WHEN PasswordHashNew IS NOT NULL AND PasswordSalt IS NOT NULL
 AND PasswordChangedAt IS NOT NULL THEN 1 ELSE 0 END) AS CredentialInitialized
FROM dbo.Users;');

EXEC(N'CREATE OR ALTER PROCEDURE dbo.MicroEquipment_GetSignedContent
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
END;');

EXEC(N'CREATE OR ALTER PROCEDURE dbo.MicroEquipment_ValidateActivitySignatures @Activity bigint
WITH EXECUTE AS OWNER AS
BEGIN
 SET NOCOUNT ON;
 DECLARE @Status nvarchar(30),@Snapshot nvarchar(max),@Review varbinary(32),@Approval varbinary(32);
 SELECT @Status=ActivityStatus FROM dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK) WHERE ActivityID=@Activity;
 IF @Status IS NULL THROW 56753,''Equipment activity is missing.'',1;
 IF @Status NOT IN(N''Reviewed'',N''Approved'')
 BEGIN
  IF EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivitySignatures WHERE ActivityID=@Activity)
   THROW 56754,''Signed activity has an inconsistent workflow status.'',1;
  RETURN;
 END;
 EXEC dbo.MicroEquipment_GetSignedContent @Activity,N''Reviewed'',@Snapshot OUTPUT;
 SET @Review=HASHBYTES(''SHA2_256'',CONVERT(varbinary(max),@Snapshot));
 IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivitySignatures
  WHERE ActivityID=@Activity AND SignatureMeaning=N''Reviewed'' AND ContentHash=@Review)
  OR EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivitySignatures
  WHERE ActivityID=@Activity AND SignatureMeaning=N''Reviewed'' AND ContentHash<>@Review)
  THROW 56755,''Review signature does not match the recorded equipment content.'',1;
 IF @Status=N''Approved''
 BEGIN
  EXEC dbo.MicroEquipment_GetSignedContent @Activity,N''Approved'',@Snapshot OUTPUT;
  SET @Approval=HASHBYTES(''SHA2_256'',CONVERT(varbinary(max),@Snapshot));
  IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivitySignatures
   WHERE ActivityID=@Activity AND SignatureMeaning=N''Approved'' AND ContentHash=@Approval)
   OR EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivitySignatures
   WHERE ActivityID=@Activity AND SignatureMeaning=N''Approved'' AND ContentHash<>@Approval)
   THROW 56756,''Approval signature does not match the recorded equipment content.'',1;
 END;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE dbo.MicroEquipment_SignActivityStage
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
 WHERE p.name=N''PharmaLIMS_TrustedEquipmentSigner'' AND p.type=N''U''
 AND p.sid=SUSER_SID(ORIGINAL_LOGIN()))
 AND ISNULL(IS_SRVROLEMEMBER(N''sysadmin'',ORIGINAL_LOGIN()),1)=0)
  THROW 56750,''Equipment signing requires the dedicated authenticated Windows service origin.'',1;
 IF @Actor IS NULL OR LEN(LTRIM(RTRIM(@Actor)))=0 OR @Version IS NULL
    OR @Version=0x0000000000000000 OR @Reason LIKE N''%''+NCHAR(10)+N''%''
    OR @Reason LIKE N''%''+NCHAR(13)+N''%''
  THROW 56751,''Invalid signer, version or signature reason.'',1;
 IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentSignerEligibility WITH(UPDLOCK,HOLDLOCK)
   WHERE Username=@Actor AND IsActive=1 AND ISNULL(MustChangePassword,0)=0
   AND CredentialInitialized=1
   AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()))
   AND ((@Stage=N''Reviewed'' AND (CanReviewResults=1 OR Role IN(N''Admin'',N''Administrator'')))
     OR (@Stage=N''Approved'' AND (CanApproveResults=1 OR Role IN(N''Admin'',N''Administrator'')))))
  THROW 56752,''Signer account is not currently eligible for this stage.'',1;

 IF @Stage NOT IN(N''Reviewed'',N''Approved'') OR NULLIF(LTRIM(RTRIM(@Reason)),N'''') IS NULL THROW 56515,''Invalid signed stage or reason.'',1;

DECLARE @Prior nvarchar(30)=CASE WHEN @Stage=N''Reviewed'' THEN N''Submitted'' ELSE N''Reviewed'' END;
IF NOT EXISTS(
 SELECT 1 FROM dbo.MicroEquipmentActivities WITH(XLOCK,HOLDLOCK)
 WHERE ActivityID=@Activity AND ActivityStatus=@Prior AND VersionToken=@Version
 AND PerformedBy<>@Actor AND ActualStartAt IS NOT NULL AND ActualEndAt IS NOT NULL
 AND NULLIF(LTRIM(RTRIM(MethodReference)),N'''') IS NOT NULL
 AND (ActivityType<>N''Calibration'' OR
      (EXISTS (SELECT 1 FROM dbo.MicroEquipmentStructuredEvidence se
               WHERE se.ActivityID=@Activity AND se.EvidenceType=N''Calibration'')
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
 AND (@Stage<>N''Approved'' OR ActivityType NOT IN (N''Calibration'',N''Cleaning'',N''Sanitization'')
      OR (EXISTS
          (SELECT 1 FROM dbo.MicroEquipmentStructuredEvidence se
           WHERE se.ActivityID=@Activity AND se.EvidenceType=ActivityType AND se.Outcome=N''PASS'')
          AND (ActivityType<>N''Calibration'' OR NOT EXISTS
              (SELECT 1 FROM dbo.MicroEquipmentCalibrationPoints cp
               WHERE cp.ActivityID=@Activity AND cp.Outcome<>N''PASS''))))
 AND (@Stage<>N''Approved'' OR EXISTS(
 SELECT 1 FROM dbo.MicroEquipmentActivitySignatures
 WHERE ActivityID=@Activity AND SignatureMeaning=N''Reviewed'' AND SignedBy<>@Actor))
)) THROW 56513,''Activity stage is stale or segregation/record requirements are unmet.'',1;

IF @Stage=N''Approved'' EXEC dbo.MicroEquipment_ValidateActivitySignatures @Activity;
DECLARE @Snapshot nvarchar(max);
EXEC dbo.MicroEquipment_GetSignedContent @Activity,@Stage,@Snapshot OUTPUT;
IF @Snapshot IS NULL THROW 56514,''Signed equipment record is missing.'',1;
INSERT dbo.MicroEquipmentActivitySignatures
 (ActivityID,SignatureMeaning,SignatureReason,ContentHash,SignedBy)
 VALUES(@Activity,@Stage,@Reason,HASHBYTES(''SHA2_256'',CONVERT(varbinary(max),@Snapshot)),@Actor);
UPDATE dbo.MicroEquipmentActivities SET ActivityStatus=@Stage WHERE ActivityID=@Activity;
IF @OwnTransaction=1 COMMIT TRANSACTION;
 END TRY
 BEGIN CATCH
  IF @OwnTransaction=1 AND @@TRANCOUNT>0 ROLLBACK TRANSACTION;
  THROW;
 END CATCH;
END;');

EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_MicroEquipmentActivities_ImmutableSigned
ON dbo.MicroEquipmentActivities AFTER INSERT, UPDATE, DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.ActivityID=i.ActivityID
  WHERE d.ActivityID IS NULL AND i.ActivityStatus IN(N''Reviewed'',N''Approved''))
  THROW 56757,''New equipment activities cannot start in a signed state.'',1;
 IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.ActivityID=d.ActivityID
  WHERE (d.ActivityStatus IN(N''Reviewed'',N''Approved'') OR EXISTS
   (SELECT 1 FROM dbo.MicroEquipmentActivitySignatures s WHERE s.ActivityID=d.ActivityID))
  AND (i.ActivityID IS NULL OR
   CONVERT(varbinary(max),(SELECT d.ActivityID,d.EquipmentID,d.ActivityType,d.PerformedBy,d.ActualStartAt,d.ActualEndAt,d.MethodReference,d.ActivityReason,d.EquipmentCodeSnapshot,d.EquipmentNameSnapshot,d.CalibrationStatusSnapshot,d.QualificationStatusSnapshot,d.CreatedAt FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES))<>CONVERT(varbinary(max),(SELECT i.ActivityID,i.EquipmentID,i.ActivityType,i.PerformedBy,i.ActualStartAt,i.ActualEndAt,i.MethodReference,i.ActivityReason,i.EquipmentCodeSnapshot,i.EquipmentNameSnapshot,i.CalibrationStatusSnapshot,i.QualificationStatusSnapshot,i.CreatedAt FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES))
   OR (d.ActivityStatus=N''Approved'' AND i.ActivityStatus<>N''Approved'')
   OR (d.ActivityStatus=N''Reviewed'' AND i.ActivityStatus NOT IN(N''Reviewed'',N''Approved''))))
  THROW 56758,''Signed equipment content is immutable; use a controlled correction retaining the original.'',1;
 IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON d.ActivityID=i.ActivityID
  WHERE i.ActivityStatus<>d.ActivityStatus AND i.ActivityStatus IN(N''Reviewed'',N''Approved''))
 BEGIN
  IF NOT (EXISTS (SELECT 1 FROM sys.database_principals p
 WHERE p.name=N''PharmaLIMS_TrustedEquipmentSigner'' AND p.type=N''U''
 AND p.sid=SUSER_SID(ORIGINAL_LOGIN()))
 AND ISNULL(IS_SRVROLEMEMBER(N''sysadmin'',ORIGINAL_LOGIN()),1)=0) THROW 56759,''Only the trusted signing origin may advance signed activity stages.'',1;
  DECLARE @Activity bigint;
  DECLARE stages CURSOR LOCAL FAST_FORWARD FOR
   SELECT i.ActivityID FROM inserted i JOIN deleted d ON d.ActivityID=i.ActivityID
   WHERE i.ActivityStatus<>d.ActivityStatus AND i.ActivityStatus IN(N''Reviewed'',N''Approved'');
  OPEN stages; FETCH NEXT FROM stages INTO @Activity;
  WHILE @@FETCH_STATUS=0
  BEGIN
   EXEC dbo.MicroEquipment_ValidateActivitySignatures @Activity;
   FETCH NEXT FROM stages INTO @Activity;
  END;
  CLOSE stages; DEALLOCATE stages;
 END;
END;');

EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_MicroEquipmentActivityLinks_SignedGuard
ON dbo.MicroEquipmentActivityLinks AFTER INSERT, UPDATE, DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivities a WITH(UPDLOCK,HOLDLOCK)
  JOIN (SELECT ActivityID FROM deleted UNION SELECT ActivityID FROM inserted) touched ON touched.ActivityID=a.ActivityID
  WHERE a.ActivityStatus IN(N''Reviewed'',N''Approved'') OR EXISTS
   (SELECT 1 FROM dbo.MicroEquipmentActivitySignatures s WHERE s.ActivityID=a.ActivityID))
  THROW 56760,''Links of a signed equipment activity cannot change.'',1;
END;');

EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_MicroEquipmentLifecycleDetails_SignedGuard
ON dbo.MicroEquipmentLifecycleDetails AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.MicroEquipmentActivities a WITH(UPDLOCK,HOLDLOCK) ON a.ActivityID=i.ActivityID
  WHERE a.ActivityStatus IN(N''Reviewed'',N''Approved'') OR EXISTS
   (SELECT 1 FROM dbo.MicroEquipmentActivitySignatures s WHERE s.ActivityID=a.ActivityID))
  THROW 56761,''Lifecycle evidence cannot be appended to a signed equipment activity.'',1;
END;');

EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_MicroEquipmentActivitySignatures_TrustedOrigin
ON dbo.MicroEquipmentActivitySignatures AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF NOT (EXISTS (SELECT 1 FROM sys.database_principals p
 WHERE p.name=N''PharmaLIMS_TrustedEquipmentSigner'' AND p.type=N''U''
 AND p.sid=SUSER_SID(ORIGINAL_LOGIN()))
 AND ISNULL(IS_SRVROLEMEMBER(N''sysadmin'',ORIGINAL_LOGIN()),1)=0) THROW 56609,''Direct or untrusted equipment signature insertion is forbidden.'',1;
END;');

EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_MicroEquipmentActivities_SignedAudit
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
          AND i.ActivityStatus IN(N''Reviewed'',N''Approved'');
      END;');

EXEC(N'CREATE OR ALTER PROCEDURE dbo.MicroEquipment_AssertTrustedBoundary
WITH EXECUTE AS OWNER AS
BEGIN
 SET NOCOUNT ON;
 IF NOT EXISTS(SELECT 1 FROM sys.sql_modules WHERE object_id=OBJECT_ID(N''dbo.MicroEquipment_SignActivityStage'')
  AND definition LIKE N''%56750%'' AND definition LIKE N''%ORIGINAL_LOGIN()%'')
  THROW 56762,''Trusted signing procedure hardening is absent.'',1;
 IF EXISTS(SELECT 1 FROM (VALUES
  (N''TRG_MicroEquipmentActivities_ImmutableSigned''),(N''TRG_MicroEquipmentActivityLinks_SignedGuard''),
  (N''TRG_MicroEquipmentActivitySignatures_TrustedOrigin''),(N''TRG_MicroEquipmentActivities_SignedAudit'')) required(Name)
  LEFT JOIN sys.triggers t ON t.object_id=OBJECT_ID(N''dbo.''+required.Name,N''TR'')
  WHERE t.object_id IS NULL OR t.is_disabled=1)
  THROW 56763,''Trusted signing integrity guards are absent or disabled.'',1;
END;');

GRANT EXECUTE ON dbo.MicroEquipment_ValidateActivitySignatures TO PharmaLIMS_EquipmentApp;
-- This operation is read-only: callers cannot manufacture a signing origin or bypass the guards.
GRANT EXECUTE ON dbo.MicroEquipment_AssertTrustedBoundary TO PharmaLIMS_EquipmentApp;

-- Administrative UAT attestation only. No acceptance is inserted by migration.
IF OBJECT_ID(N'dbo.EQ001_UAT_Acceptance',N'U') IS NULL
 CREATE TABLE dbo.EQ001_UAT_Acceptance(
 AcceptanceID bigint IDENTITY PRIMARY KEY,DatabaseName sysname NOT NULL,
 TrustedBoundary nvarchar(40) NOT NULL,EvidenceReference nvarchar(200) NOT NULL,
 ReviewAndApprovalPassed bit NOT NULL,NegativeSecurityPassed bit NOT NULL,
 ContentIntegrityPassed bit NOT NULL,RegressionPassed bit NOT NULL,
 AcceptedBy sysname NOT NULL,AcceptedAtUtc datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME());
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_EQ001_UAT_Acceptance_Controlled
 ON dbo.EQ001_UAT_Acceptance AFTER INSERT,UPDATE,DELETE AS
 BEGIN
  SET NOCOUNT ON;
  IF EXISTS(SELECT 1 FROM deleted) THROW 56764,''UAT acceptance is append-only.'',1;
  IF ISNULL(IS_SRVROLEMEMBER(N''sysadmin'',ORIGINAL_LOGIN()),0)<>1
    OR EXISTS(SELECT 1 FROM inserted WHERE AcceptedBy<>ORIGINAL_LOGIN() OR DatabaseName<>DB_NAME()
       OR LEN(LTRIM(RTRIM(EvidenceReference)))=0)
   THROW 56765,''UAT acceptance requires the actual authorized administrator and evidence.'',1;
 END;');
