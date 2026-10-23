using PharmaLIMS.TrustedSigning;
using System.ServiceProcess;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("EQ-001 service requires Windows.");
int index = Array.IndexOf(args, "--config");
if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Specify --config with the protected host settings file.");
string configPath = Path.GetFullPath(args[index + 1]);
if (args.Contains("--service", StringComparer.Ordinal))
    ServiceBase.Run(new SigningWindowsService(configPath));
else
{
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    await RunHost(configPath, stop.Token);
}

static async Task RunHost(string path, CancellationToken cancellationToken)
{
    TrustedHostSettings settings = TrustedHostSettings.Load(path);
    using var certificate = settings.LoadCertificate();
    var server = new WindowsTrustedPipeServer(settings.PipeName, certificate,
        new TrustedCallerMap(settings.WindowsAccounts),
        new SqlTrustedSignerAccountRepository(settings.ConnectionString),
        new SqlTrustedEquipmentSignatureStore(settings.ConnectionString));
    try { await server.RunAsync(cancellationToken); }
    catch (OperationCanceledException) when(cancellationToken.IsCancellationRequested) { }
}

sealed class SigningWindowsService : ServiceBase
{
    private readonly string path;
    public SigningWindowsService(string configPath) { path = configPath; ServiceName = "PharmaLIMS.TrustedSigning"; }
    private CancellationTokenSource? _stop;
    private Task? _host;
    protected override void OnStart(string[] args)
    {
        ServiceName = "PharmaLIMS.TrustedSigning";
        _stop = new CancellationTokenSource();
        // Validate protected configuration synchronously so SCM never reports a broken host as started.
        _ = TrustedHostSettings.Load(path);
        _host = Task.Run(async () => {
            TrustedHostSettings settings = TrustedHostSettings.Load(path);
            using var certificate = settings.LoadCertificate();
            var server = new WindowsTrustedPipeServer(settings.PipeName, certificate, new TrustedCallerMap(settings.WindowsAccounts),
                new SqlTrustedSignerAccountRepository(settings.ConnectionString), new SqlTrustedEquipmentSignatureStore(settings.ConnectionString));
            try { await server.RunAsync(_stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
    }
    protected override void OnStop() { _stop?.Cancel(); _host?.Wait(TimeSpan.FromSeconds(20)); _stop?.Dispose(); }
}
