-- Controlled EQ-001 reviewer mapping on isolated UAT database only.
-- This record cannot authenticate with a PharmaLIMS password; HTTP.sys
-- must independently authenticate AMEEN\Elite and enforce reviewer group SID.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'PharmaLIMS_Integration_2c941d953f70'
 THROW 56801, 'EQ-001 reviewer mapping is restricted to the exact isolated UAT database.', 1;
IF EXISTS (SELECT 1 FROM dbo.Users WHERE Username=N'AMEEN\Elite')
 THROW 56802, 'Existing PharmaLIMS user found; manual review required to preserve data.', 1;
BEGIN TRANSACTION;
BEGIN TRY
 INSERT dbo.Users
  (Username,FullName,Role,IsActive,IsLocked,CanReviewResults,CanApproveResults,
   CanAccessWater,CanAccessEM,CanRegisterSamples,CanEnterResults,CanIssueCOA,
   CanManageUsers,CanManageSettings,MustChangePassword)
 VALUES
  (N'AMEEN\Elite',N'EQ001 isolated UAT reviewer',N'UAT-Test',1,0,1,0,
   0,0,0,0,0,0,0,1);
 IF (SELECT COUNT_BIG(*) FROM dbo.Users
     WHERE Username=N'AMEEN\Elite' AND IsActive=1 AND IsLocked=0
       AND CanReviewResults=1 AND CanApproveResults=0
       AND PasswordHash IS NULL AND PasswordHashNew IS NULL)<>1
 THROW 56803, 'Safe UAT reviewer mapping verification failed.', 1;
 COMMIT;
 PRINT 'PASS: Isolated UAT Windows reviewer mapping inserted without PharmaLIMS password.';
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
