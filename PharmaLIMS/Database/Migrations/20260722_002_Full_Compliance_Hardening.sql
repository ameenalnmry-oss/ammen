SET XACT_ABORT ON;
BEGIN TRY
BEGIN TRAN;
IF COL_LENGTH(N'dbo.LIMS_SchemaVersions',N'MigrationChecksum') IS NULL ALTER TABLE dbo.LIMS_SchemaVersions ADD MigrationChecksum NVARCHAR(128) NULL;
IF COL_LENGTH(N'dbo.LIMS_SchemaVersions',N'ApplicationVersion') IS NULL ALTER TABLE dbo.LIMS_SchemaVersions ADD ApplicationVersion NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules',N'ReviewedBy') IS NULL ALTER TABLE dbo.EM_Schedules ADD ReviewedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules',N'ReviewedAt') IS NULL ALTER TABLE dbo.EM_Schedules ADD ReviewedAt DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules',N'ApprovedBy') IS NULL ALTER TABLE dbo.EM_Schedules ADD ApprovedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules',N'ApprovedAt') IS NULL ALTER TABLE dbo.EM_Schedules ADD ApprovedAt DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules',N'MediaPreparationID') IS NULL ALTER TABLE dbo.EM_Schedules ADD MediaPreparationID INT NULL;
IF NOT EXISTS(SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20260722_002') INSERT dbo.LIMS_SchemaVersions(VersionKey,Description,ApplicationVersion) VALUES(N'20260722_002',N'Full compliance hardening',N'2026.7.22.40');
COMMIT;
END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
