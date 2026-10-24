using System.Reflection;
using PharmaLIMS.Infrastructure;
using PharmaLIMS;

internal sealed partial class UatRun
{
    private async Task CheckProtectedCredentialPreflight()
    {
        string deployment = System.IO.File.ReadAllText(System.IO.Path.Combine(settings.RepositoryRoot,"scripts/EQ001_UAT_Credential_Readiness.sql"));
        await Exec(_admin,deployment);
        await Exec(_admin,deployment); // A second apply must preserve the reviewed view.
        await SqlDenied(()=>Exec(AppConfig.ConnectionString,"SELECT PasswordHashNew,PasswordSalt FROM dbo.Users"),
            "readiness view deployment does not grant raw credential access",230);
        var ready = await ReadIdentityPreflight();
        Check(ready.Any(x=>x.Check=="Secure credentials" && x.Status=="PASS"),
            "real preflight succeeds without desktop access to credential columns");

        await Exec(_admin,"INSERT dbo.Users(Username,FullName,Role,PasswordHash,IsActive) VALUES(N'uat-preflight-pending',N'UAT credential readiness fixture',N'Technician',N'[MIGRATED]',1);");
        var missing = await ReadIdentityPreflight();
        Check(missing.Any(x=>x.Check=="Secure credential transition" && x.Status=="BLOCKER" && x.Details.Contains("uat-preflight-pending",StringComparison.Ordinal)),
            "protected preflight still blocks active accounts with missing credential fields");

        await Exec(_admin,"UPDATE dbo.Users SET PasswordHashNew=N'  ',PasswordSalt=N'  ' WHERE Username=N'uat-preflight-pending';");
        var blank = await ReadIdentityPreflight();
        Check(blank.Any(x=>x.Check=="Secure credential transition" && x.Status=="BLOCKER" && x.Details.Contains("uat-preflight-pending",StringComparison.Ordinal)),
            "protected preflight still blocks whitespace credential fields");
        await Exec(_admin,"DELETE dbo.Users WHERE Username=N'uat-preflight-pending';");

        await Exec(_admin,"DROP VIEW dbo.EQ001_CredentialReadiness;");
        var absent = await ReadIdentityPreflight();
        Check(absent.Any(x=>x.Check=="Secure credential transition" && x.Status=="BLOCKER" && x.Details.Contains("Missing protected credential readiness view",StringComparison.Ordinal)),
            "missing readiness deployment blocks preflight without a credential-reading fallback");
        await Exec(_admin,deployment);
    }

    private static async Task<List<SystemPreflightCheck>> ReadIdentityPreflight()
    {
        var checks = new List<SystemPreflightCheck>();
        var method = typeof(SystemPreflightService).GetMethod("CheckIdentityIntegrityAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await (Task)method.Invoke(new SystemPreflightService(new DatabaseConnection()),new object[]{checks})!;
        return checks;
    }
}
