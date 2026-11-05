from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CERT_PREFLIGHT = ROOT / "scripts/Test-V309CertificateStoreReadiness.ps1"
CERT_CUTOVER = ROOT / "scripts/Invoke-V309CertificateStoreCutover.ps1"
DB_RUNNER = ROOT / "scripts/Invoke-V309DisposableDatabaseIntegration.ps1"
RELEASE_VALIDATION = ROOT / "scripts/Invoke-ReleaseValidation.ps1"

CONFIGURE_ACTIVITY = ROOT / "scripts/Configure-UatActivitySigning.ps1"
DEPLOY_WATER = ROOT / "scripts/Deploy-Eq001WaterAnalysis.ps1"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_certificate_preflight_binds_private_key_acl_to_service_identity() -> None:
    source = read(CERT_PREFLIGHT)
    assert "Resolve-ServiceAccountSid" in source
    assert "Resolve-MachinePrivateKeyPath" in source
    assert "Assert-ServicePrivateKeyReadAccess" in source
    assert "servicePrivateKeyReadAccess = 'PASS'" in source
    assert "LocalMachine" in source
    assert "Server Authentication EKU" in source


def test_certificate_cutover_is_backup_fail_closed_and_scope_limited() -> None:
    source = read(CERT_CUTOVER)
    assert "Test-V309CertificateStoreReadiness.ps1" in source
    assert "Set-JsonPropertyValue $config 'CertificatePath' ''" in source
    assert "Set-JsonPropertyValue $config 'CertificatePassword' ''" in source
    assert "Set-JsonPropertyValue $config 'CertificateThumbprint' $normalized" in source
    assert "Set-JsonPropertyValue $config 'CertificateStoreName' 'My'" in source
    assert "Copy-Item -LiteralPath $ServiceConfig -Destination $serviceConfigBackup" in source
    assert "Restoring the exact protected host configuration" in source
    assert "[IO.File]::WriteAllBytes($ServiceConfig,$bytes)" in source
    assert "Certificate cutover changed a protected non-certificate setting" in source
    assert "No certificate was rotated" not in source  # wording must remain a warning, not a false completion claim
    assert "The legacy PFX file was not deleted and no certificate was rotated." in source
    assert "Get-ProtectedConfigSnapshot" in source
    assert "Get-JsonPropertyValue" in source
    assert "Set-JsonPropertyValue" in source
    assert "EnableWaterAnalysisUat = [bool]$config.EnableWaterAnalysisUat" not in source
    assert "EnableWaterAnalysisUat = [bool]$verify.EnableWaterAnalysisUat" not in source


def test_external_database_integration_requires_disposable_acknowledgement() -> None:
    source = read(RELEASE_VALIDATION)
    assert "PHARMALIMS_TEST_SQL_DISPOSABLE" in source
    assert "PHARMALIMS_TEST_SQL_DISPOSABLE=YES" in source
    assert "master (or omit Initial Catalog)" in source
    assert "PHARMALIMS_CREATE_CLEAN_UAT=YES is not permitted" in source
    assert "retained Windows runtime probe database" in source


def test_disposable_runner_defaults_to_localdb_and_does_not_inherit_external_sql() -> None:
    source = read(DB_RUNNER)
    assert "$env:PHARMALIMS_TEST_MASTER_CONNECTION_STRING = $null" in source
    assert "Using Windows LocalDB MSSQLLocalDB" in source
    assert "AcknowledgeCreatesAndDropsDatabases" in source
    assert "External master connection must target master" in source
    assert "$env:PHARMALIMS_CREATE_CLEAN_UAT = $null" in source
    assert "$env:PHARMALIMS_WINDOWS_RUNTIME_PROBE_DATABASE = $null" in source


def test_operational_uat_scripts_accept_certificate_store_mode() -> None:
    activity = read(CONFIGURE_ACTIVITY)
    water = read(DEPLOY_WATER)
    for source in (activity, water):
        assert "CertificateThumbprint" in source
        assert "LocalMachine" in source
        assert "CertificateStoreName" in source
        assert "CertificatePassword must be empty in certificate-store mode." in source
    assert "Get-ConfiguredTrustedSigningCertificate" in activity
    assert "Get-ConfiguredTrustedSigningCertificate" in water
    assert "$certificateFileMode" in water
    assert "$certificateStoreMode" in water

def test_database_integration_executes_controlled_go_scripts_as_same_session_batches() -> None:
    source = read(ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs")
    migration = read(ROOT / "Database/Migrations/20261030_002_Trusted_Activity_Snapshot_Bridge.sql")

    assert "\nGO\n" in migration.replace("\r\n", "\n")
    assert "ExecuteControlledSqlScriptAsync(connectionString, sql, timeoutSeconds: 300)" in source
    assert "SplitControlledSqlBatches(sql)" in source
    assert 'line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase)' in source
    assert "await using SqlConnection connection = new(connectionString);" in source
    assert "await connection.OpenAsync();" in source
    assert "new(batch, connection)" in source
    assert "await command.ExecuteNonQueryAsync();" in source

def test_database_integration_bootstraps_trusted_signer_only_in_disposable_database() -> None:
    source = read(ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs")
    assert "ProvisionDisposableTrustedEquipmentSignerPrincipalAsync" in source
    assert "^PharmaLIMS_Integration_[0-9a-f]{12}$" in source
    assert "Disposable trusted-signer provisioning refused outside an integration database." in source
    assert "CREATE USER [PharmaLIMS_TrustedEquipmentSigner] FOR LOGIN" in source
    assert "CREATE USER [PharmaLIMS_TrustedEquipmentSigner] WITHOUT LOGIN" in source
    assert "IS_SRVROLEMEMBER(N'sysadmin')" in source
    assert "if (!retainCleanUat && !windowsRuntimeProbe)" in source

def test_runtime_migrator_executes_controlled_go_scripts_as_same_transaction_batches() -> None:
    source = read(ROOT / "Infrastructure/StartupDatabaseMigrator.cs")
    migration = read(ROOT / "Database/Migrations/20261030_002_Trusted_Activity_Snapshot_Bridge.sql")

    assert "\nGO\n" in migration.replace("\r\n", "\n")
    assert "foreach (string batchSql in SplitControlledSqlBatches(migrationSql))" in source
    assert 'line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase)' in source
    assert "new SqlCommand(batchSql, connection, transaction)" in source
    assert "await apply.ExecuteNonQueryAsync().ConfigureAwait(false);" in source

def test_runtime_smoke_bootstraps_trusted_signer_before_runtime_migrations() -> None:
    source = read(ROOT / "tests/PharmaLIMS.RuntimeSmoke/Program.cs")

    provision_call = "await ProvisionDisposableTrustedEquipmentSignerPrincipalAsync("
    migrate_call = "await migrator.ApplyRequiredUpdatesAsync().ConfigureAwait(false);"

    assert provision_call in source
    assert migrate_call in source
    assert source.index(provision_call) < source.index(migrate_call)
    assert "^PharmaLIMS_RuntimeSmoke_[0-9a-f]{12}$" in source
    assert "Runtime-smoke trusted-signer provisioning refused outside a disposable runtime-smoke database." in source
    assert "CREATE USER [PharmaLIMS_TrustedEquipmentSigner] FOR LOGIN" in source
    assert "CREATE USER [PharmaLIMS_TrustedEquipmentSigner] WITHOUT LOGIN" in source
    assert "Runtime-smoke login is already mapped to another database principal." in source

