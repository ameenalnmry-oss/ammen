using System.Reflection;
using PharmaLIMS.Infrastructure;

internal sealed partial class UatRun
{
    private async Task CheckProtectedCredentialPreflight()
    {
        // RED fixture: readiness metadata can be read; raw verifier columns remain denied.
        await Exec(_admin,"CREATE VIEW dbo.EQ001_CredentialReadiness AS SELECT Username,IsActive,CONVERT(bit,CASE WHEN NULLIF(LTRIM(RTRIM(PasswordHashNew)),N'') IS NOT NULL AND NULLIF(LTRIM(RTRIM(PasswordSalt)),N'') IS NOT NULL THEN 1 ELSE 0 END) AS CredentialInitialized FROM dbo.Users;");
        var ready = await ReadIdentityPreflight();
        Check(ready.Any(x=>x.Check=="Secure credentials" && x.Status=="PASS"),
            "real preflight succeeds without desktop access to credential columns");

        await Exec(_admin,"INSERT dbo.Users(Username,Role,PasswordHash,IsActive) VALUES(N'uat-preflight-pending',N'Technician',N'[MIGRATED]',1);");
        var missing = await ReadIdentityPreflight();
        Check(missing.Any(x=>x.Check=="Secure credential transition" && x.Status=="BLOCKER" && x.Details.Contains("uat-preflight-pending",StringComparison.Ordinal)),
            "protected preflight still blocks active accounts with missing credential fields");

        await Exec(_admin,"UPDATE dbo.Users SET PasswordHashNew=N'  ',PasswordSalt=N'  ' WHERE Username=N'uat-preflight-pending';");
        var blank = await ReadIdentityPreflight();
        Check(blank.Any(x=>x.Check=="Secure credential transition" && x.Status=="BLOCKER" && x.Details.Contains("uat-preflight-pending",StringComparison.Ordinal)),
            "protected preflight still blocks whitespace credential fields");
        await Exec(_admin,"DELETE dbo.Users WHERE Username=N'uat-preflight-pending';");
    }

    private static async Task<List<SystemPreflightCheck>> ReadIdentityPreflight()
    {
        var checks = new List<SystemPreflightCheck>();
        var method = typeof(SystemPreflightService).GetMethod("CheckIdentityIntegrityAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await (Task)method.Invoke(new SystemPreflightService(new DatabaseConnection()),new object[]{checks})!;
        return checks;
    }
}
