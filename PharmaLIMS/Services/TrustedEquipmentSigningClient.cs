using PharmaLIMS.TrustedSigning;
using System;
using System.IO;
using System.Text.Json;

namespace PharmaLIMS.Services;

public static class TrustedEquipmentSigningClient
{
    public static WindowsTrustedPipeClient CreateFromConfiguration()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("TrustedSigning", out var settings) ||
            !settings.TryGetProperty("Enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Trusted equipment signing is not configured. Contact the system administrator; no record was changed.");
        string pipe = settings.GetProperty("PipeName").GetString() ?? "";
        string pin = settings.GetProperty("ServerCertificateSha256").GetString() ?? "";
        return new WindowsTrustedPipeClient(new TrustedPipeClientOptions(pipe, pin));
    }
}
