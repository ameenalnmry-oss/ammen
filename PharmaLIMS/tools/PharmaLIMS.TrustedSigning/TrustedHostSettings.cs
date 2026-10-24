using Microsoft.Data.SqlClient;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PharmaLIMS.TrustedSigning;

public sealed class TrustedHostSettings
{
    public string PipeName { get; set; } = "PharmaLIMS_EQ001";
    public string ConnectionString { get; set; } = "";
    public string CertificatePath { get; set; } = "";
    public string CertificatePassword { get; set; } = "";
    public string DeploymentMode { get; set; } = "UAT";
    public bool ProductionActivationApproved { get; set; }
    public Dictionary<string,string> WindowsAccounts { get; set; } = new();

    [SupportedOSPlatform("windows")]
    public static TrustedHostSettings Load(string path)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("Service account is missing.");
        RequirePrivateFile(path, sid);
        var settings = JsonSerializer.Deserialize<TrustedHostSettings>(File.ReadAllText(path), new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new ArgumentException("Service configuration is missing.");
        settings.Validate();
        RequirePrivateFile(settings.CertificatePath, sid);
        return settings;
    }

    public void Validate()
    {
        new TrustedPipeClientOptions(PipeName, new string('0',64)).Validate();
        _ = new TrustedCallerMap(WindowsAccounts);
        var sql = new SqlConnectionStringBuilder(ConnectionString);
        if (DeploymentMode == "UAT")
        {
            if (!sql.InitialCatalog.StartsWith("EQ001_UAT_", StringComparison.Ordinal) && !sql.InitialCatalog.StartsWith("PharmaLIMS_EQ001_UAT_", StringComparison.Ordinal))
                throw new ArgumentException("UAT service must target an explicitly isolated EQ001 UAT database.");
        }
        else if (DeploymentMode != "Production" || !ProductionActivationApproved)
            throw new UnauthorizedAccessException("Production signing is not activated by approved host configuration.");
        _ = new SqlTrustedSignerAccountRepository(ConnectionString);
        _ = new SqlTrustedEquipmentSignatureStore(ConnectionString);
        if (string.IsNullOrWhiteSpace(CertificatePath)) throw new ArgumentException("A service TLS certificate is required.");
    }

    [SupportedOSPlatform("windows")]
    private static void RequirePrivateFile(string path, string serviceSid)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { serviceSid, "S-1-5-18", "S-1-5-32-544" };
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists) throw new FileNotFoundException("Protected service file is missing.");
        foreach (var acl in new FileSystemSecurity[] { file.GetAccessControl(), file.Directory!.GetAccessControl() })
        {
            if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !allowed.Contains(owner.Value))
                throw new UnauthorizedAccessException("Service file ownership is not protected.");
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && rule.FileSystemRights != 0 && !allowed.Contains(rule.IdentityReference.Value))
                    throw new UnauthorizedAccessException("Service configuration/certificate directory must be accessible only to the service account, SYSTEM and Administrators.");
        }
    }

    [SupportedOSPlatform("windows")]
    // Windows Schannel requires a persisted key handle during TLS authentication.
    // Do not use EphemeralKeySet: it can load a PFX yet fail when Schannel uses it.
    // Omit PersistKeySet/Exportable so this import remains temporary and private.
    public X509Certificate2 LoadCertificate() => new(CertificatePath, CertificatePassword, X509KeyStorageFlags.MachineKeySet);
}
