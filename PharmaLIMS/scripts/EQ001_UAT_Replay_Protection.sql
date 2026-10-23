/*
EQ-001: CONTROLLED UAT-ONLY replay protection preparation.
NOT an automatic production migration. A DBA must inspect the target database
and independently confirm that it is an isolated UAT clone before setting
@VerifiedIsolatedUat to 1. No signatures or historical rows are changed.
The request ID is supplied exclusively by the trusted host. Direct WPF INSERT
permissions must be cut over separately, only after end-to-end UAT passes.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @VerifiedIsolatedUat bit = 0; -- DBA/QA: deliberately opt in on ISOLATED UAT only
IF @VerifiedIsolatedUat <> 1
    THROW 56110, 'EQ-001: UAT-only script blocked; explicit isolated-UAT confirmation is required.', 1;

IF OBJECT_ID(N'dbo.LabEquipmentSignatures', N'U') IS NULL
    THROW 56111, 'EQ-001: Expected existing signature table is missing.', 1;
IF NOT EXISTS (
    SELECT 1 FROM sys.triggers
    WHERE parent_id = OBJECT_ID(N'dbo.LabEquipmentSignatures')
      AND name = N'TRG_LabEquipmentSignatures_AppendOnly' AND is_disabled = 0
)
    THROW 56112, 'EQ-001: Append-only trigger must be present and active.', 1;

BEGIN TRANSACTION;
DECLARE @BeforeCount bigint = (SELECT COUNT_BIG(*) FROM dbo.LabEquipmentSignatures);
IF COL_LENGTH(N'dbo.LabEquipmentSignatures', N'TrustedRequestId') IS NULL
    ALTER TABLE dbo.LabEquipmentSignatures ADD TrustedRequestId uniqueidentifier NULL;
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.LabEquipmentSignatures')
      AND name = N'UX_LabEquipmentSignatures_TrustedRequestId'
)
    EXEC(N'CREATE UNIQUE INDEX UX_LabEquipmentSignatures_TrustedRequestId
             ON dbo.LabEquipmentSignatures(TrustedRequestId)
             WHERE TrustedRequestId IS NOT NULL;');
IF @BeforeCount <> (SELECT COUNT_BIG(*) FROM dbo.LabEquipmentSignatures)
    THROW 56113, 'EQ-001: Historical signature count unexpectedly changed; rolling back.', 1;
COMMIT TRANSACTION;
PRINT N'EQ-001 UAT replay-protection schema prepared. No existing signature rows were edited.';
