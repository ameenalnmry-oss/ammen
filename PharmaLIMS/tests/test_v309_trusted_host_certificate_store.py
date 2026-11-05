from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SETTINGS = ROOT / "tools/PharmaLIMS.TrustedSigning/TrustedHostSettings.cs"


def text() -> str:
    return SETTINGS.read_text(encoding="utf-8-sig")


def test_trusted_host_supports_localmachine_store_without_json_password() -> None:
    source = text()
    assert "CertificateThumbprint" in source
    assert 'CertificateStoreName { get; set; } = "My"' in source
    assert "StoreLocation.LocalMachine" in source
    assert "StoreName.My" in source
    assert "FindByThumbprint" in source
    assert "CertificatePassword must be empty when CertificateThumbprint is configured." in source


def test_certificate_source_is_exclusive_and_thumbprint_is_strict() -> None:
    source = text()
    assert "if (certificateFile == certificateStore)" in source
    assert "Configure exactly one service TLS certificate source" in source
    assert "thumbprint.Length != 40" in source
    assert "Uri.IsHexDigit" in source
    assert 'CertificateStoreName.Equals("My"' in source


def test_store_certificate_requires_private_key_and_validity() -> None:
    source = text()
    assert "!certificate.HasPrivateKey" in source
    assert "certificate.NotBefore.ToUniversalTime()" in source
    assert "certificate.NotAfter.ToUniversalTime()" in source


def test_legacy_pfx_is_retained_only_for_controlled_cutover() -> None:
    source = text()
    assert "Legacy compatibility for controlled cutover only" in source
    assert "New deployments" in source
    assert "CertificatePath" in source
    assert "CertificatePassword" in source
