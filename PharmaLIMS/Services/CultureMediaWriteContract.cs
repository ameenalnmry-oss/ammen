namespace PharmaLIMS.Services;
internal static class CultureMediaWriteContract
{
    internal const string GuardLotIdentity=@"IF EXISTS(SELECT 1 FROM dbo.MediaQualifications WITH(UPDLOCK,HOLDLOCK) WHERE MediaLotID=@MediaLotID)
OR EXISTS(SELECT 1 FROM dbo.MediaPreparations WITH(UPDLOCK,HOLDLOCK) WHERE MediaLotID=@MediaLotID)
THROW 56459,'Lot identity is frozen by qualification/preparation history. Use controlled QA correction.',1;";
    internal const string EnsureMaster=@"SET NOCOUNT ON;
DECLARE @ID int;
SELECT @ID=MediaID FROM dbo.CultureMedia WITH(UPDLOCK,HOLDLOCK) WHERE UPPER(LTRIM(RTRIM(MediaCode)))=UPPER(LTRIM(RTRIM(@MediaCode)));
IF @@ROWCOUNT>1 THROW 56460,'The media code has ambiguous master identity.',1;
IF @ID IS NOT NULL
BEGIN
 IF EXISTS(SELECT 1 FROM dbo.CultureMedia WHERE MediaID=@ID AND (IsActive<>1 OR EXISTS
 (SELECT CONVERT(varbinary(max),MediaName),CONVERT(varbinary(max),MediaType),CONVERT(varbinary(max),StorageCondition)
 EXCEPT SELECT CONVERT(varbinary(max),@MediaName),CONVERT(varbinary(max),@MediaType),CONVERT(varbinary(max),@StorageCondition))))
 THROW 56461,'Existing media definition conflicts with the receipt. Use its controlled definition or a new controlled code.',1;
 -- Supplier belongs to the receipt lot; never rewrite a shared master manufacturer.
 SELECT @ID;
END
ELSE INSERT dbo.CultureMedia(MediaCode,MediaName,MediaType,Manufacturer,StorageCondition,DefaultExpiryDays,PreparationInstruction,IsActive,CreatedBy)
OUTPUT INSERTED.MediaID VALUES(@MediaCode,@MediaName,@MediaType,@Manufacturer,@StorageCondition,14,NULL,1,@UserName);";
}
