-- Controlled UAT-only correction of the supplied activity stage procedure.
-- A misplaced parenthesis let non-calibration activities bypass common approval
-- evidence and reviewer/approver separation rules. No historical rows are changed.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
BEGIN TRY
 IF DB_NAME() NOT LIKE N'EQ001[_]UAT[_]%' AND DB_NAME() NOT LIKE N'PharmaLIMS[_]EQ001[_]UAT[_]%'
  THROW 56766,'Stage policy correction is restricted to disposable EQ001 UAT.',1;
 DECLARE @Current nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage',N'P'));
 DECLARE @Digest varbinary(32)=HASHBYTES('SHA2_256',CONVERT(varbinary(max),REPLACE(REPLACE(@Current,NCHAR(13),N''),NCHAR(10),N'')));
 -- SQL Server can persist CREATE OR ALTER as CREATE with three spaces.
 -- Both corrected digests below cover the same exact reviewed body; no wildcard.
 IF @Digest IS NULL OR @Digest NOT IN(0x404F1EB0BA0AFA87915F26A6017907370C7313345593330305CD95A1E040F384,0x44C07F6ADAC697FE6C0655FDE20790808BB2F7140E937F48B2A5F0639E06CD3D,0xBA94572CF1A5650A0A296AFD94C54DF38D1A52338103716176547604CE4938B6)
  THROW 56766,'Activity procedure differs from the reviewed source. No procedure was changed.',1;
 IF @Digest=0x404F1EB0BA0AFA87915F26A6017907370C7313345593330305CD95A1E040F384
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
                         (SELECT 1 FROM dbo.MicroEquipmentStructuredEvidence se WHERE se.ActivityID=@Activity AND se.NormalMinimum IS NULL))))))
 AND (@Stage<>N''Approved'' OR ActivityType NOT IN (N''Calibration'',N''Cleaning'',N''Sanitization'')
      OR (EXISTS
          (SELECT 1 FROM dbo.MicroEquipmentStructuredEvidence se
           WHERE se.ActivityID=@Activity AND se.EvidenceType=ActivityType AND se.Outcome=N''PASS'')
          AND (ActivityType<>N''Calibration'' OR NOT EXISTS
              (SELECT 1 FROM dbo.MicroEquipmentCalibrationPoints cp
               WHERE cp.ActivityID=@Activity AND cp.Outcome<>N''PASS''))))
 AND (@Stage<>N''Approved'' OR EXISTS(
 SELECT 1 FROM dbo.MicroEquipmentActivitySignatures
 WHERE ActivityID=@Activity AND SignatureMeaning=N''Reviewed'' AND SignedBy<>@Actor)
 AND NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivitySignatures
 WHERE ActivityID=@Activity AND SignatureMeaning=N''Reviewed'' AND SignedBy=@Actor))
) THROW 56513,''Activity stage is stale or segregation/record requirements are unmet.'',1;

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
 COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
