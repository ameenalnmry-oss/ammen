using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PharmaLIMS.TrustedSigning;

public sealed record TrustedSigningEnvelope(EquipmentSigningRequest? Request, string ExpectedUsername, string Password,
    TrustedApplicationRequest? Authentication = null, ActivitySigningRequest? Activity = null);
public sealed record TrustedSigningReply(Guid RequestId, bool Success, string? SignedBy, string ErrorCode, string? UserJson = null, string? Ticket = null);

public static class TrustedPipeProtocol
{
    public const int MaximumFrameBytes = 65536;
    private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (body.Length is <= 0 or > MaximumFrameBytes) throw new ArgumentException("Invalid signing message size.");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, body.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (size is <= 0 or > MaximumFrameBytes) throw new ArgumentException("Invalid signing message size.");
        byte[] body = new byte[size];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize<T>(body, JsonOptions) ?? throw new ArgumentException("Missing signing message."); }
        catch (JsonException) { throw new ArgumentException("Invalid signing message."); }
    }
}

public sealed record TrustedPipeClientOptions(string PipeName, string ServerCertificateSha256)
{
    public const string TlsServerName = "PharmaLIMS.TrustedSigning";
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PipeName) || PipeName.Length > 100 ||
            PipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Invalid local signing pipe name.");
        if (ServerCertificateSha256 is null || ServerCertificateSha256.Length != 64 || !ServerCertificateSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("The trusted service certificate SHA256 pin is required.");
    }
}
