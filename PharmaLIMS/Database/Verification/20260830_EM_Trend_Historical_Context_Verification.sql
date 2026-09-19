SET NOCOUNT ON;
DECLARE @Missing TABLE(Item nvarchar(300));
IF COL_LENGTH(N'dbo.EM_Events',N'AreaCodeSnapshot') IS NULL INSERT @Missing VALUES(N'EM_Events.AreaCodeSnapshot');
IF COL_LENGTH(N'dbo.EM_Events',N'AreaNameSnapshot') IS NULL INSERT @Missing VALUES(N'EM_Events.AreaNameSnapshot');
IF COL_LENGTH(N'dbo.EM_Events',N'GradeSnapshot') IS NULL INSERT @Missing VALUES(N'EM_Events.GradeSnapshot');
IF COL_LENGTH(N'dbo.EM_Events',N'AreaSnapshotSource') IS NULL INSERT @Missing VALUES(N'EM_Events.AreaSnapshotSource');
IF NOT EXISTS(SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(N'dbo.EM_Events') AND name=N'TRG_EM_Events_ProtectAreaSnapshot_20260830' AND is_disabled=0)
    INSERT @Missing VALUES(N'TRG_EM_Events_ProtectAreaSnapshot_20260830 enabled');
IF EXISTS(SELECT 1 FROM @Missing)
BEGIN
    SELECT * FROM @Missing;
    THROW 53612, 'EM trend historical-context verification failed.', 1;
END;
SELECT N'PASS' AS VerificationResult;
