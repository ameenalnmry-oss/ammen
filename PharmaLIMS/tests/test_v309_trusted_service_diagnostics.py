from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SERVER = ROOT / "tools/PharmaLIMS.TrustedSigning/WindowsTrustedPipeServer.cs"
HOST = ROOT / "tools/PharmaLIMS.TrustedSigning.Host/Program.cs"


def test_trusted_pipe_logs_internal_category_with_request_correlation_only():
    server = SERVER.read_text(encoding="utf-8-sig")
    assert "Action<Guid,string>? _diagnostic" in server
    assert 'Diagnostic(request.RequestId, "ActivitySnapshot.IdentityDenied")' in server
    assert '"Signing.IdentityOrCredentialDenied"' in server
    assert "Diagnostic(requestId, category)" in server
    assert 'Diagnostic(requestId, "Signing.StoreConflict")' in server
    assert 'Diagnostic(requestId, "Signing.StoreUnavailable")' in server


def test_host_persists_correlation_and_category_without_credentials():
    host = HOST.read_text(encoding="utf-8-sig")
    assert "TrustedServiceDiagnosticSink.Log" in host
    assert "Request='{correlation}', Category='{category}'" in host
    sink = host[host.index("static class TrustedServiceDiagnosticSink"):]
    assert "Password" not in sink
    assert "Username" not in sink
    assert "ConnectionString" not in sink


def test_client_facing_fail_closed_codes_remain_generic():
    server = SERVER.read_text(encoding="utf-8-sig")
    assert 'new(request.RequestId, false, "Denied", null)' in server
    assert 'new(requestId, false, null, "Denied")' in server
    assert 'new(requestId, false, null, "Conflict")' in server
    assert 'new(requestId, false, null, "StoreUnavailable")' in server
