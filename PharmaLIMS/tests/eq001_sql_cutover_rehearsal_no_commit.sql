-- EQ-001 controlled SQL cutover + authorized signing + audit proof.
-- The outer transaction is always rolled back: UAT only.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME()<>N'PharmaLIMS_Integration_2c941d953f70'
 THROW 56640,'Rehearsal restricted to the exact UAT database.',1;
BEGIN TRANSACTION;
:r E:\PharmaLIMS-MicroEquipment-SINGLE-VERIFY-20261012\PharmaLIMS\tests\eq001_permission_cutover_after_uat.sql
DECLARE @appExec int=1;
EXECUTE AS USER=N'pharmalims_app';
SELECT @appExec=HAS_PERMS_BY_NAME(N'dbo.MicroEquipment_SignActivityStage',N'OBJECT',N'EXECUTE');
REVERT;
IF @appExec<>0 THROW 56641,'Shared WPF login can still execute signing after staged cutover.',1;
IF NOT EXISTS(SELECT 1 FROM dbo.Users WHERE Username=N'AMEEN\Elite' AND Role=N'UAT-Test'
   AND IsActive=1 AND CanReviewResults=1 AND CanApproveResults=0)
 THROW 56642,'Preexisting isolated UAT reviewer mapping is missing.',1;
INSERT dbo.LabEquipment(EquipmentCode,EquipmentName,EquipmentType,Department,GmpCritical,
 EquipmentStatus,QualificationStatus,CalibrationRequired,CalibrationStatus,IsActive,CreatedBy,CreatedAt)
VALUES(N'MIC-EQ-996',N'EQ001 UAT AUDIT PROBE',N'Incubator',N'Microbiology',1,
 N'Active',N'Qualified',0,N'Not Required',1,N'EQ001 controlled UAT',SYSUTCDATETIME());
DECLARE @equipment int=CONVERT(int,SCOPE_IDENTITY());
INSERT dbo.MicroEquipmentActivities(EquipmentID,ActivityType,ActivityStatus,PerformedBy,
 ActualStartAt,ActualEndAt,MethodReference,EquipmentCodeSnapshot,EquipmentNameSnapshot)
VALUES(@equipment,N'Cleaning',N'Submitted',N'UAT operator',SYSDATETIMEOFFSET(),
 SYSDATETIMEOFFSET(),N'EQ001 isolated audit test',N'MIC-EQ-996',N'EQ001 UAT AUDIT PROBE');
DECLARE @activity bigint=CONVERT(bigint,SCOPE_IDENTITY());
DECLARE @token binary(8);
SELECT @token=VersionToken FROM dbo.MicroEquipmentActivities WHERE ActivityID=@activity;
-- Audit-format test ONLY: disable the origin guard inside the disposable
-- transaction, then re-enable it before proving origin checks. This does NOT
-- prove a real Windows service sign-in; that requires independent end-to-end UAT.
DISABLE TRIGGER dbo.TRG_MicroEquipmentActivitySignatures_TrustedOrigin
 ON dbo.MicroEquipmentActivitySignatures;
EXECUTE AS LOGIN=N'AMEEN\PharmaLIMSSigner';
EXEC dbo.MicroEquipment_SignActivityStage
 @Activity=@activity,@Version=@token,@Actor=N'AMEEN\Elite',@Stage=N'Reviewed',
 @Reason=N'Isolated UAT audit preservation test';
REVERT;
IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivitySignatures
 WHERE ActivityID=@activity AND SignatureMeaning=N'Reviewed' AND ContentHash IS NOT NULL)
 THROW 56643,'Synthetic signature evidence missing.',1;
IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivityAudit
 WHERE ActivityID=@activity AND ActionType=N'REVIEWED' AND ChangedBy=N'AMEEN\Elite'
 AND ActionReason=N'Isolated UAT audit preservation test' AND ISJSON(NewStateJson)=1)
 THROW 56644,'Existing audit format was not preserved.',1;
PRINT 'EQ-001 PASS: Original audit format is preserved in SQL transaction (synthetic context only).';
ENABLE TRIGGER dbo.TRG_MicroEquipmentActivitySignatures_TrustedOrigin
 ON dbo.MicroEquipmentActivitySignatures;
-- A SQL administrator impersonating the signer is NOT the signer. ORIGINAL_LOGIN
-- must remain untrusted, so a second signature insertion MUST fail.
DECLARE @newToken binary(8);
SELECT @newToken=VersionToken FROM dbo.MicroEquipmentActivities WHERE ActivityID=@activity;
BEGIN TRY
 EXECUTE AS LOGIN=N'AMEEN\PharmaLIMSSigner';
 EXEC dbo.MicroEquipment_SignActivityStage
   @Activity=@activity,@Version=@newToken,@Actor=N'AMEEN\Other',
   @Stage=N'Approved',@Reason=N'Unauthorized actor simulation';
 REVERT;
END TRY
BEGIN CATCH
 DECLARE @deniedCode int=ERROR_NUMBER();
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 IF USER_NAME()=N'PharmaLIMS_TrustedEquipmentSigner' REVERT;
 IF @deniedCode NOT IN(56513,56609)
   THROW 56646,'Unexpected security test rejection: review diagnostics.',1;
 PRINT 'EQ-001 PASS: unauthorized SQL-origin or actor rejected; ALL UAT transaction changes rolled back.';
 RETURN;
END CATCH;
IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
THROW 56647,'Unauthorized SQL signature unexpectedly succeeded.',1;
