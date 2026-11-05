from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/Invoke-V309RuntimeProvenance.ps1"


def text() -> str:
    return SCRIPT.read_text(encoding="utf-8-sig")


def test_runtime_provenance_is_read_only_and_hash_based() -> None:
    source = text()
    assert "Get-FileHash" in source
    assert "SHA256" in source
    assert "Compare-Tree" in source
    assert "HASH_MISMATCH" in source
    assert "MISSING_RUNTIME" in source
    assert "UNEXPECTED_RUNTIME_CODE" in source
    assert "Copy-Item" not in source
    assert "Stop-Service" not in source
    assert "Start-Service" not in source


def test_runtime_provenance_binds_to_controlled_source_and_toolchain() -> None:
    source = text()
    assert "Test-SourceManifest.ps1" in source
    assert "sourceManifestSha256" in source
    assert "applicationVersion" in source
    assert "dotnet --version" in source
    assert "gitCommit" in source
    assert "--no-incremental" in source
    assert "PharmaLIMS.TrustedSigning.Host.csproj" in source


def test_runtime_provenance_compares_client_and_service_without_site_secrets() -> None:
    source = text()
    assert "client-v309" in source
    assert "service" in source.lower()
    assert "hostsettings.json" in source
    assert "appsettings.json" in source
    for suffix in (".pfx", ".p12", ".key", ".cer", ".crt"):
        assert suffix in source


def test_runtime_provenance_uses_isolated_staging_and_fails_closed_on_code_difference() -> None:
    source = text()
    assert "controlled-build-$stamp" in source
    assert "-o $ClientBuildDirectory" in source
    assert "-o $ServiceBuildDirectory" in source
    assert "blockingDifferenceCount" in source
    assert "DEBUG_SYMBOL_HASH_MISMATCH" in source
    assert "if ($blockingFailures.Count -gt 0)" in source
    assert "Installed runtime does not match the controlled isolated Release staging artifacts" in source
    assert "PASS: installed client/service executable code matches the controlled isolated Release staging artifacts." in source

def test_library_deps_file_is_not_treated_as_runtime_blocker() -> None:
    source = text()
    assert "PharmaLIMS.TrustedSigning.deps.json" in source
    assert "class-library dependency" in source

