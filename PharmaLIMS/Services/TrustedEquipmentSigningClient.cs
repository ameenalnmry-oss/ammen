using PharmaLIMS.TrustedSigning;
using System;
using System.IO;
using System.Text.Json;

namespace PharmaLIMS.Services;

public static class TrustedEquipmentSigningClient
{
    public static WindowsTrustedPipeClient CreateFromConfiguration() =>
        CreateFromConfiguration(activityOnly: false);

    public static WindowsTrustedPipeClient CreateActivityFromConfiguration() =>
        CreateFromConfiguration(activityOnly: true);

    private static WindowsTrustedPipeClient CreateFromConfiguration(bool activityOnly)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("TrustedSigning", out var settings))
            throw new InvalidOperationException("Trusted equipment signing is not configured. Contact the system administrator; no record was changed.");

        bool globalEnabled = settings.TryGetProperty("Enabled", out var enabled) &&
                             enabled.ValueKind == JsonValueKind.True;
        bool activityEnabled = settings.TryGetProperty("ActivitySigningEnabled", out var activity) &&
                               activity.ValueKind == JsonValueKind.True;
        if ((!activityOnly && !globalEnabled) || (activityOnly && !globalEnabled && !activityEnabled))
            throw new InvalidOperationException("Trusted equipment signing is not configured. Contact the system administrator; no record was changed.");

        string pipe = settings.GetProperty("PipeName").GetString() ?? "";
        string pin = settings.GetProperty("ServerCertificateSha256").GetString() ?? "";
        return new WindowsTrustedPipeClient(new TrustedPipeClientOptions(pipe, pin));
    }
}
