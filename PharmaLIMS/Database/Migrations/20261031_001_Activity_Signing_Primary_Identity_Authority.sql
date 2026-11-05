-- v309 hotfix: split-database activity signing uses the primary EQ001 user account
-- as the authoritative credential/account-state source. The isolated activity DB
-- remains authoritative for activity state, local UAT authorization, SoD and signed evidence.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() NOT LIKE N'PharmaLIMS_UAT[_]%'
   AND DB_NAME() NOT LIKE N'PharmaLIMS_Integration[_]%'
   AND DB_NAME() NOT LIKE N'EQ001_UAT[_]%'
   AND DB_NAME() NOT LIKE N'PharmaLIMS_EQ001_UAT[_]%'
 THROW 56890,'Primary-identity activity signing correction is UAT only.',1;

IF OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage',N'P') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentSignerEligibility',N'V') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentActivities',N'U') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentActivitySignatures',N'U') IS NULL
 THROW 56891,'Required trusted activity signing schema is missing.',1;

IF USER_ID(N'PharmaLIMS_TrustedEquipmentSigner') IS NULL
 THROW 56892,'Trusted equipment signer database principal is missing.',1;

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
 -- Credential, lock and temporary-password state are authoritatively verified
 -- against the primary EQ001 Users row by the trusted Windows host before this
 -- procedure is reached. The isolated activity DB retains a second, local
 -- authorization gate for active status and stage permission only.
 IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentSignerEligibility WITH(UPDLOCK,HOLDLOCK)
   WHERE Username=@Actor AND IsActive=1
   AND ((@Stage=N''Reviewed'' AND (CanReviewResults=1 OR Role IN(N''Admin'',N''Administrator'')))
     OR (@Stage=N''Approved'' AND (CanApproveResults=1 OR Role IN(N''Admin'',N''Administrator'')))))
  THROW 56752,''Signer account is not locally authorized for this stage.'',1;

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

-- Re-assert that the procedure still contains the protected service-origin gate
-- and that the signed-content integrity triggers remain enabled.
EXEC dbo.MicroEquipment_AssertTrustedBoundary;
