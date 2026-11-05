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
    public string ActivitySigningConnectionString { get; set; } = "";
    public string CertificatePath { get; set; } = "";
    public string CertificatePassword { get; set; } = "";
    public string CertificateThumbprint { get; set; } = "";
    public string CertificateStoreName { get; set; } = "My";
    public string DeploymentMode { get; set; } = "UAT";
    public bool ProductionActivationApproved { get; set; }
    public bool EnableWaterRegistrationUat { get; set; }
    public bool EnableWaterAnalysisUat { get; set; }
    public bool EnablePrmRegistrationUat { get; set; }
    public bool EnableEquipmentDraftUat { get; set; }
    public bool EnableEquipmentLinkUat { get; set; }
    public bool EnableEquipmentConfirmUat { get; set; }
    public Dictionary<string,string> WindowsAccounts { get; set; } = new();

    [SupportedOSPlatform("windows")]
    public static TrustedHostSettings Load(string path)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("Service account is missing.");
        RequirePrivateFile(path, sid);
        var settings = JsonSerializer.Deserialize<TrustedHostSettings>(File.ReadAllText(path), new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new ArgumentException("Service configuration is missing.");
        settings.Validate();
        if (settings.UsesCertificateFile)
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
        if (EnableWaterRegistrationUat)
        {
            if (DeploymentMode != "UAT" || ProductionActivationApproved)
                throw new UnauthorizedAccessException("Water registration is isolated UAT only.");
            WaterRegistrationPolicy.RequireTarget(ConnectionString);
        }
        if (EnableWaterAnalysisUat)
        {
            if (DeploymentMode != "UAT" || ProductionActivationApproved)
                throw new UnauthorizedAccessException("Water analysis is isolated UAT only.");
            if (!sql.IntegratedSecurity || sql.Encrypt == SqlConnectionEncryptOption.Optional || sql.TrustServerCertificate)
                throw new ArgumentException("Water analysis requires an encrypted, certificate-validated Windows service connection.");
        }
        if (EnablePrmRegistrationUat)
        {
            if (DeploymentMode != "UAT" || ProductionActivationApproved)
                throw new UnauthorizedAccessException("PRM registration is isolated UAT only.");
            PrmRegistrationPolicy.RequireTarget(ConnectionString);
        }
        if (EnableEquipmentConfirmUat && !EnableEquipmentLinkUat)
            throw new UnauthorizedAccessException("Equipment confirmation requires a validated link boundary.");
        if (EnableEquipmentLinkUat && !EnableEquipmentDraftUat)
            throw new UnauthorizedAccessException("Equipment link requires the validated equipment draft boundary.");
        if (EnableEquipmentDraftUat)
        {
            if (DeploymentMode != "UAT" || ProductionActivationApproved)
                throw new UnauthorizedAccessException("Equipment draft writing is isolated UAT only.");
            EquipmentDraftContract.ValidateTarget(ConnectionString);
            if (!sql.IntegratedSecurity || sql.Encrypt == SqlConnectionEncryptOption.Optional || sql.TrustServerCertificate)
                throw new ArgumentException("Equipment draft requires certificate-validated Windows SQL service connection.");
        }
        _ = new SqlTrustedEquipmentSignatureStore(ConnectionString);

        string activityConnection = GetActivitySigningConnectionString();
        var activitySql = new SqlConnectionStringBuilder(activityConnection);
        if (DeploymentMode == "UAT")
        {
            if (!ActivitySigningDatabasePolicy.IsAllowedUatDatabase(activitySql.InitialCatalog))
                throw new ArgumentException("Activity signing must target an explicitly isolated UAT database.");
            if (!string.Equals(activitySql.DataSource, sql.DataSource, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Activity signing must remain on the same controlled SQL Server as the trusted host.");
            if (!activitySql.IntegratedSecurity ||
                (activitySql.Encrypt != SqlConnectionEncryptOption.Mandatory &&
                 activitySql.Encrypt != SqlConnectionEncryptOption.Strict) ||
                activitySql.TrustServerCertificate ||
                activitySql.AttachDBFilename.Length != 0)
                throw new ArgumentException("Activity signing requires Windows authentication and server-validated TLS.");
            _ = new SqlTrustedActivitySignatureStore(activityConnection);
        }

        bool certificateFile = UsesCertificateFile;
        bool certificateStore = UsesCertificateStore;
        if (certificateFile == certificateStore)
            throw new ArgumentException("Configure exactly one service TLS certificate source: CertificatePath or CertificateThumbprint.");

        if (certificateStore)
        {
            string thumbprint = NormalizeThumbprint(CertificateThumbprint);
            if (thumbprint.Length != 40 || !thumbprint.All(Uri.IsHexDigit))
                throw new ArgumentException("CertificateThumbprint must be a 40-character SHA-1 Windows certificate thumbprint.");
            if (!CertificateStoreName.Equals("My", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Trusted signing certificates must be loaded from LocalMachine\\My.");
            if (!string.IsNullOrEmpty(CertificatePassword))
                throw new ArgumentException("CertificatePassword must be empty when CertificateThumbprint is configured.");
        }
    }

    private bool UsesCertificateFile => !string.IsNullOrWhiteSpace(CertificatePath);
    private bool UsesCertificateStore => !string.IsNullOrWhiteSpace(CertificateThumbprint);

    private static string NormalizeThumbprint(string? thumbprint) =>
        new string((thumbprint ?? string.Empty).Where(value => !char.IsWhiteSpace(value)).ToArray());

    public string GetActivitySigningConnectionString() =>
        string.IsNullOrWhiteSpace(ActivitySigningConnectionString)
            ? ConnectionString
            : ActivitySigningConnectionString.Trim();

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
    public X509Certificate2 LoadCertificate()
    {
        if (UsesCertificateStore)
        {
            string thumbprint = NormalizeThumbprint(CertificateThumbprint);
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            X509Certificate2Collection matches = store.Certificates.Find(
                X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            if (matches.Count != 1)
                throw new InvalidOperationException("The configured trusted-signing certificate thumbprint was not found exactly once in LocalMachine\\My.");

            var certificate = new X509Certificate2(matches[0]);
            DateTime now = DateTime.UtcNow;
            if (!certificate.HasPrivateKey ||
                now < certificate.NotBefore.ToUniversalTime() ||
                now > certificate.NotAfter.ToUniversalTime())
            {
                certificate.Dispose();
                throw new InvalidOperationException("The configured trusted-signing certificate is missing its private key or is outside its validity period.");
            }
            return certificate;
        }

        // Legacy compatibility for controlled cutover only. Windows Schannel requires
        // a persisted machine key handle during TLS authentication. New deployments
        // should use CertificateThumbprint so no PFX password is stored in JSON.
        return new X509Certificate2(
            CertificatePath,
            CertificatePassword,
            X509KeyStorageFlags.MachineKeySet);
    }
}
