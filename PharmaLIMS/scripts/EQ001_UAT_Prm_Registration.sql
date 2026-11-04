-- Exact isolated EQ001 UAT only. Explicit operator review is required before setting this local switch.
SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON; SET ARITHABORT ON; SET NUMERIC_ROUNDABORT OFF; SET XACT_ABORT ON;
DECLARE @VerifiedIsolatedUat bit = 0;
IF @VerifiedIsolatedUat<>1 OR DB_NAME()<>N'EQ001_UAT_AMEEN_20261024' OR @@SERVERNAME<>N'AMEEN\SQLEXPRESS'
 THROW 56930,'Review and explicitly select the exact isolated AMEEN EQ001 UAT target.',1;
BEGIN TRY
 BEGIN TRANSACTION;
 IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_TrustedEquipmentSigner') IS NULL THROW 56931,'Existing independent signer principal is required.',1;
 IF OBJECT_ID(N'dbo.EQ001_PrmRegistrationReceipts',N'U') IS NULL
 CREATE TABLE dbo.EQ001_PrmRegistrationReceipts(
  RequestId uniqueidentifier NOT NULL PRIMARY KEY,PayloadHash binary(32) NOT NULL,Actor nvarchar(100) NOT NULL,CallerSid nvarchar(184) NOT NULL,
  State nvarchar(12) NOT NULL,SampleId int NULL REFERENCES dbo.PRM_Samples(SampleID),SampleNumber nvarchar(50) NULL,TestCount int NOT NULL,
  RecordedAtUtc datetime2(7) NOT NULL,ActorVersion binary(8) NOT NULL,Reason nvarchar(500) NOT NULL,Meaning nvarchar(200) NOT NULL,
  CONSTRAINT CK_EQ001_PrmRegistrationReceipts_State CHECK((State=N'Committed' AND SampleId IS NOT NULL AND SampleNumber IS NOT NULL AND TestCount>0)
   OR(State=N'Cancelled' AND SampleId IS NULL AND SampleNumber IS NULL AND TestCount=0)));
 DECLARE @Procedure nvarchar(max)=N'CREATE OR ALTER PROCEDURE dbo.EQ001_UAT_PrmRegistration
 @Action nvarchar(16),@RequestId uniqueidentifier,@Payload nvarchar(max),
 @Actor nvarchar(100),@ActorVersion binary(8),@CallerSid nvarchar(184),@Reason nvarchar(500),@Meaning nvarchar(200)
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 IF DB_NAME()<>N''EQ001_UAT_AMEEN_20261024'' OR @@SERVERNAME<>N''AMEEN\SQLEXPRESS'' OR @@TRANCOUNT=0
  THROW 56930,''PRM registration requires exact isolated UAT and an explicit transaction.'',1;
 IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N''PharmaLIMS_TrustedEquipmentSigner'' AND type=N''U'' AND sid=SUSER_SID(ORIGINAL_LOGIN()))
  OR ISNULL(IS_SRVROLEMEMBER(N''sysadmin'',ORIGINAL_LOGIN()),1)<>0
  THROW 56931,''Only the independent Windows signing service can execute PRM registration.'',1;
 IF @Action IS NULL OR @Action NOT IN(N''Register'',N''Resolve'',N''Cancel'') OR @RequestId IS NULL OR @RequestId=CONVERT(uniqueidentifier,0x0)
  OR @Payload IS NULL OR ISJSON(@Payload)<>1 OR DATALENGTH(@Payload)>32768
  OR NULLIF(LTRIM(RTRIM(@CallerSid)),N'''') IS NULL OR NULLIF(LTRIM(RTRIM(@Reason)),N'''') IS NULL OR NULLIF(LTRIM(RTRIM(@Meaning)),N'''') IS NULL
  THROW 56932,''Invalid PRM request.'',1;
 DECLARE @Version binary(8);
 SELECT @Version=AuthenticationRowVersion FROM dbo.Users WITH(UPDLOCK,HOLDLOCK)
 WHERE Username=@Actor AND IsActive=1 AND IsLocked=0 AND MustChangePassword=0 AND CanRegisterSamples=1
  AND (LockedUntil IS NULL OR LockedUntil<=SYSDATETIME());
 IF @Version IS NULL OR @ActorVersion IS NULL OR @Version<>@ActorVersion THROW 56933,''PRM registration permission or authentication state changed.'',1;
 DECLARE @Hash binary(32)=HASHBYTES(''SHA2_256'',@Payload),@Lock int,@LockName nvarchar(255)=N''EQ001.Prm.''+CONVERT(nvarchar(36),@RequestId);
 EXEC @Lock=sys.sp_getapplock @Resource=@LockName,@LockMode=N''Exclusive'',@LockOwner=N''Transaction'',@LockTimeout=10000;
 IF @Lock<0 THROW 56934,''PRM request is busy; recover the same request.'',1;
 DECLARE @State nvarchar(12),@SampleId int,@Number nvarchar(50),@TestCount int=0;
 SELECT @State=State,@SampleId=SampleId,@Number=SampleNumber,@TestCount=TestCount
 FROM dbo.EQ001_PrmRegistrationReceipts WITH(UPDLOCK,HOLDLOCK) WHERE RequestId=@RequestId;
 IF @State IS NOT NULL AND EXISTS(SELECT 1 FROM dbo.EQ001_PrmRegistrationReceipts WHERE RequestId=@RequestId AND (PayloadHash<>@Hash OR Actor<>@Actor OR CallerSid<>@CallerSid OR Reason<>@Reason OR Meaning<>@Meaning))
  THROW 56935,''PRM request identity is bound to its original payload and caller.'',1;
 IF @State IS NOT NULL
 BEGIN
  SELECT @RequestId RequestId,@State State,ISNULL(@SampleId,0) SampleId,ISNULL(@Number,N'''') SampleNumber,
   CASE WHEN @State=N''Committed'' THEN N''Registered'' ELSE N'''' END Status,@TestCount TestCount FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
  RETURN;
 END;
 IF @Action=N''Resolve''
 BEGIN
  SELECT @RequestId RequestId,N''Pending'' State,0 SampleId,N'''' SampleNumber,N'''' Status,0 TestCount FOR JSON PATH,WITHOUT_ARRAY_WRAPPER; RETURN;
 END;
 IF @Action=N''Cancel''
 BEGIN
  INSERT dbo.EQ001_PrmRegistrationReceipts(RequestId,PayloadHash,Actor,CallerSid,State,SampleId,SampleNumber,TestCount,RecordedAtUtc,ActorVersion,Reason,Meaning)
  VALUES(@RequestId,@Hash,@Actor,@CallerSid,N''Cancelled'',NULL,NULL,0,SYSUTCDATETIME(),@ActorVersion,@Reason,@Meaning);
  SELECT @RequestId RequestId,N''Cancelled'' State,0 SampleId,N'''' SampleNumber,N'''' Status,0 TestCount FOR JSON PATH,WITHOUT_ARRAY_WRAPPER; RETURN;
 END;
 IF (SELECT COUNT(*) FROM OPENJSON(@Payload))<>42 OR EXISTS(SELECT [key] FROM OPENJSON(@Payload) GROUP BY [key] HAVING COUNT(*)<>1)
  OR EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key] COLLATE Latin1_General_100_BIN2 NOT IN(N''SampleCategory'',N''SamplePurpose'',N''MaterialCode'',N''MaterialName'',N''MaterialType'',N''Manufacturer'',N''Supplier'',N''ManufacturerLotNo'',N''SupplierLotNo'',N''GRNNo'',N''ReceivedDate'',N''ManufacturerExpiryDate'',N''RetestDate'',N''RetestReason'',N''PreviousReportNo'',N''PreviousRetestDate'',N''ProposedRetestDate'',N''RetestRemarks'',N''ProductCode'',N''ProductName'',N''BatchNo'',N''BatchSize'',N''DosageForm'',N''ProductionStage'',N''SampleSource'',N''SampledFrom'',N''MachineLineNo'',N''ManufacturingDate'',N''PackagingDate'',N''ExpiryDate'',N''PackSize'',N''SpecificationNo'',N''TestsRequired'',N''SampleQuantity'',N''Unit'',N''StorageCondition'',N''StabilityChamberNo'',N''StabilityProtocolNo'',N''SampleDateTime'',N''SampledBy'',N''Department'',N''Remarks'')) THROW 56936,''PRM payload fields do not match the typed contract.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''SampleCategory'' AND (type<>1 OR DATALENGTH(value)>80)) THROW 56936,''Invalid PRM field: SampleCategory.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''SamplePurpose'' AND (type<>1 OR DATALENGTH(value)>160)) THROW 56936,''Invalid PRM field: SamplePurpose.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''MaterialCode'' AND (type<>1 OR DATALENGTH(value)>160)) THROW 56936,''Invalid PRM field: MaterialCode.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''MaterialName'' AND (type<>1 OR DATALENGTH(value)>400)) THROW 56936,''Invalid PRM field: MaterialName.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''MaterialType'' AND (type<>1 OR DATALENGTH(value)>160)) THROW 56936,''Invalid PRM field: MaterialType.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''Manufacturer'' AND (type<>1 OR DATALENGTH(value)>320)) THROW 56936,''Invalid PRM field: Manufacturer.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''Supplier'' AND (type<>1 OR DATALENGTH(value)>320)) THROW 56936,''Invalid PRM field: Supplier.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''ManufacturerLotNo'' AND (type<>1 OR DATALENGTH(value)>240)) THROW 56936,''Invalid PRM field: ManufacturerLotNo.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''SupplierLotNo'' AND (type<>1 OR DATALENGTH(value)>240)) THROW 56936,''Invalid PRM field: SupplierLotNo.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''GRNNo'' AND (type<>1 OR DATALENGTH(value)>240)) THROW 56936,''Invalid PRM field: GRNNo.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''ReceivedDate'' AND (type NOT IN(0,1) OR (type=1 AND (TRY_CONVERT(date,value,126) IS NULL OR TRY_CONVERT(datetime2,value,126)<>CONVERT(datetime2,TRY_CONVERT(date,value,126)))))) THROW 56936,''Invalid PRM field: ReceivedDate.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''ManufacturerExpiryDate'' AND (type NOT IN(0,1) OR (type=1 AND (TRY_CONVERT(date,value,126) IS NULL OR TRY_CONVERT(datetime2,value,126)<>CONVERT(datetime2,TRY_CONVERT(date,value,126)))))) THROW 56936,''Invalid PRM field: ManufacturerExpiryDate.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''RetestDate'' AND (type NOT IN(0,1) OR (type=1 AND (TRY_CONVERT(date,value,126) IS NULL OR TRY_CONVERT(datetime2,value,126)<>CONVERT(datetime2,TRY_CONVERT(date,value,126)))))) THROW 56936,''Invalid PRM field: RetestDate.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''RetestReason'' AND (type<>1 OR DATALENGTH(value)>600)) THROW 56936,''Invalid PRM field: RetestReason.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''PreviousReportNo'' AND (type<>1 OR DATALENGTH(value)>160)) THROW 56936,''Invalid PRM field: PreviousReportNo.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''PreviousRetestDate'' AND (type NOT IN(0,1) OR (type=1 AND (TRY_CONVERT(date,value,126) IS NULL OR TRY_CONVERT(datetime2,value,126)<>CONVERT(datetime2,TRY_CONVERT(date,value,126)))))) THROW 56936,''Invalid PRM field: PreviousRetestDate.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''ProposedRetestDate'' AND (type NOT IN(0,1) OR (type=1 AND (TRY_CONVERT(date,value,126) IS NULL OR TRY_CONVERT(datetime2,value,126)<>CONVERT(datetime2,TRY_CONVERT(date,value,126)))))) THROW 56936,''Invalid PRM field: ProposedRetestDate.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''RetestRemarks'' AND (type<>1 OR DATALENGTH(value)>2000)) THROW 56936,''Invalid PRM field: RetestRemarks.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''ProductCode'' AND (type<>1 OR DATALENGTH(value)>160)) THROW 56936,''Invalid PRM field: ProductCode.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''ProductName'' AND (type<>1 OR DATALENGTH(value)>400)) THROW 56936,''Invalid PRM field: ProductName.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''BatchNo'' AND (type<>1 OR DATALENGTH(value)>240)) THROW 56936,''Invalid PRM field: BatchNo.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''BatchSize'' AND (type<>1 OR DATALENGTH(value)>160)) THROW 56936,''Invalid PRM field: BatchSize.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''DosageForm'' AND (type<>1 OR DATALENGTH(value)>160)) THROW 56936,''Invalid PRM field: DosageForm.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''ProductionStage'' AND (type<>1 OR DATALENGTH(value)>160)) THROW 56936,''Invalid PRM field: ProductionStage.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''SampleSource'' AND (type<>1 OR DATALENGTH(value)>240)) THROW 56936,''Invalid PRM field: SampleSource.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''SampledFrom'' AND (type<>1 OR DATALENGTH(value)>320)) THROW 56936,''Invalid PRM field: SampledFrom.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''MachineLineNo'' AND (type<>1 OR DATALENGTH(value)>240)) THROW 56936,''Invalid PRM field: MachineLineNo.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''ManufacturingDate'' AND (type NOT IN(0,1) OR (type=1 AND (TRY_CONVERT(date,value,126) IS NULL OR TRY_CONVERT(datetime2,value,126)<>CONVERT(datetime2,TRY_CONVERT(date,value,126)))))) THROW 56936,''Invalid PRM field: ManufacturingDate.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''PackagingDate'' AND (type NOT IN(0,1) OR (type=1 AND (TRY_CONVERT(date,value,126) IS NULL OR TRY_CONVERT(datetime2,value,126)<>CONVERT(datetime2,TRY_CONVERT(date,value,126)))))) THROW 56936,''Invalid PRM field: PackagingDate.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''ExpiryDate'' AND (type NOT IN(0,1) OR (type=1 AND (TRY_CONVERT(date,value,126) IS NULL OR TRY_CONVERT(datetime2,value,126)<>CONVERT(datetime2,TRY_CONVERT(date,value,126)))))) THROW 56936,''Invalid PRM field: ExpiryDate.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''PackSize'' AND (type<>1 OR DATALENGTH(value)>240)) THROW 56936,''Invalid PRM field: PackSize.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''SpecificationNo'' AND (type<>1 OR DATALENGTH(value)>240)) THROW 56936,''Invalid PRM field: SpecificationNo.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''TestsRequired'' AND (type<>1 OR DATALENGTH(value)>2000)) THROW 56936,''Invalid PRM field: TestsRequired.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''SampleQuantity'' AND (type NOT IN(0,2) OR (type=2 AND (TRY_CONVERT(decimal(18,3),value) IS NULL OR TRY_CONVERT(decimal(38,18),value)<>TRY_CONVERT(decimal(18,3),value))))) THROW 56936,''Invalid PRM field: SampleQuantity.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''Unit'' AND (type<>1 OR DATALENGTH(value)>60)) THROW 56936,''Invalid PRM field: Unit.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''StorageCondition'' AND (type<>1 OR DATALENGTH(value)>320)) THROW 56936,''Invalid PRM field: StorageCondition.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''StabilityChamberNo'' AND (type<>1 OR DATALENGTH(value)>240)) THROW 56936,''Invalid PRM field: StabilityChamberNo.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''StabilityProtocolNo'' AND (type<>1 OR DATALENGTH(value)>240)) THROW 56936,''Invalid PRM field: StabilityProtocolNo.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''SampleDateTime'' AND (type<>1 OR TRY_CONVERT(datetime2(0),value,126) IS NULL OR value<>CONVERT(nvarchar(19),TRY_CONVERT(datetime2(0),value,126),126))) THROW 56936,''Invalid PRM field: SampleDateTime.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''SampledBy'' AND (type<>1 OR DATALENGTH(value)>240)) THROW 56936,''Invalid PRM field: SampledBy.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''Department'' AND (type<>1 OR DATALENGTH(value)>240)) THROW 56936,''Invalid PRM field: Department.'',1;
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key]=N''Remarks'' AND (type<>1 OR DATALENGTH(value)>2000)) THROW 56936,''Invalid PRM field: Remarks.'',1;
 DECLARE @Category nvarchar(40)=JSON_VALUE(@Payload,''$.SampleCategory''),@No nvarchar(120)=JSON_VALUE(@Payload,''$.SpecificationNo''),
  @Stage nvarchar(80)=JSON_VALUE(@Payload,''$.ProductionStage''),@Now datetime2(0)=SYSDATETIME(),@SpecVersion int,
  @Sampling datetime2(0)=TRY_CONVERT(datetime2(0),JSON_VALUE(@Payload,''$.SampleDateTime''),126),@Item nvarchar(80),@Sequence nvarchar(60);
 SET @Item=CASE WHEN @Category IN(N''Raw Material'',N''Primary Packaging'') THEN JSON_VALUE(@Payload,''$.MaterialCode'') ELSE JSON_VALUE(@Payload,''$.ProductCode'') END;
 SET @Sequence=CASE @Category WHEN N''Raw Material'' THEN N''RM_SAMPLE'' WHEN N''Primary Packaging'' THEN N''PP_SAMPLE'' WHEN N''Production / In-Process'' THEN N''IP_SAMPLE'' WHEN N''Finished Product'' THEN N''FP_SAMPLE'' WHEN N''Stability'' THEN N''ST_SAMPLE'' END;
 IF @Sequence IS NULL OR NULLIF(LTRIM(RTRIM(@No)),N'''') IS NULL OR NULLIF(LTRIM(RTRIM(@Item)),N'''') IS NULL
  OR @Sampling<CONVERT(datetime2,''17530101'') OR @Sampling>@Now OR NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,''$.SampledBy''))),N'''') IS NULL
  OR (@Category=N''Production / In-Process'' AND NULLIF(LTRIM(RTRIM(@Stage)),N'''') IS NULL)
  OR (JSON_VALUE(@Payload,''$.SampleQuantity'') IS NOT NULL AND (TRY_CONVERT(decimal(18,3),JSON_VALUE(@Payload,''$.SampleQuantity''))<=0 OR NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,''$.Unit''))),N'''') IS NULL))
  THROW 56936,''Required PRM category, timestamp, item or quantity information is invalid.'',1;
 IF @Category IN(N''Raw Material'',N''Primary Packaging'') AND NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,''$.MaterialName''))),N'''') IS NULL THROW 56936,''Required material information is missing.'',1;
 IF @Category IN(N''Raw Material'',N''Primary Packaging'') AND NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,''$.ManufacturerLotNo''))),N'''') IS NULL THROW 56936,''Required material information is missing.'',1;
 IF @Category IN(N''Raw Material'',N''Primary Packaging'') AND NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,''$.SamplePurpose''))),N'''') IS NULL THROW 56936,''Required material information is missing.'',1;
 IF @Category NOT IN(N''Raw Material'',N''Primary Packaging'') AND NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,''$.ProductName''))),N'''') IS NULL THROW 56936,''Required product information is missing.'',1;
 IF @Category NOT IN(N''Raw Material'',N''Primary Packaging'') AND NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,''$.BatchNo''))),N'''') IS NULL THROW 56936,''Required product information is missing.'',1;
 IF JSON_VALUE(@Payload,''$.SamplePurpose'')=N''Retest'' AND NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,''$.RetestReason''))),N'''') IS NULL THROW 56936,''Retest reason is required.'',1;
 IF @Category=N''Stability'' AND NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,''$.ProductionStage''))),N'''') IS NULL THROW 56936,''Required stability information is missing.'',1;
 IF @Category=N''Stability'' AND NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,''$.SampleSource''))),N'''') IS NULL THROW 56936,''Required stability information is missing.'',1;
 IF @Category=N''Stability'' AND NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,''$.StorageCondition''))),N'''') IS NULL THROW 56936,''Required stability information is missing.'',1;
 IF @Category=N''Stability'' AND NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,''$.StabilityChamberNo''))),N'''') IS NULL THROW 56936,''Required stability information is missing.'',1;
 IF @Category=N''Stability'' AND NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,''$.StabilityProtocolNo''))),N'''') IS NULL THROW 56936,''Required stability information is missing.'',1;
 DECLARE @Control TABLE(Valid int); INSERT @Control SELECT CONVERT(int,CASE WHEN HASHBYTES(''SHA2_256'',REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID(N''dbo.EQ001_UAT_PrmVersionEvidence'')),CHARINDEX(N''VIEW'',OBJECT_DEFINITION(OBJECT_ID(N''dbo.EQ001_UAT_PrmVersionEvidence''))),LEN(OBJECT_DEFINITION(OBJECT_ID(N''dbo.EQ001_UAT_PrmVersionEvidence'')))),CHAR(13),N''''))=0xCF2EAC4C6D422D95F500FBCA2744C27E6A45E6E5F3EF8DE241281A532F977169
 AND HASHBYTES(''SHA2_256'',REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID(N''dbo.EQ001_UAT_EffectivePrmSpecificationTests'')),CHARINDEX(N''VIEW'',OBJECT_DEFINITION(OBJECT_ID(N''dbo.EQ001_UAT_EffectivePrmSpecificationTests''))),LEN(OBJECT_DEFINITION(OBJECT_ID(N''dbo.EQ001_UAT_EffectivePrmSpecificationTests'')))),CHAR(13),N''''))=0x187885741EB8DEBD04F64377EF98E3BAE73A38E851D1C906BDB72D499FF8A299
 AND HASHBYTES(''SHA2_256'',REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID(N''dbo.TRG_EQ001_PrmSupersessions_AppendOnly'')),CHARINDEX(N''TRIGGER'',OBJECT_DEFINITION(OBJECT_ID(N''dbo.TRG_EQ001_PrmSupersessions_AppendOnly''))),LEN(OBJECT_DEFINITION(OBJECT_ID(N''dbo.TRG_EQ001_PrmSupersessions_AppendOnly'')))),CHAR(13),N''''))=0x6BA055A4AC5F67FD7C924239626A9FE73EF7A30057905CF75FB5D98F30D7A047
 AND EXISTS(SELECT 1 FROM sys.triggers WHERE object_id=OBJECT_ID(N''dbo.TRG_EQ001_PrmSupersessions_AppendOnly'') AND parent_id=OBJECT_ID(N''dbo.EQ001_PrmSupersessions'') AND is_disabled=0)
 THEN 1 ELSE 0 END);
 IF NOT EXISTS(SELECT 1 FROM @Control WHERE Valid=1) THROW 56937,''Trusted PRM supersession controls must be validated before registration.'',1;
 SELECT configured.* INTO #Eligible FROM dbo.EQ001_UAT_EffectivePrmSpecificationTests configured WITH(UPDLOCK,HOLDLOCK)
 WHERE configured.SpecificationNo=@No AND configured.SampleCategory=@Category
 AND UPPER(LTRIM(RTRIM(configured.ItemCode))) IN(UPPER(LTRIM(RTRIM(@Item))),N''*'')
 AND (@Category<>N''Production / In-Process'' OR UPPER(LTRIM(RTRIM(ISNULL(configured.ProductionStage,N'''')))) IN(UPPER(LTRIM(RTRIM(@Stage))),N''*''))
 AND configured.IsActive=1 AND configured.ApprovalStatus=N''Approved''
 AND (configured.EffectiveDate IS NULL OR configured.EffectiveDate<=CAST(@Now AS date))
 AND NOT(ISNULL(configured.CreatedBy,N'''')=N''Controlled PRM Standard Profile Readiness 20260913'' AND (ISNULL(configured.ReviewedBy,N'''')=N''Controlled PRM Profile Review 20260913'' OR ISNULL(configured.ApprovedBy,N'''')=N''Controlled PRM Profile Approval 20260913''))

      AND NULLIF(LTRIM(RTRIM(ISNULL(configured.ReviewedBy,N''''))),N'''') IS NOT NULL
      AND configured.ReviewedDate IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(configured.ApprovedBy,N''''))),N'''') IS NOT NULL
      AND configured.ApprovedDate IS NOT NULL
      AND configured.ReviewedDate <= configured.ApprovedDate
      AND EXISTS
      (
          SELECT 1
          FROM dbo.PRM_SpecificationSignatures reviewSig
          INNER JOIN dbo.PRM_SpecificationSignatures approveSig
              ON approveSig.SpecificationNo=reviewSig.SpecificationNo
             AND approveSig.SampleCategory=reviewSig.SampleCategory
             AND approveSig.VersionNo=reviewSig.VersionNo
          WHERE reviewSig.SpecificationNo=configured.SpecificationNo
            AND reviewSig.SampleCategory=configured.SampleCategory
            AND reviewSig.VersionNo=configured.VersionNo
            AND reviewSig.ActionType=N''Review Specification''
            AND approveSig.ActionType=N''Approve Specification''
            AND UPPER(LTRIM(RTRIM(reviewSig.SignedBy)))=UPPER(LTRIM(RTRIM(configured.ReviewedBy)))
            AND UPPER(LTRIM(RTRIM(approveSig.SignedBy)))=UPPER(LTRIM(RTRIM(configured.ApprovedBy)))
            AND NULLIF(LTRIM(RTRIM(reviewSig.ActionReason)),N'''') IS NOT NULL
            AND NULLIF(LTRIM(RTRIM(reviewSig.MeaningOfSignature)),N'''') IS NOT NULL
            AND NULLIF(LTRIM(RTRIM(approveSig.ActionReason)),N'''') IS NOT NULL
            AND NULLIF(LTRIM(RTRIM(approveSig.MeaningOfSignature)),N'''') IS NOT NULL
            AND reviewSig.SignedAt <= approveSig.SignedAt
            AND (0=0 OR
                 UPPER(LTRIM(RTRIM(reviewSig.SignedBy)))<>UPPER(LTRIM(RTRIM(approveSig.SignedBy))))
      );
 SELECT @SpecVersion=MAX(VersionNo) FROM #Eligible;
 IF @SpecVersion IS NULL THROW 56938,''No effective approved signature-backed PRM specification matches this scope.'',1;
 DELETE #Eligible WHERE VersionNo<>@SpecVersion;
 IF EXISTS(SELECT TestCode FROM #Eligible GROUP BY TestCode HAVING COUNT(*)>1)
  OR EXISTS(SELECT 1 FROM #Eligible WHERE NULLIF(LTRIM(RTRIM(TestCode)),N'''') IS NULL OR NULLIF(LTRIM(RTRIM(SpecificationText)),N'''') IS NULL OR (RequiredTest=1 AND (MinimumElapsedHours IS NULL OR MinimumElapsedHours<0)))
  THROW 56938,''Approved PRM test snapshot is ambiguous or incomplete.'',1;
 DECLARE @Year int=YEAR(@Now),@Prefix nvarchar(20),@Last int,@CurrentYear int,@Max int,@Key nvarchar(30);
 SET @LockName=N''EQ001.Prm.Number.''+@Sequence;
 EXEC @Lock=sys.sp_getapplock @Resource=@LockName,@LockMode=N''Exclusive'',@LockOwner=N''Transaction'',@LockTimeout=10000;
 IF @Lock<0 THROW 56934,''PRM numbering is busy; retry the same request.'',1;
 IF (SELECT COUNT(*) FROM dbo.PRM_NumberSequences WITH(UPDLOCK,HOLDLOCK) WHERE SequenceName=@Sequence)<>1
  THROW 56939,''A single reviewed PRM sequence configuration is required.'',1;
 SELECT @Prefix=Prefix,@Last=LastNumber,@CurrentYear=CurrentYear FROM dbo.PRM_NumberSequences WITH(UPDLOCK,HOLDLOCK) WHERE SequenceName=@Sequence;
 IF NULLIF(LTRIM(RTRIM(@Prefix)),N'''') IS NULL OR @CurrentYear>@Year OR @Last<0 THROW 56939,''Invalid PRM numbering configuration.'',1;
 SET @Key=@Prefix+N''-''+CONVERT(nvarchar(4),@Year)+N''-'';
 SELECT @Max=ISNULL(MAX(TRY_CONVERT(int,SUBSTRING(SampleNumber,LEN(@Key)+1,20))),0)
 FROM dbo.PRM_Samples WITH(UPDLOCK,HOLDLOCK) WHERE LEFT(SampleNumber,LEN(@Key))=@Key;
 IF @CurrentYear<>@Year SET @Last=0;
 IF @Last<@Max SET @Last=@Max;
 IF @Last>=2147483647 THROW 56939,''PRM number sequence capacity reached.'',1;
 SET @Last=@Last+1;
 UPDATE dbo.PRM_NumberSequences SET CurrentYear=@Year,LastNumber=@Last,LastUpdated=@Now WHERE SequenceName=@Sequence;
 IF @@ROWCOUNT<>1 THROW 56939,''Ambiguous PRM number sequence.'',1;
 SET @Number=@Key+CASE WHEN @Last<10000 THEN RIGHT(N''0000''+CONVERT(nvarchar(10),@Last),4) ELSE CONVERT(nvarchar(10),@Last) END;
 IF LEN(@Number)>50 OR EXISTS(SELECT 1 FROM dbo.PRM_Samples WITH(UPDLOCK,HOLDLOCK) WHERE SampleNumber=@Number) THROW 56939,''PRM sample number collision or capacity reached.'',1;
 INSERT dbo.PRM_Samples(SampleNumber,SampleCategory,SamplePurpose,MaterialCode,MaterialName,MaterialType,Manufacturer,Supplier,ManufacturerLotNo,SupplierLotNo,GRNNo,ReceivedDate,ManufacturerExpiryDate,RetestDate,RetestReason,PreviousReportNo,PreviousRetestDate,ProposedRetestDate,RetestRemarks,ProductCode,ProductName,BatchNo,BatchSize,DosageForm,ProductionStage,SampleSource,SampledFrom,MachineLineNo,ManufacturingDate,PackagingDate,ExpiryDate,PackSize,SpecificationNo,TestsRequired,SampleQuantity,Unit,StorageCondition,StabilityChamberNo,StabilityProtocolNo,SampleDateTime,SampledBy,Department,Remarks,SpecificationVersionNo,SampleStatus,ResultInterpretation,ReportStatus,TimingReconciliationStatus,CreatedBy)
 VALUES(@Number,JSON_VALUE(@Payload,''$.SampleCategory''),NULLIF(JSON_VALUE(@Payload,''$.SamplePurpose''),N''''),NULLIF(JSON_VALUE(@Payload,''$.MaterialCode''),N''''),NULLIF(JSON_VALUE(@Payload,''$.MaterialName''),N''''),NULLIF(JSON_VALUE(@Payload,''$.MaterialType''),N''''),NULLIF(JSON_VALUE(@Payload,''$.Manufacturer''),N''''),NULLIF(JSON_VALUE(@Payload,''$.Supplier''),N''''),NULLIF(JSON_VALUE(@Payload,''$.ManufacturerLotNo''),N''''),NULLIF(JSON_VALUE(@Payload,''$.SupplierLotNo''),N''''),NULLIF(JSON_VALUE(@Payload,''$.GRNNo''),N''''),TRY_CONVERT(date,JSON_VALUE(@Payload,''$.ReceivedDate''),126),TRY_CONVERT(date,JSON_VALUE(@Payload,''$.ManufacturerExpiryDate''),126),TRY_CONVERT(date,JSON_VALUE(@Payload,''$.RetestDate''),126),NULLIF(JSON_VALUE(@Payload,''$.RetestReason''),N''''),NULLIF(JSON_VALUE(@Payload,''$.PreviousReportNo''),N''''),TRY_CONVERT(date,JSON_VALUE(@Payload,''$.PreviousRetestDate''),126),TRY_CONVERT(date,JSON_VALUE(@Payload,''$.ProposedRetestDate''),126),NULLIF(JSON_VALUE(@Payload,''$.RetestRemarks''),N''''),NULLIF(JSON_VALUE(@Payload,''$.ProductCode''),N''''),NULLIF(JSON_VALUE(@Payload,''$.ProductName''),N''''),NULLIF(JSON_VALUE(@Payload,''$.BatchNo''),N''''),NULLIF(JSON_VALUE(@Payload,''$.BatchSize''),N''''),NULLIF(JSON_VALUE(@Payload,''$.DosageForm''),N''''),NULLIF(JSON_VALUE(@Payload,''$.ProductionStage''),N''''),NULLIF(JSON_VALUE(@Payload,''$.SampleSource''),N''''),NULLIF(JSON_VALUE(@Payload,''$.SampledFrom''),N''''),NULLIF(JSON_VALUE(@Payload,''$.MachineLineNo''),N''''),TRY_CONVERT(date,JSON_VALUE(@Payload,''$.ManufacturingDate''),126),TRY_CONVERT(date,JSON_VALUE(@Payload,''$.PackagingDate''),126),TRY_CONVERT(date,JSON_VALUE(@Payload,''$.ExpiryDate''),126),NULLIF(JSON_VALUE(@Payload,''$.PackSize''),N''''),JSON_VALUE(@Payload,''$.SpecificationNo''),NULLIF(JSON_VALUE(@Payload,''$.TestsRequired''),N''''),TRY_CONVERT(decimal(18,3),JSON_VALUE(@Payload,''$.SampleQuantity'')),NULLIF(JSON_VALUE(@Payload,''$.Unit''),N''''),NULLIF(JSON_VALUE(@Payload,''$.StorageCondition''),N''''),NULLIF(JSON_VALUE(@Payload,''$.StabilityChamberNo''),N''''),NULLIF(JSON_VALUE(@Payload,''$.StabilityProtocolNo''),N''''),TRY_CONVERT(datetime2(0),JSON_VALUE(@Payload,''$.SampleDateTime''),126),JSON_VALUE(@Payload,''$.SampledBy''),NULLIF(JSON_VALUE(@Payload,''$.Department''),N''''),NULLIF(JSON_VALUE(@Payload,''$.Remarks''),N''''),@SpecVersion,N''Registered'',N''Not Tested'',N''Not Issued'',N''Not Required'',@Actor);
 SET @SampleId=CONVERT(int,SCOPE_IDENTITY());
 INSERT dbo.PRM_SampleTests(SampleID,TestCode,TestName,SpecificationText,Unit,ResultValue,ResultType,SpecificationLimit,Interpretation,Remarks,
  RequiredTest,MinimumElapsedHours,SortOrder,EnteredBy,EnteredDate,SourceSpecificationTestID,SpecificationVersionNo,SpecificationItemCode,SpecificationProductionStage)
 SELECT @SampleId,TestCode,TestName,SpecificationText,Unit,NULL,ResultType,SpecificationLimit,N''Not Tested'',NULL,
  RequiredTest,MinimumElapsedHours,SortOrder,NULL,NULL,SpecificationTestID,VersionNo,ItemCode,ProductionStage FROM #Eligible;
 SET @TestCount=@@ROWCOUNT;
 IF @TestCount<=0 THROW 56938,''No approved PRM tests assigned; registration rolled back.'',1;
 DECLARE @Snapshot nvarchar(max)=(SELECT s.*,@TestCount AssignedTests FROM dbo.PRM_Samples s WHERE SampleID=@SampleId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
 INSERT dbo.AuditTrail(TableName,RecordID,Action,OldValue,NewValue,ChangedBy,ChangeDate,PerformedBy,Reason,CreatedUtc,SourceWorkstation,SourceApplication,UtcRecordedAt,FieldName,SampleNumber,ModuleName)
 VALUES(N''PRM_Samples'',@SampleId,N''PRM Sample Registered'',N'''',@Snapshot,@Actor,@Now,@Actor,@Reason,SYSUTCDATETIME(),HOST_NAME(),N''PharmaLIMS.TrustedSigning'',SYSUTCDATETIME(),N''Registration'',@Number,N''PRM'');
 INSERT dbo.EQ001_PrmRegistrationReceipts(RequestId,PayloadHash,Actor,CallerSid,State,SampleId,SampleNumber,TestCount,RecordedAtUtc,ActorVersion,Reason,Meaning)
 VALUES(@RequestId,@Hash,@Actor,@CallerSid,N''Committed'',@SampleId,@Number,@TestCount,SYSUTCDATETIME(),@ActorVersion,@Reason,@Meaning);
 SELECT @RequestId RequestId,N''Committed'' State,@SampleId SampleId,@Number SampleNumber,N''Registered'' Status,@TestCount TestCount FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
END;
';
 IF OBJECT_ID(N'dbo.EQ001_UAT_PrmRegistration') IS NOT NULL AND
 (OBJECT_ID(N'dbo.EQ001_UAT_PrmRegistration',N'P') IS NULL OR OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_PrmRegistration')) IS NULL OR
 HASHBYTES('SHA2_256',REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_PrmRegistration')),CHARINDEX(N'PROCEDURE',OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_PrmRegistration'))),2147483647),CHAR(13),N''))<>
 HASHBYTES('SHA2_256',REPLACE(SUBSTRING(@Procedure,CHARINDEX(N'PROCEDURE',@Procedure),2147483647),CHAR(13),N'')))
 THROW 56930,'Unknown existing PRM registration procedure must not be overwritten.',1;
 IF OBJECT_ID(N'dbo.EQ001_UAT_PrmRegistration') IS NULL EXEC(@Procedure);
 DECLARE @Trigger nvarchar(max)=N'CREATE TRIGGER dbo.TRG_EQ001_PrmRegistrationReceipts_AppendOnly ON dbo.EQ001_PrmRegistrationReceipts AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; THROW 56940,''PRM registration receipts are append-only.'',1; END;';
 IF OBJECT_ID(N'dbo.TRG_EQ001_PrmRegistrationReceipts_AppendOnly') IS NOT NULL AND
  (OBJECT_ID(N'dbo.TRG_EQ001_PrmRegistrationReceipts_AppendOnly',N'TR') IS NULL OR
   ISNULL(HASHBYTES('SHA2_256',REPLACE(OBJECT_DEFINITION(OBJECT_ID(N'dbo.TRG_EQ001_PrmRegistrationReceipts_AppendOnly')),CHAR(13),N'')),0x)<>
   HASHBYTES('SHA2_256',REPLACE(@Trigger,CHAR(13),N''))) THROW 56940,'Unknown PRM receipt append-only trigger must not be overwritten.',1;
 IF OBJECT_ID(N'dbo.TRG_EQ001_PrmRegistrationReceipts_AppendOnly',N'TR') IS NULL EXEC(@Trigger);
 IF NOT EXISTS(SELECT 1 FROM sys.triggers WHERE object_id=OBJECT_ID(N'dbo.TRG_EQ001_PrmRegistrationReceipts_AppendOnly') AND parent_id=OBJECT_ID(N'dbo.EQ001_PrmRegistrationReceipts') AND is_disabled=0)
  THROW 56940,'PRM receipt append-only trigger is unavailable.',1;
 GRANT EXECUTE ON dbo.EQ001_UAT_PrmRegistration TO PharmaLIMS_TrustedEquipmentSigner;
 DENY INSERT,UPDATE,DELETE ON dbo.EQ001_PrmRegistrationReceipts TO PharmaLIMS_TrustedEquipmentSigner;
 COMMIT;
 PRINT N'PASS: PRM registration UAT boundary staged. No desktop or service direct DML grants, historical changes or Production support.';
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
