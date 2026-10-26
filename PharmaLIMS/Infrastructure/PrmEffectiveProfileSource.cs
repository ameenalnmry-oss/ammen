namespace PharmaLIMS.Infrastructure;

// Site UAT only. Historical loads must continue reading the original table.
public static class PrmEffectiveProfileSource
{
    public static bool Enabled => AppConfig.TrustedSigningEnabled &&
        AppConfig.DatabaseName.StartsWith("EQ001_UAT_", System.StringComparison.OrdinalIgnoreCase);

    public static string Sql => Enabled
        ? "dbo.EQ001_UAT_EffectivePrmSpecificationTests"
        : "dbo.PRM_SpecificationTests";
    public const string ControlValidationSql = @"SELECT CONVERT(int,CASE WHEN HASHBYTES('SHA2_256',REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_PrmVersionEvidence')),CHARINDEX(N'VIEW',OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_PrmVersionEvidence'))),LEN(OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_PrmVersionEvidence')))),CHAR(13),N''))=0xCF2EAC4C6D422D95F500FBCA2744C27E6A45E6E5F3EF8DE241281A532F977169
 AND HASHBYTES('SHA2_256',REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_EffectivePrmSpecificationTests')),CHARINDEX(N'VIEW',OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_EffectivePrmSpecificationTests'))),LEN(OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_EffectivePrmSpecificationTests')))),CHAR(13),N''))=0x187885741EB8DEBD04F64377EF98E3BAE73A38E851D1C906BDB72D499FF8A299
 AND HASHBYTES('SHA2_256',REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID(N'dbo.TRG_EQ001_PrmSupersessions_AppendOnly')),CHARINDEX(N'TRIGGER',OBJECT_DEFINITION(OBJECT_ID(N'dbo.TRG_EQ001_PrmSupersessions_AppendOnly'))),LEN(OBJECT_DEFINITION(OBJECT_ID(N'dbo.TRG_EQ001_PrmSupersessions_AppendOnly')))),CHAR(13),N''))=0x6BA055A4AC5F67FD7C924239626A9FE73EF7A30057905CF75FB5D98F30D7A047
 AND EXISTS(SELECT 1 FROM sys.triggers WHERE object_id=OBJECT_ID(N'dbo.TRG_EQ001_PrmSupersessions_AppendOnly') AND parent_id=OBJECT_ID(N'dbo.EQ001_PrmSupersessions') AND is_disabled=0)
 THEN 1 ELSE 0 END);";
}
