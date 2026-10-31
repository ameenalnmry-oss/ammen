using Microsoft.Data.SqlClient;

namespace PharmaLIMS.Services;

/// <summary>Ensures a switched desktop cannot inherit the previous actor's configuration overrides.</summary>
public static class SwitchedProcessEnvironment
{
    public static readonly string[] RemovedOverrides =
    {
        "PHARMALIMS_CONNECTION_STRING", "PHARMALIMS_DPAPI_PROTECTED_PASSWORD",
        "PHARMALIMS_APPLY_STARTUP_DATABASE_UPDATES"
    };

    public static (string Server, string Database) ReadTarget(string connectionString)
    {
        var connection = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(connection.DataSource) || string.IsNullOrWhiteSpace(connection.InitialCatalog))
            throw new InvalidOperationException("Windows user switching requires an explicit SQL server and database.");
        return (connection.DataSource, connection.InitialCatalog);
    }

    public static void EnsureTarget(string expectedServer, string expectedDatabase, string actualServer, string actualDatabase)
    {
        if (string.IsNullOrWhiteSpace(expectedServer) || string.IsNullOrWhiteSpace(expectedDatabase) ||
            !string.Equals(expectedServer, actualServer, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expectedDatabase, actualDatabase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The SQL destination changed during the Windows user switch. No connection was opened.");
    }

    public static void Apply(IDictionary<string, string?> environment, string profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
            throw new ArgumentException("A target Windows profile is required.", nameof(profile));
        profile = profile.TrimEnd('\\');
        foreach (string name in RemovedOverrides) environment.Remove(name);
        environment["USERPROFILE"] = profile;
        environment["LOCALAPPDATA"] = profile + @"\AppData\Local";
        environment["APPDATA"] = profile + @"\AppData\Roaming";
        environment["TEMP"] = profile + @"\AppData\Local\Temp";
        environment["TMP"] = environment["TEMP"];
    }
}
