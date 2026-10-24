using Microsoft.Data.SqlClient;
using PharmaLIMS;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal sealed partial class UatRun
{
    private async Task PrepareActivitySchema()
    {
        if (!_db.StartsWith("EQ001_UAT_",StringComparison.Ordinal)) throw new Exception("Activity fixture database guard failed.");
        string fixture = File.ReadAllText(Path.Combine(settings.RepositoryRoot,"tests/fixtures/eq001_activity_site_procedures.sql"));
        foreach (string batch in Regex.Split(fixture,@"^\s*GO\s*$",RegexOptions.Multiline|RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await Exec(_admin,batch);
        string stagePolicy=File.ReadAllText(Path.Combine(settings.RepositoryRoot,"tests/eq001_activity_stage_policy_uat.sql"));
        await Exec(_admin,stagePolicy);
        await Exec(_admin,stagePolicy); // Idempotence must retain the corrected procedure.
        await Exec(_admin,$"CREATE ROLE PharmaLIMS_EquipmentWindowsUat;ALTER ROLE PharmaLIMS_EquipmentWindowsUat ADD MEMBER [{_clientLogin}];"+
            "GRANT EXECUTE ON dbo.EQ001_WindowsUat_ReadOwnUser TO PharmaLIMS_EquipmentWindowsUat;"+
            "GRANT EXECUTE ON dbo.EQ001_WindowsUat_CommitSuccess TO PharmaLIMS_EquipmentWindowsUat;"+
            "GRANT EXECUTE ON dbo.EQ001_WindowsUat_RecordFailure TO PharmaLIMS_EquipmentWindowsUat;"+
            "GRANT EXECUTE ON dbo.EQ001_WindowsUat_AssertActor TO PharmaLIMS_EquipmentWindowsUat;");
        await Exec(_admin,File.ReadAllText(Path.Combine(settings.RepositoryRoot,"tests/eq001_activity_service_boundary_uat.sql")));
    }

    private async Task ExerciseActivities(WindowsTrustedPipeClient pipe,AuthService auth,string client,TrustedHostSettings hostSettings)
    {
        await SqlDenied(()=>Exec(client,"EXEC dbo.EQ001_WindowsUat_ReadOwnUser @Username=N'uat-signer'"),"desktop principal cannot read password material through the retired compatibility procedure");
        await SqlDenied(()=>Exec(client,"EXEC dbo.EQ001_WindowsUat_CommitSuccess @UserID=1,@Username=N'uat-signer',@ExpectedVersion=0x0100000000000000,@IsLogin=1"),"desktop principal cannot reset failure counters through the retired success procedure");
        await SqlDenied(()=>Exec(client,"EXEC dbo.EQ001_WindowsUat_RecordFailure @VerifiedUserID=1,@Username=N'uat-signer',@VerifiedLegacyHash=N'forged',@VerifiedHash=N'forged',@VerifiedSalt=N'forged'"),"desktop principal cannot submit forged credential-state commits through the retired failure procedure");
        await SqlDenied(()=>Exec(client,"DECLARE @Actor nvarchar(256),@ActorID int;EXEC dbo.EQ001_WindowsUat_AssertActor @Username=N'uat-signer',@Actor=@Actor OUTPUT,@ActorID=@ActorID OUTPUT"),"retired compatibility identity endpoint remains unavailable to the desktop");
        await Exec(_admin,"UPDATE dbo.Users SET PasswordChangedAt=SYSUTCDATETIME() WHERE Username IN(N'uat-signer',N'uat-reviewer');"+
            "INSERT dbo.LabEquipment(EquipmentCode,EquipmentName,EquipmentType,Department,CreatedBy) VALUES(N'MIC-EQ-999',N'Activity UAT equipment',N'pH Meter',N'Microbiology',N'qa-fixture');");
        int equipmentId=Convert.ToInt32(await Scalar("SELECT EquipmentID FROM dbo.LabEquipment WHERE EquipmentCode=N'MIC-EQ-999'"));
        async Task<long> Seed(string performer) => Convert.ToInt64(await Scalar($"DECLARE @Created TABLE(ActivityID bigint);INSERT dbo.MicroEquipmentActivities(EquipmentID,ActivityType,ActivityStatus,PerformedBy,ActualStartAt,ActualEndAt,MethodReference,ActivityReason,EquipmentCodeSnapshot,EquipmentNameSnapshot) OUTPUT inserted.ActivityID INTO @Created VALUES({equipmentId},N'Use',N'Submitted',N'{performer}',SYSDATETIMEOFFSET(),SYSDATETIMEOFFSET(),N'UAT-METHOD',N'Synthetic disposable equipment use',N'MIC-EQ-999',N'Activity UAT equipment');SELECT ActivityID FROM @Created;"));
        async Task<ActivitySigningRequest> RequestFor(long id,string stage="Reviewed") => new(Guid.NewGuid(),id,stage,"Windows isolated UAT activity signature",DateTimeOffset.UtcNow,
            (byte[])(await Scalar("SELECT VersionToken FROM dbo.MicroEquipmentActivities WHERE ActivityID="+id))!,_db);

        long activity=await Seed("uat-technician");
        long historical=await Seed("historical-uat-control");
        string history=Convert.ToHexString((byte[])(await Scalar($"SELECT HASHBYTES('SHA2_256',(SELECT * FROM dbo.MicroEquipmentActivities WHERE ActivityID={historical} FOR JSON PATH))"))!);
        var initial=await RequestFor(activity);
        await SqlDenied(()=>Exec(client,$"EXEC dbo.EQ001_CommitActivitySignature @RequestId='{Guid.NewGuid()}',@Activity={activity},@Version=0x0100000000000000,@Actor=N'forged',@Stage=N'Reviewed',@Reason=N'Untrusted direct SQL request',@AuthenticationVersion=0x0100000000000000,@IssuedAtUtc='2026-01-01T00:00:00+00:00'"),"desktop SQL principal cannot execute the protected activity commit");
        await Denied(()=>pipe.CommitActivityAsync(initial,"uat-signer","wrong-password"),"activity endpoint independently rejects an incorrect signature password");
        Check(Convert.ToInt32(await Scalar("SELECT FailedLoginAttempts FROM dbo.Users WHERE Username=N'uat-signer'"))==1,"activity credential failure persists through the guarded authentication counter");
        Check(await UiActivity(auth,equipmentId,activity,_password),"actual WPF activity signature dialog commits review through the protected Windows service");
        Check(Convert.ToInt32(await Scalar($"SELECT COUNT(*) FROM dbo.MicroEquipmentActivitySignatures WHERE ActivityID={activity} AND SignatureMeaning=N'Reviewed' AND SignedBy=N'uat-signer' AND DATALENGTH(ContentHash)=32"))==1,"activity signature persists with the independently authenticated signer and SQL content hash");
        Check(Convert.ToInt32(await Scalar($"SELECT COUNT(*) FROM dbo.MicroEquipmentActivityAudit WHERE ActivityID={activity} AND ActionType=N'REVIEWED'"))==1,"activity stage and audit evidence commit together");
        await Denied(()=>pipe.CommitActivityAsync(initial with{RequestId=Guid.NewGuid(),IssuedAtUtc=DateTimeOffset.UtcNow},"uat-signer",_password),"activity signing rejects the stale SQL version","Conflict");
        long self=await Seed("uat-signer");
        var selfRequest=await RequestFor(self);
        await Denied(()=>pipe.CommitActivityAsync(selfRequest,"uat-signer",_password),"activity performer cannot review their own work","Conflict");
        long replayActivity=await Seed("uat-technician");
        var replay=await RequestFor(replayActivity);
        await pipe.CommitActivityAsync(replay,"uat-signer",_password);
        Check(true,"native activity request commits through the dedicated service SQL identity");
        await Denied(()=>pipe.CommitActivityAsync(replay,"uat-signer",_password),"repeated activity RequestId cannot append a second signature","Conflict");
        var approval=await RequestFor(activity,"Approved");
        await Denied(()=>pipe.CommitActivityAsync(approval,"uat-signer",_password),"activity approval requires current explicit approval permission");
        await Exec(_admin,"UPDATE dbo.Users SET CanApproveResults=1 WHERE Username IN(N'uat-signer',N'uat-reviewer');");
        await Denied(()=>pipe.CommitActivityAsync(approval,"uat-signer",_password),"activity reviewer cannot approve their own review","Conflict");

        settings.ActivityProbeDatabase=_db;settings.ActivityProbeId=self;settings.ActivityProbeVersion=Convert.ToBase64String((await RequestFor(self)).ExpectedVersion);
        File.WriteAllText(settings.SettingsPath,JsonSerializer.Serialize(settings));
        async Task Probe(string mode,string label)
        {
            var info=ServiceAccountProcess(Path.Combine(settings.PrivateDirectory,"probe","PharmaLIMS.TrustedSigning.WindowsUat.exe"));
            info.ArgumentList.Add(mode);info.ArgumentList.Add(settings.SettingsPath);
            using var process=Process.Start(info)!;await process.WaitForExitAsync();Check(process.ExitCode==0,label);
        }
        await Probe("--probe-activity-store","actual service SQL identity rejects stale authentication rowversion before signature persistence");
        await Probe("--probe-activity-self","actual Windows service identity cannot sign an activity as a mapped application user");

        StopHost();hostSettings.WindowsAccounts[settings.CallerSid]="uat-reviewer";
        File.WriteAllText(_hostConfigPath,JsonSerializer.Serialize(hostSettings));ProtectHostConfig();await StartHost();
        await pipe.CommitActivityAsync(await RequestFor(activity,"Approved"),"uat-reviewer",_password);
        Check(Convert.ToInt32(await Scalar($"SELECT COUNT(*) FROM dbo.MicroEquipmentActivitySignatures WHERE ActivityID={activity} AND SignatureMeaning=N'Approved' AND SignedBy=N'uat-reviewer'"))==1,"a distinct independently mapped reviewer can approve the reviewed activity");
        var wrongDatabase=(await RequestFor(self)) with{DatabaseName="EQ001_UAT_Different"};
        await Denied(()=>pipe.CommitActivityAsync(wrongDatabase,"uat-reviewer",_password),"activity caller cannot redirect a signature to another database");
        Check(history==Convert.ToHexString((byte[])(await Scalar($"SELECT HASHBYTES('SHA2_256',(SELECT * FROM dbo.MicroEquipmentActivities WHERE ActivityID={historical} FOR JSON PATH))"))!),"historical synthetic activity control remains byte-for-byte unchanged");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.EQ001_ActivitySigningRequests"))==3 && Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.MicroEquipmentActivitySignatures"))==3,"failed activity requests leave no replay ledger entries or extra signature rows");
        StopHost();hostSettings.WindowsAccounts[settings.CallerSid]="uat-signer";
        File.WriteAllText(_hostConfigPath,JsonSerializer.Serialize(hostSettings));ProtectHostConfig();await StartHost();
    }

    private async Task<bool> UiActivity(AuthService auth,int equipmentId,long activity,string password)
    {
        var window=new LabResources(auth);window.Show();
        var grid=(DataGrid)window.FindName("GridEquipment");var clock=Stopwatch.StartNew();
        while(grid.Items.Count==0 && clock.Elapsed<TimeSpan.FromSeconds(25))await Task.Delay(25);
        foreach(object item in grid.Items)
            if(Convert.ToInt32(item.GetType().GetProperty("EquipmentID")!.GetValue(item))==equipmentId){grid.SelectedItem=item;break;}
        if(grid.SelectedItem is null || Convert.ToInt32(grid.SelectedItem.GetType().GetProperty("EquipmentID")!.GetValue(grid.SelectedItem))!=equipmentId)
        { window.Close(); throw new Exception("Activity UAT equipment was not loaded and selected."); }
        var current=auth.GetCurrentUser();
        if(current is null || !current.IsActive || current.IsLocked || current.MustChangePassword || !current.CanReviewResults)
        { window.Close(); throw new Exception("Activity UAT account is not currently eligible for review."); }
        ((TextBox)window.FindName("TxtActivityId")).Text=activity.ToString();
        ((TextBox)window.FindName("TxtActivitySignatureReason")).Text="Windows UAT independently reviewed activity";
        TrustedEquipmentSignature? dialog=null;bool submitted=false;string? error=null;bool confirmed=false;
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};clock.Restart();
        timer.Tick+=(_,_)=>{
            NativeActivityDialogs.CloseUnexpected(title=>error="Unexpected validation dialog: "+title);
            dialog??=Application.Current.Windows.OfType<TrustedEquipmentSignature>().FirstOrDefault();
            if(dialog is not null && !submitted){submitted=true;((PasswordBox)dialog.FindName("TxtPassword")).Password=password;((Button)dialog.FindName("BtnConfirm")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
            if(dialog is not null){confirmed=dialog.IsConfirmed;error=((TextBlock)dialog.FindName("LblError")).Text;if(!string.IsNullOrEmpty(error))dialog.Close();}
            if(clock.Elapsed>TimeSpan.FromSeconds(35)){dialog?.Close();timer.Stop();}
        };
        timer.Start();
        try {
            typeof(LabResources).GetMethod("BtnReviewActivity_Click",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(window,new object[]{window,new RoutedEventArgs()});
            while(!submitted && string.IsNullOrEmpty(error) && clock.Elapsed<TimeSpan.FromSeconds(35))await Task.Delay(25);
            while(dialog is not null && dialog.IsVisible && clock.Elapsed<TimeSpan.FromSeconds(35))await Task.Delay(25);
            confirmed=dialog?.IsConfirmed==true;
        }
        finally{timer.Stop();window.Close();}
        if(!confirmed)throw new Exception("WPF activity signature failed: "+error);
        return confirmed;
    }
}

internal static class NativeActivityDialogs
{
    private delegate bool WindowCallback(IntPtr window,IntPtr parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId,WindowCallback callback,IntPtr parameter);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder value,int length);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder value,int length);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window,uint message,IntPtr first,IntPtr second);
    internal static void CloseUnexpected(Action<string> report)
    {
        EnumThreadWindows(GetCurrentThreadId(),(window,_)=>{
            var kind=new StringBuilder(256);GetClassName(window,kind,kind.Capacity);
            if(kind.ToString()=="#32770"){
                var title=new StringBuilder(512);GetWindowText(window,title,title.Capacity);
                report(title.ToString());PostMessage(window,0x0010,IntPtr.Zero,IntPtr.Zero);
            }
            return true;
        },IntPtr.Zero);
    }
}
