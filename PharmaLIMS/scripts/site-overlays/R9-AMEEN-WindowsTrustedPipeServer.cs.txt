using Microsoft.Data.SqlClient;
using System.IO.Pipes;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;

namespace PharmaLIMS.TrustedSigning;

[SupportedOSPlatform("windows")]
public sealed class WindowsTrustedPipeServer
{
    private readonly string _pipeName;
    private readonly X509Certificate2 _certificate;
    private readonly TrustedCallerMap _callers;
    private readonly ITrustedSignerAccountRepository _accounts;
    private readonly ITrustedEquipmentSignatureStore _store;
    private readonly ITrustedApplicationEndpoint? _application;
    private readonly TrustedActivitySigningCoordinator? _activities;
    private readonly Action<Guid,string>? _diagnostic;
    public WindowsTrustedPipeServer(string pipeName, X509Certificate2 certificate, TrustedCallerMap callers,
        ITrustedSignerAccountRepository accounts, ITrustedEquipmentSignatureStore store, ITrustedApplicationEndpoint? application = null,
        TrustedActivitySigningCoordinator? activities = null, Action<Guid,string>? diagnostic = null)
    {
        new TrustedPipeClientOptions(pipeName, Convert.ToHexString(certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256))).Validate();
        if (!certificate.HasPrivateKey) throw new ArgumentException("Service TLS certificate has no private key.");
        _pipeName = pipeName; _certificate = certificate; _callers = callers; _accounts = accounts; _store = store; _application = application;
        _activities = activities; _diagnostic = diagnostic;
    }

    public async Task RunAsync(CancellationToken cancellationToken, TaskCompletionSource? readiness = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier serviceSid = identity.User ?? throw new UnauthorizedAccessException("Windows service identity is missing.");
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(serviceSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        foreach (string sid in _callers.AllowedWindowsSids)
        {
            if (string.Equals(sid, serviceSid.Value, StringComparison.OrdinalIgnoreCase)) continue;
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid),
                PipeAccessRights.ReadWrite & ~PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        }
        var pipes = new List<NamedPipeServerStream>();
        try
        {
            // Keep the first handle alive until shutdown. Recreating it between callers
            // would leave a name-squatting gap; later handles must omit FirstPipeInstance.
            for (int i = 0; i < TrustedPipeConnectionPool.MaximumConnections; i++)
                pipes.Add(NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut,
                    TrustedPipeConnectionPool.MaximumConnections, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | (i == 0 ? PipeOptions.FirstPipeInstance : PipeOptions.None),
                    TrustedPipeProtocol.MaximumFrameBytes, TrustedPipeProtocol.MaximumFrameBytes, acl));
            await TrustedPipeConnectionPool.RunAsync(pipes.ToArray(),
                async (pipe, token) =>
                {
                    try { await ServeAsync(pipe, serviceSid.Value, token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException)
                    {
                        Diagnostic(Guid.Empty, "TransportRejected." + ex.GetType().Name);
                        throw;
                    }
                }, cancellationToken, readiness).ConfigureAwait(false);
        }
        finally { foreach (var pipe in pipes) pipe.Dispose(); }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, string serviceSid, CancellationToken cancellationToken)
    {
        await using var tls = new SslStream(pipe, true);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
            ServerCertificate = _certificate, ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, cancellationToken).ConfigureAwait(false);
        TrustedSigningEnvelope envelope = await TrustedPipeProtocol.ReadAsync<TrustedSigningEnvelope>(tls, cancellationToken).ConfigureAwait(false);
        string? callerSid = null;
        pipe.RunAsClient(() => { using WindowsIdentity caller = WindowsIdentity.GetCurrent(true) ?? throw new UnauthorizedAccessException("No impersonated Windows caller."); callerSid = caller.User?.Value; });
        if (envelope.Authentication is not null)
        {
            var request = envelope.Authentication;
            TrustedSigningReply result;
            if (!_callers.IsAllowedCaller(callerSid, serviceSid) || _application is null || envelope.Request is not null || envelope.Activity is not null || envelope.ActivitySnapshot is not null)
            {
                Diagnostic(request.RequestId, "ApplicationEnvelopeDenied");
                result = new(request.RequestId, false, null, "Denied");
            }
            else
            {
                result = await _application.HandleAsync(callerSid!, request, cancellationToken).ConfigureAwait(false);
                if (!result.Success) Diagnostic(request.RequestId, "Application." + result.ErrorCode);
            }
            await TrustedPipeProtocol.WriteAsync(tls, result, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (envelope.ActivitySnapshot is not null)
        {
            var request = envelope.ActivitySnapshot;
            ActivitySnapshotReply result;
            try
            {
                if (callerSid is null || callerSid == serviceSid || _activities is null ||
                    envelope.Request is not null || envelope.Authentication is not null ||
                    envelope.Activity is not null || !string.IsNullOrEmpty(envelope.Password))
                    throw new UnauthorizedAccessException("Invalid activity snapshot caller.");

                string username = _callers.Resolve(callerSid, serviceSid, envelope.ExpectedUsername);
                if (string.IsNullOrWhiteSpace(username))
                    throw new UnauthorizedAccessException("Invalid activity snapshot identity.");

                ActivitySnapshot snapshot =
                    await _activities.ReadSnapshotAsync(request, cancellationToken).ConfigureAwait(false);
                result = new ActivitySnapshotReply(request.RequestId, true, "", snapshot);
            }
            catch (UnauthorizedAccessException)
            {
                Diagnostic(request.RequestId, "ActivitySnapshot.IdentityDenied");
                result = new(request.RequestId, false, "Denied", null);
            }
            catch (ArgumentException)
            {
                Diagnostic(request.RequestId, "ActivitySnapshot.InvalidRequest");
                result = new(request.RequestId, false, "InvalidRequest", null);
            }
            catch (SqlException ex) when (ex.Number == 56765)
            {
                Diagnostic(request.RequestId, "ActivitySnapshot.Conflict");
                result = new(request.RequestId, false, "Conflict", null);
            }
            catch (SqlException)
            {
                Diagnostic(request.RequestId, "ActivitySnapshot.StoreUnavailable");
                result = new(request.RequestId, false, "StoreUnavailable", null);
            }
            catch (InvalidOperationException)
            {
                Diagnostic(request.RequestId, "ActivitySnapshot.InvalidStoreResponse");
                result = new(request.RequestId, false, "StoreUnavailable", null);
            }

            await TrustedPipeProtocol.WriteAsync(tls, result, cancellationToken).ConfigureAwait(false);
            return;
        }
        Guid requestId = envelope.Request?.RequestId ?? envelope.Activity?.RequestId ?? Guid.Empty;
        TrustedSigningReply reply;
        try
        {
            if (callerSid is null || (envelope.Request is null) == (envelope.Activity is null) ||
                string.IsNullOrEmpty(envelope.Password) || envelope.Password.Length > 4096)
                throw new UnauthorizedAccessException("Invalid caller or credential.");
            string username = _callers.Resolve(callerSid, serviceSid, envelope.ExpectedUsername);
            if (envelope.Activity is not null)
            {
                if (_activities is null || _application is null)
                    throw new UnauthorizedAccessException("Activity signing is not configured.");

                // Activity signing must use the same canonical credential verifier as
                // Login/Change Password. The Windows caller is resolved first, then the
                // trusted application endpoint reauthenticates that exact PharmaLIMS
                // identity. No second password-verification implementation is used here.
                VerifiedActivitySigner signer = await ReauthenticateActivityAsync(
                    callerSid!, username, envelope.Password, cancellationToken).ConfigureAwait(false);
                await _activities.CommitVerifiedAsync(
                    envelope.Activity, signer, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var authenticator = new TrustedHostAuthenticator(new KernelBoundCaller(username), _accounts);
                var coordinator = new TrustedSigningCoordinator(authenticator, _store);
                await coordinator.CommitEquipmentSignatureAsync(envelope.Request!, envelope.Password, cancellationToken).ConfigureAwait(false);
            }
            reply = new TrustedSigningReply(requestId, true, username, "");
        }
        catch (UnauthorizedAccessException)
        {
            string category = callerSid == serviceSid ? "Signing.SameAccountDenied" : "Signing.IdentityOrCredentialDenied";
            Diagnostic(requestId, category);
            reply = new(requestId, false, null, callerSid == serviceSid ? "SameAccountDenied" : "Denied");
        }
        catch (ArgumentException)
        {
            Diagnostic(requestId, "Signing.InvalidRequest");
            reply = new(requestId, false, null, "InvalidRequest");
        }
        catch (SqlException ex) when (ex.Number is 56139 or 56750 or 56752 or 56764)
        {
            Diagnostic(requestId, "Signing.StoreAuthorizationRejected");
            reply = new(requestId, false, null, "Denied");
        }
        catch (SqlException ex) when (ex.Number is 56140 or 56141 or 56142 or 2601 or 2627 or 56513 or 56765)
        {
            Diagnostic(requestId, "Signing.StoreConflict");
            reply = new(requestId, false, null, "Conflict");
        }
        catch (SqlException)
        {
            Diagnostic(requestId, "Signing.StoreUnavailable");
            reply = new(requestId, false, null, "StoreUnavailable");
        }
        await TrustedPipeProtocol.WriteAsync(tls, reply, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VerifiedActivitySigner> ReauthenticateActivityAsync(
        string callerSid, string username, string password, CancellationToken cancellationToken)
    {
        TrustedSigningReply authentication = await _application!.HandleAsync(
            callerSid,
            new TrustedApplicationRequest(
                Guid.NewGuid(),
                "ActivityReauthenticate",
                username,
                password),
            cancellationToken).ConfigureAwait(false);

        if (!authentication.Success ||
            !string.Equals(authentication.SignedBy, username, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authentication.UserJson))
        {
            throw new UnauthorizedAccessException("Canonical activity reauthentication was rejected.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(authentication.UserJson);
            JsonElement user = document.RootElement;

            string verifiedUsername = user.GetProperty("Username").GetString() ?? string.Empty;
            bool isActive = user.GetProperty("IsActive").GetBoolean();
            bool isLocked = user.GetProperty("IsLocked").GetBoolean();
            bool mustChangePassword = user.GetProperty("MustChangePassword").GetBoolean();
            bool canReviewResults = user.GetProperty("CanReviewResults").GetBoolean();
            bool canApproveResults = user.GetProperty("CanApproveResults").GetBoolean();
            byte[] authenticationRowVersion =
                user.GetProperty("AuthenticationRowVersion").GetBytesFromBase64();

            if (!verifiedUsername.Equals(username, StringComparison.OrdinalIgnoreCase) ||
                authenticationRowVersion.Length != 8)
            {
                throw new UnauthorizedAccessException("Canonical activity identity did not match the Windows-bound account.");
            }

            return new VerifiedActivitySigner(
                verifiedUsername,
                isActive,
                isLocked,
                mustChangePassword,
                canReviewResults,
                canApproveResults,
                authenticationRowVersion);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new UnauthorizedAccessException(
                "Canonical activity reauthentication response was invalid.", ex);
        }
    }

    private void Diagnostic(Guid requestId, string category)
    {
        if (_diagnostic is null) return;
        try { _diagnostic(requestId, category); }
        catch { /* Diagnostics must never alter the fail-closed signing result. */ }
    }

    private sealed class KernelBoundCaller(string username) : ITrustedCallerIdentity
    { public string GetAuthenticatedUsername() => username; }
}
