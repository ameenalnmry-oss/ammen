using System.IO.Pipes;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace PharmaLIMS.TrustedSigning;

[SupportedOSPlatform("windows")]
public sealed class WindowsTrustedPipeClient
{
    private readonly TrustedPipeClientOptions _options;
    public WindowsTrustedPipeClient(TrustedPipeClientOptions options) { options.Validate(); _options = options; }

    public async Task CommitAsync(EquipmentSigningRequest request, string expectedUsername, string password, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Trusted equipment signing requires Windows.");
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(expectedUsername) || expectedUsername.Length > 100 || string.IsNullOrEmpty(password) || password.Length > 4096)
            throw new ArgumentException("The active account and a fresh password are required.");
        TrustedSigningReply reply = await ExchangeAsync(new(request, expectedUsername, password), request.RequestId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(reply.SignedBy, expectedUsername, StringComparison.OrdinalIgnoreCase)) throw new SecurityException("Trusted signing identity does not match the active account.");
    }

    public Task<TrustedSigningReply> ApplicationAsync(TrustedApplicationRequest request, CancellationToken cancellationToken = default) =>
        ExchangeAsync(new(null, "", "", request), request.RequestId, cancellationToken);

    public async Task CommitActivityAsync(ActivitySigningRequest request, string expectedUsername, string password,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Trusted activity signing requires Windows.");
        TrustedActivitySigningCoordinator.ValidateShape(request, DateTimeOffset.UtcNow);
        if (string.IsNullOrWhiteSpace(expectedUsername) || expectedUsername.Length > 100 ||
            string.IsNullOrEmpty(password) || password.Length > 4096)
            throw new ArgumentException("The active account and fresh password are required.");
        var reply = await ExchangeAsync(new(null, expectedUsername, password, Activity:request), request.RequestId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(reply.SignedBy, expectedUsername, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Trusted activity signer differs from the active account.");
    }

    private async Task<TrustedSigningReply> ExchangeAsync(TrustedSigningEnvelope envelope, Guid requestId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var pipe = new NamedPipeClientStream(".", _options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await using var tls = new SslStream(pipe, false, (_, certificate, _, errors) =>
            errors == SslPolicyErrors.None && certificate != null &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), Convert.FromHexString(_options.ServerCertificateSha256)));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
            TargetHost = TrustedPipeClientOptions.TlsServerName,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.Online
        }, timeout.Token).ConfigureAwait(false);
        // No credential is written before both normal TLS validation and the configured pin succeed.
        await TrustedPipeProtocol.WriteAsync(tls, envelope, timeout.Token).ConfigureAwait(false);
        TrustedSigningReply reply = await TrustedPipeProtocol.ReadAsync<TrustedSigningReply>(tls, timeout.Token).ConfigureAwait(false);
        if (reply.RequestId != requestId) throw new SecurityException("Signing response does not match the submitted request.");
        if (!reply.Success) throw new TrustedSigningRejectedException(reply.ErrorCode);
        return reply;
    }
}
