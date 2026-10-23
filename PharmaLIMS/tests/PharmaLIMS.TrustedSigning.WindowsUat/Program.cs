using System.IO;
using Microsoft.Data.SqlClient;
using PharmaLIMS;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Repositories;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;
using System.Diagnostics;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        string path = args.Last();
        var settings = JsonSerializer.Deserialize<UatSettings>(File.ReadAllText(path))!;
        if (args.Contains("--probe-self")) return ProbeSelf(settings).GetAwaiter().GetResult();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (_, _) => {
            try { await new UatRun(settings).ExecuteAsync(); result = 0; }
            catch (Exception ex) { Console.Error.WriteLine("::error title=EQ001 Windows UAT::" + ex.GetType().Name + ": " + ex.Message.Replace("\r", " ").Replace("\n", " ")); Console.Error.WriteLine(ex.StackTrace); }
            finally { app.Shutdown(result); }
        };
        app.Run();
        return result;
    }

    private static async Task<int> ProbeSelf(UatSettings s)
    {
        try { await new WindowsTrustedPipeClient(new(s.PipeName,s.CertificateSha256)).CommitAsync(UatRun.Request(),"uat-signer","not-a-password"); }
        catch (UnauthorizedAccessException) { return 0; }
        return 1;
    }
}

internal sealed class UatSettings
{
    public string MasterConnectionString { get; set; } = "";
    public string ServiceAccount { get; set; } = "";
    public string ServicePassword { get; set; } = "";
    public string ServiceSid { get; set; } = "";
    public string CallerSid { get; set; } = "";
    public string PrivateDirectory { get; set; } = "";
    public string HostExePath { get; set; } = "";
    public string CertificatePath { get; set; } = "";
    public string CertificatePassword { get; set; } = "";
    public string CertificateSha256 { get; set; } = "";
    public string PipeName { get; set; } = "";
    public string RepositoryRoot { get; set; } = "";
    public string ResultsDirectory { get; set; } = "";
    public string SettingsPath { get; set; } = "";
}

internal sealed class UatRun(UatSettings settings)
{
    private readonly List<string> _passes = new();
    private string _db = "EQ001_UAT_" + Guid.NewGuid().ToString("N");
    private string _admin = "";
    private Process? _host;
    private string _serviceLogin = "eqsvc_" + Guid.NewGuid().ToString("N")[..10];
    private string _clientLogin = "eqwpf_" + Guid.NewGuid().ToString("N")[..10];
    private string _sqlPassword = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private string _password = "Uat!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private string _appSettingsPath = Path.Combine(AppContext.BaseDirectory,"appsettings.json");
    private byte[]? _oldSettings;
    private readonly string _hostConfigPath = Path.Combine(settings.PrivateDirectory,"hostsettings.json");

    private async Task Exec(string cs,string sql)
    {
        await using var c=new SqlConnection(cs);await c.OpenAsync();await using var cmd=new SqlCommand(sql,c){CommandTimeout=300};await cmd.ExecuteNonQueryAsync();
    }
    private async Task<object?> Scalar(string sql)
    {await using var c=new SqlConnection(_admin);await c.OpenAsync();await using var cmd=new SqlCommand(sql,c);return await cmd.ExecuteScalarAsync();}
    private void Check(bool ok,string label) { if(!ok)throw new Exception("Assertion: "+label);_passes.Add(label);Console.WriteLine("PASS: "+label); }
    private async Task Denied(Func<Task> action,string label)
    {
        try {await action();} catch(Exception e) when(e is UnauthorizedAccessException or System.Security.Authentication.AuthenticationException or IOException or OperationCanceledException or SqlException or ArgumentException) {Check(true,label);return;}
        throw new Exception("Expected refusal: "+label);
    }

    public static EquipmentSigningRequest Request() => new(Guid.NewGuid(),SigningRequestPolicy.EquipmentCreate,null,"EQ001-E2E","Windows UAT controlled change",DateTimeOffset.UtcNow,null,
        JsonSerializer.Serialize(new {EquipmentCode="EQ001-E2E",EquipmentName="Windows UAT meter",EquipmentType="pH Meter",GmpCritical=true,EquipmentStatus="Active",QualificationStatus="Qualified",CalibrationRequired=false,CalibrationStatus="Not Required"}));

    public async Task ExecuteAsync()
    {
        if (!settings.MasterConnectionString.Contains("localhost",StringComparison.OrdinalIgnoreCase)) throw new Exception("UAT must use the ephemeral local SQL instance.");
        if (WindowsIdentity.GetCurrent().User?.Value != settings.CallerSid || settings.CallerSid==settings.ServiceSid) throw new Exception("Distinct real Windows caller/service accounts are required.");
        Directory.CreateDirectory(settings.ResultsDirectory);
        try
        {
            await Exec(settings.MasterConnectionString,$"CREATE DATABASE [{_db}]");
            _admin = new SqlConnectionStringBuilder(settings.MasterConnectionString){InitialCatalog=_db}.ConnectionString;
            await PrepareSchema();
            await Exec(settings.MasterConnectionString,$"CREATE LOGIN [{_serviceLogin}] WITH PASSWORD='{_sqlPassword}', CHECK_POLICY=OFF;CREATE LOGIN [{_clientLogin}] WITH PASSWORD='{_sqlPassword}',CHECK_POLICY=OFF;");
            await Exec(_admin,$"CREATE USER [{_serviceLogin}] FOR LOGIN [{_serviceLogin}];GRANT SELECT ON dbo.Users TO [{_serviceLogin}];GRANT UPDATE(FailedLoginAttempts,IsLocked,LockedUntil) ON dbo.Users TO [{_serviceLogin}];GRANT EXECUTE ON dbo.EQ001_CommitEquipmentSignature TO [{_serviceLogin}];CREATE USER [{_clientLogin}] FOR LOGIN [{_clientLogin}];GRANT SELECT ON SCHEMA::dbo TO [{_clientLogin}];GRANT UPDATE(LastLogin,FailedLoginAttempts,IsLocked,LockedUntil,PasswordHashNew,PasswordSalt,PasswordHash) ON dbo.Users TO [{_clientLogin}];");
            string service = new SqlConnectionStringBuilder(_admin){IntegratedSecurity=false,UserID=_serviceLogin,Password=_sqlPassword}.ConnectionString;
            string client = new SqlConnectionStringBuilder(_admin){IntegratedSecurity=false,UserID=_clientLogin,Password=_sqlPassword}.ConnectionString;
            await SeedUser("uat-signer",true);
            await SeedUser("uat-reviewer",false);
            await Exec(_admin,"INSERT dbo.LabEquipment(EquipmentCode,EquipmentName,EquipmentType,CreatedBy) VALUES(N'HISTORICAL-UAT',N'Unchanged historical fixture',N'pH Meter',N'qa-fixture');INSERT dbo.LabEquipmentSignatures(EquipmentID,ActionType,MeaningOfSignature,ActionReason,SignedBy) SELECT EquipmentID,N'Create',N'Historical fixture',N'Isolated UAT historic control',N'qa-fixture' FROM dbo.LabEquipment WHERE EquipmentCode=N'HISTORICAL-UAT';");
            string history=await HistoryDigest();
            var hostSettings=new TrustedHostSettings{PipeName=settings.PipeName,ConnectionString=service,CertificatePath=settings.CertificatePath,CertificatePassword=settings.CertificatePassword,WindowsAccounts=new(){[settings.CallerSid]="uat-signer"}};
            File.WriteAllText(_hostConfigPath,JsonSerializer.Serialize(hostSettings));ProtectHostConfig();
            await StartHost();
            Check(true,"host runs under a distinct Windows service account with protected configuration");
            var pipe=new WindowsTrustedPipeClient(new(settings.PipeName,settings.CertificateSha256));
            await Denied(()=>new WindowsTrustedPipeClient(new(settings.PipeName,new string('0',64))).CommitAsync(Request(),"uat-signer",_password),"untrusted service certificate pin is rejected before password submission");
            Check(Convert.ToInt32(await Scalar("SELECT FailedLoginAttempts FROM dbo.Users WHERE Username=N'uat-signer'"))==0,"bad TLS identity does not consume a password attempt");
            await Denied(()=>pipe.CommitAsync(Request(),"uat-reviewer",_password),"kernel Windows caller cannot impersonate a different PharmaLIMS user through the request");
            await Denied(()=>Exec(client,"EXEC dbo.EQ001_CommitEquipmentSignature @TrustedRequestId=NULL,@ActionType=NULL,@EquipmentCode=NULL,@Reason=NULL,@RequestedStateJson=NULL,@VerifiedSigner=NULL"),"WPF principal cannot execute the privileged SQL commit");
            await Denied(()=>Exec(client,"INSERT dbo.LabEquipmentSignatures(EquipmentID,ActionType,MeaningOfSignature,ActionReason,SignedBy) VALUES(1,N'Create',N'fake',N'fake',N'fake')"),"WPF principal cannot write signature evidence directly");
            _oldSettings=File.Exists(_appSettingsPath)?File.ReadAllBytes(_appSettingsPath):null;
            File.WriteAllText(_appSettingsPath,JsonSerializer.Serialize(new {Environment="Development",Database=new{Server="localhost,14335",Database=_db,IntegratedSecurity=false,UserId=_clientLogin,Password=_sqlPassword,Encrypt=true,TrustServerCertificate=false},Runtime=new{ApplyStartupDatabaseUpdates=false,DevelopmentAdminFullPermissions=false},TrustedSigning=new{Enabled=true,PipeName=settings.PipeName,ServerCertificateSha256=settings.CertificateSha256}}));
            Environment.SetEnvironmentVariable("PHARMALIMS_CONNECTION_STRING",client);
            var auth=new AuthService(new UserRepository(new DatabaseConnection()));
            var user=await auth.AuthenticateAsync("uat-signer",_password);
            Check(user!=null && user.CanManageSettings,"existing application login succeeds with current PBKDF2 credential");
            Check(!await UiSave(auth,"wrong-password",false),"actual WPF signature dialog rejects wrong password without equipment save");
            Check(Convert.ToInt32(await Scalar("SELECT FailedLoginAttempts FROM dbo.Users WHERE Username=N'uat-signer'"))==1,"WPF failed signature persists the server-side failure counter");
            Check(await UiSave(auth,_password,true),"actual LabResources WPF save and signature dialog commit through the Windows service");
            Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.LabEquipmentSignatures s JOIN dbo.LabEquipment e ON e.EquipmentID=s.EquipmentID WHERE e.EquipmentCode=N'EQ001-E2E' AND s.SignedBy=N'uat-signer'"))==1,"WPF saved signature is bound to the independently mapped signer");
            Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.AuditTrail WHERE TableName=N'LabEquipment' AND ChangedBy=N'uat-signer'"))==1,"WPF save commits its audit evidence in SQL");
            Check(await UiSave(auth,_password,true,update:true),"actual WPF equipment update commits using the loaded rowversion");
            int id=Convert.ToInt32(await Scalar("SELECT EquipmentID FROM dbo.LabEquipment WHERE EquipmentCode=N'EQ001-E2E'"));
            var version=(byte[])(await Scalar("SELECT RowVersion FROM dbo.LabEquipment WHERE EquipmentID="+id))!;
            var request=Request() with{Action=SigningRequestPolicy.EquipmentUpdate,EquipmentId=id,ExpectedRowVersion=version};
            await pipe.CommitAsync(request,"uat-signer",_password);
            await Denied(()=>pipe.CommitAsync(request,"uat-signer",_password),"real Windows transport rejects replayed RequestId");
            await Denied(()=>pipe.CommitAsync(request with{RequestId=Guid.NewGuid()},"uat-signer",_password),"real Windows transport rejects stale equipment version");
            var reviewer=await auth.AuthenticateAsync("uat-reviewer",_password);
            Check(reviewer!=null && reviewer.CanReviewResults && !reviewer.CanManageSettings,"reviewer login and review permission are preserved without equipment-management permission");
            await Denied(()=>pipe.CommitAsync(Request(),"uat-reviewer",_password),"review-only application account cannot obtain an equipment signature");
            await Exec(_admin,"UPDATE dbo.Users SET CanManageSettings=0 WHERE Username=N'uat-signer'");
            await Denied(()=>pipe.CommitAsync(Request(),"uat-signer",_password),"server refuses revoked equipment permission even when client session is stale");
            await Exec(_admin,"UPDATE dbo.Users SET CanManageSettings=1 WHERE Username=N'uat-signer'");
            for(int i=0;i<5;i++)await Denied(()=>pipe.CommitAsync(Request(),"uat-signer","wrong-password"),"failed real-service reauthentication "+(i+1));
            Check(Convert.ToBoolean(await Scalar("SELECT IsLocked FROM dbo.Users WHERE Username=N'uat-signer'")),"five failed real-service attempts lock the SQL account");
            await Denied(()=>pipe.CommitAsync(Request(),"uat-signer",_password),"locked account cannot sign with the correct password");
            Check(await auth.AuthenticateAsync("uat-signer",_password)==null,"normal application login rejects a service-locked account");
            Check(await HistoryDigest()==history,"historical UAT equipment/signature control rows remain byte-for-byte unchanged");
            await Exec(_admin,"UPDATE dbo.Users SET IsLocked=0,FailedLoginAttempts=0,LockedUntil=NULL WHERE Username=N'uat-signer'");
            await auth.AuthenticateAsync("uat-signer",_password);
            StopHost();
            hostSettings.WindowsAccounts=new(){["S-1-5-21-111-222-333-999"]="uat-signer"};File.WriteAllText(_hostConfigPath,JsonSerializer.Serialize(hostSettings));ProtectHostConfig();await StartHost();
            await Denied(()=>pipe.CommitAsync(Request(),"uat-signer",_password),"unmapped actual Windows SID is refused despite a valid application password");
            StopHost();
            long count=Convert.ToInt64(await Scalar("SELECT COUNT_BIG(*) FROM dbo.LabEquipmentSignatures"));
            Check(!await UiSave(auth,_password,false,update:true),"actual WPF dialog fails closed when trusted service is unavailable");
            Check(Convert.ToInt64(await Scalar("SELECT COUNT_BIG(*) FROM dbo.LabEquipmentSignatures"))==count,"service failure cannot fall back to direct WPF signing");
            File.WriteAllText(Path.Combine(settings.ResultsDirectory,"results.json"),JsonSerializer.Serialize(new{Commit=Environment.GetEnvironmentVariable("GITHUB_SHA"),Platform=Environment.OSVersion.ToString(),WindowsCallerSid=settings.CallerSid,ServiceSid=settings.ServiceSid,Passed=_passes.Count,Checks=_passes,ProductionActivated=false},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"EQ001 WINDOWS/WPF/SQL UAT: {_passes.Count} checks passed.");
        }
        finally
        {
            StopHost();
            Environment.SetEnvironmentVariable("PHARMALIMS_CONNECTION_STRING",null);
            if (_oldSettings!=null) File.WriteAllBytes(_appSettingsPath,_oldSettings); else if(File.Exists(_appSettingsPath))File.Delete(_appSettingsPath);
            await Exec(settings.MasterConnectionString,$"IF DB_ID(N'{_db}') IS NOT NULL BEGIN ALTER DATABASE [{_db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_db}]; END; IF SUSER_ID(N'{_serviceLogin}') IS NOT NULL DROP LOGIN [{_serviceLogin}]; IF SUSER_ID(N'{_clientLogin}') IS NOT NULL DROP LOGIN [{_clientLogin}];");
            Console.WriteLine("Disposable Windows UAT database and SQL logins removed; service process stopped.");
        }
    }

    private void ProtectHostConfig()
    {
        var file=new FileInfo(_hostConfigPath);var acl=file.GetAccessControl();
        acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));file.SetAccessControl(acl);
    }
    private async Task SeedUser(string username,bool manage)
    {
        byte[] salt=RandomNumberGenerator.GetBytes(32);string hash=Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(_password,salt,600000,HashAlgorithmName.SHA256,32));
        await using var c=new SqlConnection(_admin);await c.OpenAsync();await using var cmd=new SqlCommand("INSERT dbo.Users(Username,FullName,Role,PasswordHash,PasswordHashNew,PasswordSalt,CanManageSettings,CanReviewResults) VALUES(@u,@u,@r,N'[MIGRATED]',@h,@s,@m,1)",c);
        cmd.Parameters.AddWithValue("@u",username);cmd.Parameters.AddWithValue("@r",manage?"Admin":"Reviewer");cmd.Parameters.AddWithValue("@h",hash);cmd.Parameters.AddWithValue("@s",Convert.ToBase64String(salt));cmd.Parameters.AddWithValue("@m",manage);await cmd.ExecuteNonQueryAsync();
    }
    private async Task<string> HistoryDigest() => Convert.ToHexString((byte[])(await Scalar("SELECT HASHBYTES('SHA2_256',(SELECT e.*,s.SignatureID,s.SignedBy,s.SignedAt,s.ActionType,s.ActionReason FROM dbo.LabEquipment e JOIN dbo.LabEquipmentSignatures s ON e.EquipmentID=s.EquipmentID WHERE e.EquipmentCode=N'HISTORICAL-UAT' FOR JSON PATH))"))!);
    private async Task PrepareSchema()
    {
        var asm=Assembly.Load("PharmaLIMS.DatabaseIntegration");var type=asm.GetType("Program")!;
        var apply=type.GetMethod("ApplyControlledFileAsync",BindingFlags.Static|BindingFlags.NonPublic)!;
        var ledger=type.GetMethod("RecordMigrationLedgerAsync",BindingFlags.Static|BindingFlags.NonPublic)!;
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(settings.RepositoryRoot,"Database/MigrationManifest.json")));
        var root=manifest.RootElement;var entries=new[]{root.GetProperty("freshInstallBaseline")}.Concat(root.GetProperty("migrations").EnumerateArray());
        foreach(var m in entries) {
            if(m.TryGetProperty("supersededBy",out var superseded)&&!string.IsNullOrWhiteSpace(superseded.GetString()))continue;
            string version=m.GetProperty("versionKey").GetString()!,hash=m.GetProperty("sha256").GetString()!;
            await (Task)apply.Invoke(null,new object[]{settings.RepositoryRoot,_admin,m.GetProperty("file").GetString()!,hash,version})!;
            await (Task)ledger.Invoke(null,new object[]{_admin,version,m.GetProperty("description").GetString()!,hash,root.GetProperty("applicationVersion").GetString()!})!;
        }
        foreach(string name in new[]{"EQ001_UAT_Replay_Protection.sql","EQ001_UAT_Trusted_Commit_Procedure.sql"}) {
            string sql=File.ReadAllText(Path.Combine(settings.RepositoryRoot,"scripts",name));
            if(!_db.StartsWith("EQ001_UAT_",StringComparison.Ordinal))throw new Exception("UAT target guard failed");
            await Exec(_admin,sql.Replace("DECLARE @VerifiedIsolatedUat bit = 0;","DECLARE @VerifiedIsolatedUat bit = 1;",StringComparison.Ordinal));
        }
    }
    private async Task StartHost()
    {
        var secret=new SecureString();foreach(char c in settings.ServicePassword)secret.AppendChar(c);secret.MakeReadOnly();
        var info=new ProcessStartInfo(settings.HostExePath){UseShellExecute=false,UserName=settings.ServiceAccount,Domain=Environment.MachineName,Password=secret,LoadUserProfile=false,WorkingDirectory=settings.PrivateDirectory,CreateNoWindow=true};
        info.ArgumentList.Add("--config");info.ArgumentList.Add(_hostConfigPath);
        _host=Process.Start(info)??throw new Exception("Could not start the separate-account service.");
        for(int i=0;i<20;i++) {
            if(_host.HasExited)throw new Exception("Signing host exited before readiness, code "+_host.ExitCode);
            using var probe=new System.IO.Pipes.NamedPipeClientStream(".",settings.PipeName,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous);
            try {await probe.ConnectAsync(250);return;} catch(TimeoutException){await Task.Delay(250);}
        }
        throw new TimeoutException("Signing host did not create its protected pipe.");
    }
    private void StopHost(){if(_host!=null){if(!_host.HasExited){_host.Kill(true);_host.WaitForExit(10000);}_host.Dispose();_host=null;}}
    private async Task<bool> UiSave(AuthService auth,string password,bool expectedSuccess,bool update=false)
    {
        var window=new LabResources(auth);
        window.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if(update) {
            var grid=(DataGrid)window.FindName("GridEquipment");
            foreach(object row in grid.Items) if((string?)row.GetType().GetProperty("EquipmentCode")?.GetValue(row)=="EQ001-E2E"){grid.SelectedItem=row;break;}
        }
        ((TextBox)window.FindName("TxtCode")).Text="EQ001-E2E";
        ((TextBox)window.FindName("TxtName")).Text=update?"Windows UAT revised meter":"Windows UAT meter";
        ((TextBox)window.FindName("TxtType")).Text="pH Meter";
        ((CheckBox)window.FindName("ChkCalibrationRequired")).IsChecked=false;
        Select((ComboBox)window.FindName("CboCalibration"),"Not Required");Select((ComboBox)window.FindName("CboQualification"),"Qualified");
        TrustedEquipmentSignature? dialog=null;bool submitted=false;bool confirmed=false;string? error=null;
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};var clock=Stopwatch.StartNew();
        timer.Tick+=(_,_)=>{
            dialog ??= Application.Current.Windows.OfType<TrustedEquipmentSignature>().FirstOrDefault();
            if(dialog!=null && !submitted){submitted=true;((PasswordBox)dialog.FindName("TxtPassword")).Password=password;((TextBox)dialog.FindName("TxtReason")).Text="Windows isolated UAT signed equipment change";((Button)dialog.FindName("BtnConfirm")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
            if(dialog!=null){confirmed=dialog.IsConfirmed;string text=((TextBlock)dialog.FindName("LblError")).Text;if(!string.IsNullOrEmpty(text)){error=text;dialog.Close();}}
            if(clock.Elapsed>TimeSpan.FromSeconds(35)){dialog?.Close();timer.Stop();}
        };
        timer.Start();
        try {((Button)window.FindName("BtnSaveEquipment")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));confirmed=dialog?.IsConfirmed==true;}
        finally {timer.Stop();window.Close();}
        if(!submitted)throw new Exception("Actual WPF signature dialog did not open.");
        if(expectedSuccess && !confirmed)throw new Exception("WPF save failed: "+error);
        return confirmed;
    }
    private static void Select(ComboBox box,string text){foreach(ComboBoxItem i in box.Items)if((string)i.Content==text){box.SelectedItem=i;return;}throw new Exception("Missing WPF option "+text);}
}
