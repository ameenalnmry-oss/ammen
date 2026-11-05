from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]

class V308ActivitySigningSplit(unittest.TestCase):
    def test_activity_policy_accepts_main_uat_without_relaxing_request_shape(self):
        source = (ROOT / "tools/PharmaLIMS.TrustedSigning/TrustedActivitySigning.cs").read_text(encoding="utf-8-sig")
        self.assertIn('databaseName.StartsWith("PharmaLIMS_UAT_"', source)
        self.assertIn("ActivitySigningDatabasePolicy.IsAllowedUatDatabase(request.DatabaseName)", source)
        self.assertIn('request.Stage is not ("Reviewed" or "Approved")', source)
        self.assertIn("request.ExpectedVersion is not { Length: 8 }", source)

    def test_activity_store_uses_same_database_policy_and_exact_request_database_match(self):
        source = (ROOT / "tools/PharmaLIMS.TrustedSigning/SqlTrustedActivitySignatureStore.cs").read_text(encoding="utf-8-sig")
        self.assertIn("ActivitySigningDatabasePolicy.IsAllowedUatDatabase(settings.InitialCatalog)", source)
        self.assertIn("request.DatabaseName.Equals(_databaseName", source)
        self.assertIn('"dbo.MicroEquipment_SignActivityStage"', source)
        self.assertIn('"dbo.EQ001_CommitActivitySignature" : "dbo.MicroEquipment_SignActivityStage"', source)
        self.assertIn('_databaseName.StartsWith("EQ001_UAT_", StringComparison.Ordinal)', source)
        self.assertIn('_databaseName.StartsWith("PharmaLIMS_EQ001_UAT_", StringComparison.Ordinal)', source)
        self.assertIn('"@AuthenticationVersion"', source)
        self.assertIn('"@RequestId"', source)

    def test_host_keeps_primary_eq001_connection_and_splits_only_activity_signing(self):
        settings = (ROOT / "tools/PharmaLIMS.TrustedSigning/TrustedHostSettings.cs").read_text(encoding="utf-8-sig")
        program = (ROOT / "tools/PharmaLIMS.TrustedSigning.Host/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn("ActivitySigningConnectionString", settings)
        self.assertIn("GetActivitySigningConnectionString", settings)
        self.assertIn("new SqlTrustedSignerAccountRepository(settings.ConnectionString)", program)
        self.assertIn("new SqlTrustedEquipmentSignatureStore(settings.ConnectionString)", program)
        self.assertIn("new TrustedApplicationEndpoint(settings.ConnectionString", program)
        self.assertIn("new TrustedActivityAuthenticator(settings.ConnectionString)", program)
        self.assertIn("new SqlTrustedActivitySignatureStore(activityConnection)", program)

    def test_activity_split_requires_same_sql_server_windows_auth_and_validated_tls(self):
        source = (ROOT / "tools/PharmaLIMS.TrustedSigning/TrustedHostSettings.cs").read_text(encoding="utf-8-sig")
        self.assertIn("activitySql.DataSource, sql.DataSource", source)
        self.assertIn("!activitySql.IntegratedSecurity", source)
        self.assertIn("activitySql.TrustServerCertificate", source)
        self.assertIn("activitySql.AttachDBFilename.Length != 0", source)

    def test_debug_build_prefers_local_development_config(self):
        csproj = (ROOT / "PharmaLIMS.csproj").read_text(encoding="utf-8-sig")
        self.assertIn('appsettings.Development.json', csproj)
        self.assertIn("Exists('$(MSBuildProjectDirectory)\\appsettings.Development.json')", csproj)
        self.assertIn("!Exists('$(MSBuildProjectDirectory)\\appsettings.Development.json')", csproj)

    def test_activity_signing_can_be_enabled_without_switching_global_trusted_transport(self):
        client = (ROOT / "Services/TrustedEquipmentSigningClient.cs").read_text(encoding="utf-8-sig")
        lab = (ROOT / "LabResources.xaml.cs").read_text(encoding="utf-8-sig")
        setup = (ROOT / "scripts/Configure-UatActivitySigning.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("ActivitySigningEnabled", client)
        self.assertIn("CreateActivityFromConfiguration", client)
        self.assertIn("CreateActivityFromConfiguration()", lab)
        self.assertIn("Enabled = $false", setup)
        self.assertIn("ActivitySigningEnabled = $true", setup)

    def test_setup_script_preserves_primary_database_and_configures_activity_database(self):
        source = (ROOT / "scripts/Configure-UatActivitySigning.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("ActivitySigningConnectionString", source)
        self.assertIn("PharmaLIMS_UAT_20261001", source)
        self.assertIn("Trusted service primary DB preserved", source)
        self.assertIn("MicroEquipment_SignActivityStage", source)
        self.assertIn("PharmaLIMS_TrustedEquipmentSigner", source)
        self.assertIn("ServerCertificateSha256", source)
        self.assertNotIn("ProductionActivationApproved = $true", source)

    def test_setup_script_is_windows_powershell_compatible(self):
        source = (ROOT / "scripts/Configure-UatActivitySigning.ps1").read_text(encoding="utf-8-sig")
        self.assertNotRegex(source, r"(?m)^\s*\$Host\s*=")
        self.assertNotIn("$activity.InitialCatalog", source)
        self.assertNotIn("$activity.ApplicationName", source)
        self.assertIn("$activity['Initial Catalog'] = $TargetDatabase", source)
        self.assertIn("$activity['Application Name'] = 'PharmaLIMS.TrustedActivitySigning'", source)

    def test_setup_script_does_not_redeclare_service_login_parameter(self):
        source = (ROOT / "scripts/Configure-UatActivitySigning.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("DECLARE @serviceLoginName sysname=@ServiceLogin;", source)
        self.assertNotIn("DECLARE @serviceLogin sysname=@ServiceLogin;", source)
        self.assertIn("SUSER_ID(@serviceLoginName)", source)

if __name__ == "__main__":
    unittest.main()
