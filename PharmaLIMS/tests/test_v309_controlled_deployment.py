from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "Deploy-V309ControlledReleaseArtifacts.ps1"


def source() -> str:
    return SCRIPT.read_text(encoding="utf-8-sig")


def test_controlled_deployment_is_bound_to_provenance_and_exact_core_artifacts() -> None:
    text = source()
    assert "V309_RUNTIME_PROVENANCE_" in text
    assert "SOURCE_MANIFEST_SHA256.txt" in text
    assert "sourceManifestSha256" in text
    for artifact in (
        "client|PharmaLIMS.dll",
        "client|PharmaLIMS.TrustedSigning.dll",
        "service|PharmaLIMS.dll",
        "service|PharmaLIMS.TrustedSigning.dll",
        "service|PharmaLIMS.TrustedSigning.Host.dll",
    ):
        assert artifact in text
    assert "exactly $($expected.Count) known executable hash mismatches" in text


def test_controlled_deployment_checks_build_and_runtime_hashes_before_copy() -> None:
    text = source()
    assert "Get-FileHash" in text
    assert "Controlled build artifact changed after provenance capture" in text
    assert "Installed runtime changed after provenance capture" in text
    assert "Post-copy SHA-256 verification failed" in text


def test_controlled_deployment_has_backup_rollback_and_service_recovery() -> None:
    text = source()
    assert "EQ001-UAT-v309-controlled-release-" in text
    assert "Stop-Service" in text
    assert "Start-Service" in text
    assert "Restoring backed-up runtime artifacts" in text
    assert "ROLLED_BACK_OR_FAILED" in text
    assert "V309_CONTROLLED_DEPLOYMENT_" in text


def test_controlled_deployment_does_not_change_sql_config_or_activity_state() -> None:
    text = source()
    forbidden = (
        "UPDATE dbo.",
        "DELETE FROM",
        "INSERT INTO",
        "ALTER TABLE",
        "hostsettings.json",
        "appsettings.json",
        "ActivityStatus",
        "MicroEquipmentActivities",
        "PasswordHash",
        "PasswordSalt",
    )
    for token in forbidden:
        assert token not in text
