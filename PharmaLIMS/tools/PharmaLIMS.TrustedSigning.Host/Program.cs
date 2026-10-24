using System.IO;
using PharmaLIMS.TrustedSigning;
using System.ServiceProcess;
using System.Security.Cryptography.X509Certificates;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("EQ-001 service requires Windows.");
int index=Array.IndexOf(args,"--config");
if(index<0 || index+1>=args.Length)throw new ArgumentException("Specify --config with the protected host settings file.");
string configPath=Path.GetFullPath(args[index+1]);
if(args.Contains("--service",StringComparer.Ordinal))ServiceBase.Run(new SigningWindowsService(configPath));
else
{
    using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
    var settings=TrustedHostSettings.Load(configPath);using var certificate=settings.LoadCertificate();
    var server=BuildServer(settings,certificate);
    try{await server.RunAsync(stop.Token);}catch(OperationCanceledException)when(stop.IsCancellationRequested){}
}

static WindowsTrustedPipeServer BuildServer(TrustedHostSettings settings,X509Certificate2 certificate) => new(settings.PipeName,certificate,new TrustedCallerMap(settings.WindowsAccounts),new SqlTrustedSignerAccountRepository(settings.ConnectionString),new SqlTrustedEquipmentSignatureStore(settings.ConnectionString),new TrustedApplicationEndpoint(settings.ConnectionString),
    settings.DeploymentMode == "UAT" ? new TrustedActivitySigningCoordinator(new TrustedActivityAuthenticator(settings.ConnectionString),new SqlTrustedActivitySignatureStore(settings.ConnectionString)) : null);

sealed class SigningWindowsService : ServiceBase
{
    private readonly string _path;private CancellationTokenSource? _stop;private Task? _host;private X509Certificate2? _certificate;
    public SigningWindowsService(string path){_path=path;ServiceName="PharmaLIMS.TrustedSigning";}
    protected override void OnStart(string[] args)
    {
        var settings=TrustedHostSettings.Load(_path);
        _certificate=settings.LoadCertificate();
        var server=new WindowsTrustedPipeServer(settings.PipeName,_certificate,new TrustedCallerMap(settings.WindowsAccounts),new SqlTrustedSignerAccountRepository(settings.ConnectionString),new SqlTrustedEquipmentSignatureStore(settings.ConnectionString),new TrustedApplicationEndpoint(settings.ConnectionString),
            settings.DeploymentMode == "UAT" ? new TrustedActivitySigningCoordinator(new TrustedActivityAuthenticator(settings.ConnectionString),new SqlTrustedActivitySignatureStore(settings.ConnectionString)) : null);
        _stop=new CancellationTokenSource();var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host=server.RunAsync(_stop.Token,ready);
        if(!ready.Task.Wait(TimeSpan.FromSeconds(10)) || _host.IsFaulted){_stop.Cancel();_certificate.Dispose();throw new InvalidOperationException("Trusted signing listener failed to start.");}
        _=_host.ContinueWith(task=>{if(task.IsFaulted){_=task.Exception;ExitCode=1;Stop();}},TaskScheduler.Default);
    }
    protected override void OnStop()
    {
        _stop?.Cancel();
        try{if(_host is not null && !_host.IsFaulted)_host.Wait(TimeSpan.FromSeconds(20));}catch(AggregateException){}
        _certificate?.Dispose();_stop?.Dispose();
    }
}
