using Microsoft.Data.SqlClient;
using System.IO.Pipes;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace PharmaLIMS.TrustedSigning;

[SupportedOSPlatform("windows")]
public sealed class WindowsTrustedPipeServer
{
    private readonly string _pipeName;
    private readonly X509Certificate2 _certificate;
    private readonly TrustedCallerMap _callers;
    private readonly ITrustedSignerAccountRepository _accounts;
    private readonly ITrustedEquipmentSignatureStore _store;
    public WindowsTrustedPipeServer(string pipeName, X509Certificate2 certificate, TrustedCallerMap callers,
        ITrustedSignerAccountRepository accounts, ITrustedEquipmentSignatureStore store)
    {
        new TrustedPipeClientOptions(pipeName, Convert.ToHexString(certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256))).Validate();
        if (!certificate.HasPrivateKey) throw new ArgumentException("Service TLS certificate has no private key.");
        _pipeName = pipeName; _certificate = certificate; _callers = callers; _accounts = accounts; _store = store;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier serviceSid = identity.User ?? throw new UnauthorizedAccessException("Windows service identity is missing.");
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(serviceSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, TrustedPipeProtocol.MaximumFrameBytes, TrustedPipeProtocol.MaximumFrameBytes, acl);
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try { await ServeAsync(pipe, serviceSid.Value, deadline.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or ArgumentException or UnauthorizedAccessException)
            { /* Invalid/abandoned connections cannot terminate the host or reach a local SQL fallback. */ }
        }
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
        Guid requestId = envelope.Request?.RequestId ?? Guid.Empty;
        TrustedSigningReply reply;
        try
        {
            if (callerSid is null || envelope.Request is null || string.IsNullOrEmpty(envelope.Password) || envelope.Password.Length > 4096)
                throw new UnauthorizedAccessException("Invalid caller or credential.");
            string username = _callers.Resolve(callerSid, serviceSid, envelope.ExpectedUsername);
            var authenticator = new TrustedHostAuthenticator(new KernelBoundCaller(username), _accounts);
            var coordinator = new TrustedSigningCoordinator(authenticator, _store);
            await coordinator.CommitEquipmentSignatureAsync(envelope.Request, envelope.Password, cancellationToken).ConfigureAwait(false);
            reply = new TrustedSigningReply(requestId, true, username, "");
        }
        catch (UnauthorizedAccessException) { reply = new(requestId, false, null, "Denied"); }
        catch (ArgumentException) { reply = new(requestId, false, null, "InvalidRequest"); }
        catch (SqlException ex) when (ex.Number is 56140 or 56141 or 56142 or 2601 or 2627) { reply = new(requestId, false, null, "Conflict"); }
        catch (SqlException) { reply = new(requestId, false, null, "StoreUnavailable"); }
        await TrustedPipeProtocol.WriteAsync(tls, reply, cancellationToken).ConfigureAwait(false);
    }

    private sealed class KernelBoundCaller(string username) : ITrustedCallerIdentity
    { public string GetAuthenticatedUsername() => username; }
}
