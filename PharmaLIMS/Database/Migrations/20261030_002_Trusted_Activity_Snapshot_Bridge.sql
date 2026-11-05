SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.MicroEquipmentActivities',N'U') IS NULL
    THROW 56764,'Activity snapshot bridge requires dbo.MicroEquipmentActivities.',1;
IF OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage',N'P') IS NULL
    THROW 56764,'Activity snapshot bridge requires dbo.MicroEquipment_SignActivityStage.',1;
IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_TrustedEquipmentSigner') IS NULL
    THROW 56764,'Trusted equipment signer role is missing.',1;
GO
CREATE OR ALTER PROCEDURE dbo.MicroEquipment_ReadActivitySnapshot
    @Activity bigint,
    @Equipment int,
    @Stage nvarchar(30)
AS
BEGIN
    SET NOCOUNT ON;
    IF @Activity<=0 OR @Equipment<=0 OR @Stage NOT IN(N'Reviewed',N'Approved')
        THROW 56764,'Invalid activity snapshot request.',1;
    DECLARE @Prior nvarchar(30)=CASE WHEN @Stage=N'Reviewed' THEN N'Submitted' ELSE N'Reviewed' END;
    IF NOT EXISTS(
        SELECT 1 FROM dbo.MicroEquipmentActivities
        WHERE ActivityID=@Activity AND EquipmentID=@Equipment AND ActivityStatus=@Prior)
        THROW 56765,'Activity stage, equipment or identity changed before signing.',1;
    SELECT ActivityID,EquipmentID,ActivityStatus,VersionToken
    FROM dbo.MicroEquipmentActivities
    WHERE ActivityID=@Activity AND EquipmentID=@Equipment AND ActivityStatus=@Prior;
END;
GO
GRANT EXECUTE ON dbo.MicroEquipment_ReadActivitySnapshot TO PharmaLIMS_TrustedEquipmentSigner;
GO