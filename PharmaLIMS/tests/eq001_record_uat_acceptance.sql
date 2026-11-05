-- MANUAL ADMINISTRATIVE ATTESTATION, isolated UAT only.
-- Populate these fields ONLY after Windows-authenticated positive review/approval,
-- shared-role denials, content-integrity and workflow regression tests have passed.
-- Source contracts and SQL impersonation probes are not functional UAT acceptance.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() NOT LIKE N'PharmaLIMS_Integration[_]%'
 AND DB_NAME() NOT LIKE N'PharmaLIMS_UAT_CLEAN[_]%'
 AND DB_NAME() <> N'PharmaLIMS_UAT_20261001'
 THROW 56768,'UAT attestation is restricted to an isolated test database.',1;
DECLARE @EvidenceReference nvarchar(200)=NULL;
DECLARE @ReviewAndApprovalPassed bit=0,@NegativeSecurityPassed bit=0,
        @ContentIntegrityPassed bit=0,@RegressionPassed bit=0;
IF NULLIF(LTRIM(RTRIM(@EvidenceReference)),N'') IS NULL
 OR @ReviewAndApprovalPassed<>1 OR @NegativeSecurityPassed<>1
 OR @ContentIntegrityPassed<>1 OR @RegressionPassed<>1
 THROW 56769,'No UAT acceptance recorded: complete actual tests and evidence first.',1;
EXEC dbo.MicroEquipment_AssertTrustedBoundary;
INSERT dbo.EQ001_UAT_Acceptance
 (DatabaseName,TrustedBoundary,EvidenceReference,ReviewAndApprovalPassed,
  NegativeSecurityPassed,ContentIntegrityPassed,RegressionPassed,AcceptedBy)
VALUES(DB_NAME(),N'20261017_001',@EvidenceReference,@ReviewAndApprovalPassed,
 @NegativeSecurityPassed,@ContentIntegrityPassed,@RegressionPassed,ORIGINAL_LOGIN());
