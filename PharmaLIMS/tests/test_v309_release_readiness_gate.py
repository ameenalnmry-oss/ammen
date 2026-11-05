from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
GATE = ROOT / "scripts" / "Invoke-V309ReleaseReadinessGate.ps1"
CERT = ROOT / "scripts" / "Test-V309CertificateStoreReadiness.ps1"


def test_release_gate_orchestrates_existing_authoritative_controls() -> None:
    source = GATE.read_text(encoding="utf-8-sig")
    assert "Invoke-ReleaseValidation.ps1" in source
    assert "Invoke-V309RuntimeProvenance.ps1" in source
    assert "Test-V309CertificateStoreReadiness.ps1" in source
    assert "RunDatabaseIntegration" in source
    assert "RunRuntimeSmoke" in source
    assert "V309_RELEASE_READINESS_SUMMARY.json" in source


def test_release_gate_does_not_mutate_runtime_or_uat() -> None:
    source = GATE.read_text(encoding="utf-8-sig")
    forbidden = (
        "Stop-Service",
        "Start-Service",
        "Copy-Item",
        "Remove-Item $ClientRuntime",
        "Remove-Item $ServiceRuntime",
        "UPDATE dbo.",
        "DELETE FROM",
        "INSERT INTO",
    )
    for token in forbidden:
        assert token not in source


def test_certificate_preflight_is_read_only_and_strict() -> None:
    source = CERT.read_text(encoding="utf-8-sig")
    assert "StoreLocation]::LocalMachine" in source
    assert "StoreName]::My" in source
    assert "FindByThumbprint" in source
    assert "HasPrivateKey" in source
    assert "1.3.6.1.5.5.7.3.1" in source
    assert "Server Authentication EKU" in source
    assert "ServerCertificateSha256" in source
    assert "Client TLS pin does not match" in source
    assert "READY_FOR_CONTROLLED_CUTOVER" in source
    assert "Stop-Service" not in source
    assert "Start-Service" not in source
    assert "Set-Content" in source  # evidence only
    assert "hostsettings.json" not in source


def test_release_validation_builds_trusted_host_instead_of_running_library() -> None:
    release = (ROOT / "scripts" / "Invoke-ReleaseValidation.ps1").read_text(encoding="utf-8-sig")
    assert "dotnet run --project tools/PharmaLIMS.TrustedSigning/PharmaLIMS.TrustedSigning.csproj" not in release
    assert "PharmaLIMS.TrustedSigning.Host/PharmaLIMS.TrustedSigning.Host.csproj" in release
    assert "Trusted signing host build" in release


def test_release_gate_and_provenance_are_windows_powershell_51_collection_safe() -> None:
    gate = GATE.read_text(encoding="utf-8-sig")
    provenance = (ROOT / "scripts" / "Invoke-V309RuntimeProvenance.ps1").read_text(encoding="utf-8-sig")
    assert "steps = @($steps)" not in gate
    assert "steps = @($steps | ForEach-Object { $_ })" in gate
    assert "return @($rows)" not in provenance
    assert "return $rows.ToArray()" in provenance


def test_release_gate_cleans_only_generated_source_workspace_artifacts_before_delivery_validation() -> None:
    source = GATE.read_text(encoding="utf-8-sig")
    assert "Clear-ControlledWorkspaceGeneratedArtifacts" in source
    assert "Controlled workspace cleanup" in source
    for generated_name in ("'bin'", "'obj'", "'__pycache__'", "'.pytest_cache'"):
        assert generated_name in source
    assert "$artifactRoot = (Join-Path $root 'artifacts')" in source
    assert "Remove-Item -LiteralPath $directory.FullName -Recurse -Force" in source
    assert "Remove-Item -LiteralPath $ClientRuntime" not in source
    assert "Remove-Item -LiteralPath $ServiceRuntime" not in source
