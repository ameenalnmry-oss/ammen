-- Run ONLY as the real, non-admin AMEEN\LimsReview Windows logon.
-- The parent seeds a disposable canonical-schema database, never actual UAT.
SET NOCOUNT ON;
IF DB_NAME() NOT LIKE N'PharmaLIMS_Integration[_]%'
 THROW 56880,'Windows runtime probe refuses actual application databases.',1;
IF ORIGINAL_LOGIN()<>N'AMEEN\LimsReview' OR ISNULL(IS_SRVROLEMEMBER(N'sysadmin'),1)<>0
 THROW 56881,'Probe requires the actual non-admin reviewer Windows identity.',1;
IF ISNULL(IS_ROLEMEMBER(N'PharmaLIMS_EquipmentWindowsUat'),0)<>1
 THROW 56882,'Restricted Windows runtime membership is missing.',1;

DECLARE @Own TABLE
 (UserID int,Username nvarchar(100),FullName nvarchar(200),Role nvarchar(50),
 Department nvarchar(100),Section nvarchar(100),PasswordHash nvarchar(max),
 PasswordHashNew nvarchar(512),PasswordSalt nvarchar(256),AuthenticationRowVersion binary(8),
 IsActive bit,LastLogin datetime,FailedLoginAttempts int,IsLocked bit,LockedUntil datetime,
 MustChangePassword bit,PasswordChangedAt datetime2,
 CanAccessWater bit,CanAccessEM bit,CanRegisterSamples bit,CanEnterResults bit,
 CanReviewResults bit,CanApproveResults bit,CanIssueCOA bit,CanCancelCOA bit,
 CanAccessReports bit,CanManageUsers bit,CanManageSettings bit);
INSERT @Own EXEC dbo.EQ001_WindowsUat_ReadOwnUser @Username=N'AMEEN\LimsReview';
IF (SELECT COUNT(*) FROM @Own)<>1 OR NOT EXISTS(SELECT 1 FROM @Own WHERE Username=ORIGINAL_LOGIN())
 THROW 56883,'Own-account credential read failed.',1;
DECLARE @ID int,@Version binary(8),@Hash nvarchar(max),@Salt nvarchar(max),@Legacy nvarchar(max);
SELECT @ID=UserID,@Version=AuthenticationRowVersion,@Hash=PasswordHashNew,@Salt=PasswordSalt,@Legacy=PasswordHash FROM @Own;
IF @Hash<>N'FIXTURE_CURRENT_HASH' OR @Salt<>N'FIXTURE_CURRENT_SALT'
 THROW 56884,'Unexpected credential fixture; refusing this database.',1;

BEGIN TRY
 EXEC dbo.EQ001_WindowsUat_ReadOwnUser @Username=N'OTHER\LimsReview';
 THROW 56885,'Cross-account credential read was accepted.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>56875 THROW; END CATCH;
BEGIN TRY
 EXEC dbo.EQ001_WindowsUat_ReadOwnUser @UserID=2147483647;
 THROW 56898,'Cross-account ID read was accepted.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>56875 THROW; END CATCH;
BEGIN TRY
 DECLARE @Actor nvarchar(256),@ActorID int;
 EXEC dbo.EQ001_WindowsUat_AssertActor NULL,NULL,@Actor OUTPUT,@ActorID OUTPUT;
 THROW 56899,'Private owner helper was directly executable.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>229 THROW; END CATCH;
BEGIN TRY
 DECLARE @Secret nvarchar(max); SELECT @Secret=PasswordHashNew FROM dbo.Users WHERE Username=N'OtherFixture';
 THROW 56886,'Direct credential-column read was accepted.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER() NOT IN(229,230) THROW; END CATCH;
BEGIN TRY
 UPDATE dbo.Users SET PasswordHashNew=N'FORGED' WHERE UserID=@ID;
 THROW 56887,'Direct credential replacement was accepted.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER() NOT IN(229,230) THROW; END CATCH;
BEGIN TRY
 UPDATE dbo.Users SET CanApproveResults=1 WHERE UserID=@ID;
 THROW 56888,'Direct privilege replacement was accepted.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER() NOT IN(229,230) THROW; END CATCH;
BEGIN TRY
 INSERT dbo.AuditTrail(TableName,RecordID,Action) SELECT N'Users',@ID,N'Forged' WHERE 1=0;
 THROW 56889,'Direct audit insert was accepted.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER() NOT IN(229,230) THROW; END CATCH;
BEGIN TRY
 EXEC dbo.MicroEquipment_SignActivityStage @Activity=9223372036854775807,@Version=0x0100000000000000,
  @Actor=N'AMEEN\LimsReview',@Stage=N'Reviewed',@Reason=N'Disposable untrusted-origin rejection probe';
 THROW 56890,'Direct signing origin was accepted.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER() NOT IN(229,56750) THROW; END CATCH;

-- Caller-selected session hints cannot redirect the bound account.
EXEC sys.sp_set_session_context @key=N'Actor',@value=N'OtherFixture';
DECLARE @Commit TABLE(AuthenticationRowVersion binary(8));
INSERT @Commit EXEC dbo.EQ001_WindowsUat_CommitSuccess @UserID=@ID,@Username=N'AMEEN\LimsReview',@ExpectedVersion=@Version,@IsLogin=1;
IF (SELECT COUNT(*) FROM @Commit)<>1 OR NOT EXISTS(SELECT 1 FROM dbo.Users WHERE UserID=@ID AND LastLogin IS NOT NULL)
 THROW 56891,'Own login accounting failed.',1;
DELETE @Commit;
INSERT @Commit EXEC dbo.EQ001_WindowsUat_CommitSuccess @UserID=@ID,@Username=N'AMEEN\LimsReview',@ExpectedVersion=@Version,@IsLogin=1;
IF EXISTS(SELECT 1 FROM @Commit) THROW 56892,'Stale authentication rowversion was accepted.',1;
BEGIN TRY
 EXEC dbo.EQ001_WindowsUat_RecordFailure @VerifiedUserID=@ID,@Username=N'OtherFixture',
  @VerifiedLegacyHash=@Legacy,@VerifiedHash=@Hash,@VerifiedSalt=@Salt;
 THROW 56893,'Cross-account failure accounting was accepted.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>56875 THROW; END CATCH;
EXEC dbo.EQ001_WindowsUat_RecordFailure @VerifiedUserID=@ID,@Username=N'AMEEN\LimsReview',
 @VerifiedLegacyHash=@Legacy,@VerifiedHash=N'STALE_HASH',@VerifiedSalt=@Salt;
IF EXISTS(SELECT 1 FROM dbo.Users WHERE UserID=@ID AND FailedLoginAttempts<>0)
 THROW 56894,'Stale credential failure changed accounting.',1;
DECLARE @Attempts int=0;
WHILE @Attempts<5
BEGIN
 EXEC dbo.EQ001_WindowsUat_RecordFailure @VerifiedUserID=@ID,@Username=N'AMEEN\LimsReview',
  @VerifiedLegacyHash=@Legacy,@VerifiedHash=@Hash,@VerifiedSalt=@Salt;
 SET @Attempts+=1;
END;
IF NOT EXISTS(SELECT 1 FROM dbo.Users WHERE UserID=@ID AND FailedLoginAttempts=5 AND IsLocked=1 AND LockedUntil>SYSDATETIME())
 THROW 56895,'Own failure/lockout accounting failed.',1;
IF EXISTS(SELECT 1 FROM dbo.Users WHERE Username=N'OtherFixture' AND (FailedLoginAttempts<>0 OR CanApproveResults<>0))
 THROW 56896,'Another account was changed.',1;
DELETE @Own;
INSERT @Own EXEC dbo.EQ001_WindowsUat_ReadOwnUser @Username=N'AMEEN\LimsReview';
IF EXISTS(SELECT 1 FROM @Own) THROW 56897,'Locked-account credential read was accepted.',1;
PRINT 'PASS real Windows identity: own verifier only, cross-account refusal, direct credential/privilege/audit/signing denial, CAS, stale-credential refusal and lockout.';
PRINT 'This disposable SQL probe is not functional WPF signing acceptance.';
