using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using System;
using System.IO;
using System.Text.Json;

namespace PharmaLIMS
{
    public static class AppConfig
    {
        private const string DefaultSqlServerName = @".\SQLEXPRESS";
        private const string DefaultDatabaseName = "PharmaLIMS";
        private const string DefaultEnvironmentName = "Production";
#if DEBUG
        private const string CompiledEnvironmentName = "Development";
#else
        private const string CompiledEnvironmentName = "Production";
#endif
        private const string DefaultSiteName = "Pharmaceutical Site";
        private const string DefaultDepartmentName = "Microbiology Department";
        private const string DefaultApplicationMode = "MultiDepartment";
        public const int DefaultSessionTimeoutMinutes = 15;
        public const int MinimumSessionTimeoutMinutes = 5;
        public const int MaximumSessionTimeoutMinutes = 60;

        private static readonly Lazy<AppSettings> Settings = new Lazy<AppSettings>(LoadSettings);

        // Compliance mode is compiled into the application. Runtime JSON cannot turn a
        // Release/Production binary into a Development binary with bypass privileges.
        public static string EnvironmentName => CompiledEnvironmentName;
        public static bool IsProduction => CompiledEnvironmentName.Equals("Production", StringComparison.OrdinalIgnoreCase);
        public static bool IsDevelopment => CompiledEnvironmentName.Equals("Development", StringComparison.OrdinalIgnoreCase);

        public static string SiteName => Settings.Value.SiteName ?? DefaultSiteName;
        public static string DepartmentName => Settings.Value.DepartmentName ?? DefaultDepartmentName;
        public static string WorkstationName => Settings.Value.WorkstationName ?? Environment.MachineName;
        public static string ApplicationMode => Settings.Value.ApplicationMode ?? DefaultApplicationMode;

        public static string SqlServerName => Settings.Value.SqlServerName ?? DefaultSqlServerName;
        public static string DatabaseName => Settings.Value.DatabaseName ?? DefaultDatabaseName;
        public static int CommandTimeoutSeconds => Settings.Value.CommandTimeoutSeconds > 0
            ? Settings.Value.CommandTimeoutSeconds
            : 30;

        public static bool EnforceAuditTrail => Settings.Value.EnforceAuditTrail;
        public static bool EnforceElectronicSignatureStorage => Settings.Value.EnforceElectronicSignatureStorage;
        public static bool ApplyStartupDatabaseUpdates => Settings.Value.ApplyStartupDatabaseUpdates;
        public static int SessionTimeoutMinutes => Settings.Value.SessionTimeoutMinutes > 0
            ? Settings.Value.SessionTimeoutMinutes
            : DefaultSessionTimeoutMinutes;
        public static bool DevelopmentAdminFullPermissions =>
            IsDevelopment && Settings.Value.DevelopmentAdminFullPermissions;
        public static bool AllowEarlyMicrobiologyResults =>
            IsDevelopment && Settings.Value.AllowEarlyMicrobiologyResults;
        public static bool AllowLegacyPrmSpecificationFallback =>
            IsDevelopment && Settings.Value.AllowLegacyPrmSpecificationFallback;

        public static string ConnectionString => Settings.Value.ConnectionString ?? BuildConnectionString(Settings.Value);

        private static AppSettings LoadSettings()
        {
            AppSettings settings = CreateDefaultSettings();
            string settingsPath = ResolveSettingsPath();

            // A Production binary must never silently fall back to compiled defaults.
            // Controlled publishing maps the approved site-specific
            // appsettings.Production.json to appsettings.json. Direct Release execution
            // without that file therefore fails closed before any database connection
            // or GMP workflow can start.
            if (IsProduction && !File.Exists(settingsPath))
            {
                throw new InvalidOperationException(
                    "Production runtime configuration is missing. Deploy an approved site-specific appsettings.json " +
                    "(published from appsettings.Production.json) before starting PharmaLIMS.");
            }

            if (File.Exists(settingsPath))
            {
                using FileStream stream = File.OpenRead(settingsPath);
                using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                JsonElement root = document.RootElement;
                settings.EnvironmentName = ReadString(root, "Environment", settings.EnvironmentName);

                if (root.TryGetProperty("Application", out JsonElement application))
                {
                    settings.SiteName = ReadString(application, "SiteName", settings.SiteName);
                    settings.DepartmentName = ReadString(application, "DepartmentName", settings.DepartmentName);
                    settings.WorkstationName = ReadString(application, "WorkstationName", settings.WorkstationName);
                    settings.ApplicationMode = ReadString(application, "ApplicationMode", settings.ApplicationMode);
                }

                if (root.TryGetProperty("Database", out JsonElement database))
                {
                    settings.SqlServerName = ReadString(database, "Server", settings.SqlServerName);
                    settings.DatabaseName = ReadString(database, "Database", settings.DatabaseName);
                    settings.IntegratedSecurity = ReadBool(database, "IntegratedSecurity", settings.IntegratedSecurity);
                    settings.UserId = ReadString(database, "UserId", settings.UserId);
                    settings.Password = ReadString(database, "Password", settings.Password);
                    settings.DpapiProtectedPassword = ReadString(database, "DpapiProtectedPassword", settings.DpapiProtectedPassword);
                    settings.Encrypt = ReadBool(database, "Encrypt", settings.Encrypt);
                    settings.TrustServerCertificate = ReadBool(database, "TrustServerCertificate", settings.TrustServerCertificate);
                    settings.CommandTimeoutSeconds = ReadInt(database, "CommandTimeoutSeconds", settings.CommandTimeoutSeconds);

                    string configuredConnectionString = ReadString(database, "ConnectionString", string.Empty);
                    if (!string.IsNullOrWhiteSpace(configuredConnectionString))
                        settings.ConnectionStringOverride = configuredConnectionString.Trim();
                }

                if (root.TryGetProperty("Runtime", out JsonElement runtime))
                {
                    settings.ApplyStartupDatabaseUpdates = ReadBool(
                        runtime,
                        "ApplyStartupDatabaseUpdates",
                        settings.ApplyStartupDatabaseUpdates);

                    settings.SessionTimeoutMinutes = ReadInt(
                        runtime,
                        "SessionTimeoutMinutes",
                        settings.SessionTimeoutMinutes);

                    settings.DevelopmentAdminFullPermissions = ReadBool(
                        runtime,
                        "DevelopmentAdminFullPermissions",
                        settings.DevelopmentAdminFullPermissions);

                    settings.AllowEarlyMicrobiologyResults = ReadBool(
                        runtime,
                        "AllowEarlyMicrobiologyResults",
                        settings.AllowEarlyMicrobiologyResults);

                    settings.AllowLegacyPrmSpecificationFallback = ReadBool(
                        runtime,
                        "AllowLegacyPrmSpecificationFallback",
                        settings.AllowLegacyPrmSpecificationFallback);
                }

                if (root.TryGetProperty("Compliance", out JsonElement compliance))
                {
                    settings.EnforceAuditTrail = ReadBool(compliance, "EnforceAuditTrail", settings.EnforceAuditTrail);
                    settings.EnforceElectronicSignatureStorage = ReadBool(
                        compliance,
                        "EnforceElectronicSignatureStorage",
                        settings.EnforceElectronicSignatureStorage);
                }
            }

            string? environmentConnectionString = Environment.GetEnvironmentVariable("PHARMALIMS_CONNECTION_STRING");
            if (!string.IsNullOrWhiteSpace(environmentConnectionString))
                settings.ConnectionStringOverride = environmentConnectionString.Trim();

            string? protectedPasswordOverride = Environment.GetEnvironmentVariable("PHARMALIMS_DPAPI_PROTECTED_PASSWORD");
            if (!string.IsNullOrWhiteSpace(protectedPasswordOverride))
                settings.DpapiProtectedPassword = protectedPasswordOverride.Trim();

            string? startupUpdatesOverride = Environment.GetEnvironmentVariable("PHARMALIMS_APPLY_STARTUP_DATABASE_UPDATES");
            if (!string.IsNullOrWhiteSpace(startupUpdatesOverride) &&
                bool.TryParse(startupUpdatesOverride, out bool applyStartupDatabaseUpdates))
            {
                settings.ApplyStartupDatabaseUpdates = applyStartupDatabaseUpdates;
            }

            NormalizeSettings(settings);

            // Build first, then validate the exact connection string that will actually be used.
            // This prevents an environment-variable override from bypassing production checks.
            settings.ConnectionString = BuildConnectionString(settings);
            ValidateSettings(settings);

            // Do not retain a second plaintext copy after the final connection string is built.
            settings.Password = string.Empty;

            return settings;
        }

        private static AppSettings CreateDefaultSettings()
        {
            return new AppSettings
            {
                EnvironmentName = DefaultEnvironmentName,
                SiteName = DefaultSiteName,
                DepartmentName = DefaultDepartmentName,
                WorkstationName = Environment.MachineName,
                ApplicationMode = DefaultApplicationMode,
                SqlServerName = DefaultSqlServerName,
                DatabaseName = DefaultDatabaseName,
                Encrypt = true,
                TrustServerCertificate = false,
                IntegratedSecurity = true,
                CommandTimeoutSeconds = 30,
                EnforceAuditTrail = true,
                EnforceElectronicSignatureStorage = true,
                ApplyStartupDatabaseUpdates = false,
                SessionTimeoutMinutes = DefaultSessionTimeoutMinutes,
                DevelopmentAdminFullPermissions = false,
                AllowEarlyMicrobiologyResults = false,
                AllowLegacyPrmSpecificationFallback = false,
                UserId = string.Empty,
                Password = string.Empty,
                DpapiProtectedPassword = string.Empty,
                ConnectionStringOverride = string.Empty,
                ConnectionString = string.Empty
            };
        }

        private static string ResolveSettingsPath()
        {
            string baseDirectoryPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(baseDirectoryPath))
                return baseDirectoryPath;

            string currentDirectoryPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            if (File.Exists(currentDirectoryPath))
                return currentDirectoryPath;

            return baseDirectoryPath;
        }

        private static void NormalizeSettings(AppSettings settings)
        {
            settings.EnvironmentName = string.IsNullOrWhiteSpace(settings.EnvironmentName)
                ? DefaultEnvironmentName
                : settings.EnvironmentName.Trim();

            settings.SiteName = string.IsNullOrWhiteSpace(settings.SiteName)
                ? DefaultSiteName
                : settings.SiteName.Trim();

            settings.DepartmentName = string.IsNullOrWhiteSpace(settings.DepartmentName)
                ? DefaultDepartmentName
                : settings.DepartmentName.Trim();

            settings.WorkstationName = string.IsNullOrWhiteSpace(settings.WorkstationName)
                ? Environment.MachineName
                : settings.WorkstationName.Trim();

            settings.ApplicationMode = string.IsNullOrWhiteSpace(settings.ApplicationMode)
                ? DefaultApplicationMode
                : settings.ApplicationMode.Trim();

            if (settings.CommandTimeoutSeconds <= 0)
                settings.CommandTimeoutSeconds = 30;

            if (settings.SessionTimeoutMinutes <= 0)
                settings.SessionTimeoutMinutes = DefaultSessionTimeoutMinutes;
        }

        private static void ValidateSettings(AppSettings settings)
        {
            if (!settings.EnvironmentName.Equals("Production", StringComparison.OrdinalIgnoreCase) &&
                !settings.EnvironmentName.Equals("Development", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Environment must be exactly 'Production' or 'Development'. Unknown environment names are blocked to prevent fail-open compliance behavior.");
            }

            if (!settings.EnvironmentName.Equals(CompiledEnvironmentName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The runtime configuration requests '{settings.EnvironmentName}', but this binary is compiled for '{CompiledEnvironmentName}'. " +
                    "PharmaLIMS blocks environment switching by editing appsettings.json. Use a controlled build for the required environment.");
            }

            SqlConnectionStringBuilder builder;

            try
            {
                builder = new SqlConnectionStringBuilder(settings.ConnectionString);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException("The configured database connection string is invalid.", ex);
            }

            if (string.IsNullOrWhiteSpace(builder.DataSource))
                throw new InvalidOperationException("The database server name is required.");

            if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
                throw new InvalidOperationException("The PharmaLIMS database name is required.");

            if (!IsProduction)
                return;

            string encryptValue = builder.ContainsKey("Encrypt")
                ? builder["Encrypt"]?.ToString() ?? string.Empty
                : string.Empty;

            bool encryptionEnabled =
                encryptValue.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                encryptValue.Equals("Mandatory", StringComparison.OrdinalIgnoreCase) ||
                encryptValue.Equals("Strict", StringComparison.OrdinalIgnoreCase);

            if (!encryptionEnabled)
            {
                throw new InvalidOperationException(
                    "Production database configuration is not secure. Enable encryption in the effective connection string.");
            }

            if (builder.TrustServerCertificate)
            {
                throw new InvalidOperationException(
                    "Production database configuration is not secure. TrustServerCertificate must be false.");
            }

            if (!builder.IntegratedSecurity)
            {
                if (!string.IsNullOrWhiteSpace(settings.ConnectionStringOverride))
                {
                    throw new InvalidOperationException(
                        "Production SQL authentication cannot be supplied through a plaintext ConnectionString override. " +
                        "Use Windows Integrated Security or Database:DpapiProtectedPassword.");
                }

                if (!string.IsNullOrWhiteSpace(settings.Password))
                {
                    throw new InvalidOperationException(
                        "Production database passwords must not be stored as plaintext in appsettings.json. " +
                        "Use Windows Integrated Security or Database:DpapiProtectedPassword.");
                }

                if (string.IsNullOrWhiteSpace(builder.UserID) ||
                    string.IsNullOrWhiteSpace(settings.DpapiProtectedPassword) ||
                    string.IsNullOrWhiteSpace(builder.Password))
                {
                    throw new InvalidOperationException(
                        "Production SQL authentication requires UserId and a DPAPI-protected password created for the Windows account that runs PharmaLIMS.");
                }
            }

            if (!settings.EnforceAuditTrail)
            {
                throw new InvalidOperationException(
                    "Compliance:EnforceAuditTrail must be true in the Production environment.");
            }

            if (!settings.EnforceElectronicSignatureStorage)
            {
                throw new InvalidOperationException(
                    "Compliance:EnforceElectronicSignatureStorage must be true in the Production environment.");
            }

            if (settings.SessionTimeoutMinutes < MinimumSessionTimeoutMinutes ||
                settings.SessionTimeoutMinutes > MaximumSessionTimeoutMinutes)
            {
                throw new InvalidOperationException(
                    $"Runtime:SessionTimeoutMinutes must be between {MinimumSessionTimeoutMinutes} and {MaximumSessionTimeoutMinutes} minutes in Production.");
            }

            if (settings.ApplyStartupDatabaseUpdates)
            {
                throw new InvalidOperationException(
                    "Runtime:ApplyStartupDatabaseUpdates must be false in the Production environment.");
            }

            if (settings.DevelopmentAdminFullPermissions)
            {
                throw new InvalidOperationException(
                    "Runtime:DevelopmentAdminFullPermissions must be false in the Production environment.");
            }

            if (settings.AllowEarlyMicrobiologyResults)
            {
                throw new InvalidOperationException(
                    "Runtime:AllowEarlyMicrobiologyResults must be false in the Production environment.");
            }

            if (settings.AllowLegacyPrmSpecificationFallback)
            {
                throw new InvalidOperationException(
                    "Runtime:AllowLegacyPrmSpecificationFallback must be false in the Production environment.");
            }
        }

        private static string BuildConnectionString(AppSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.ConnectionStringOverride))
                return settings.ConnectionStringOverride.Trim();

            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder
            {
                DataSource = string.IsNullOrWhiteSpace(settings.SqlServerName)
                    ? DefaultSqlServerName
                    : settings.SqlServerName.Trim(),
                InitialCatalog = string.IsNullOrWhiteSpace(settings.DatabaseName)
                    ? DefaultDatabaseName
                    : settings.DatabaseName.Trim(),
                Encrypt = settings.Encrypt
                    ? SqlConnectionEncryptOption.Mandatory
                    : SqlConnectionEncryptOption.Optional,
                TrustServerCertificate = settings.TrustServerCertificate,
                ConnectTimeout = 15,
                ApplicationName = "PharmaLIMS",
                Pooling = true,
                MinPoolSize = 1,
                MaxPoolSize = 40,
                PersistSecurityInfo = false
            };

            if (settings.IntegratedSecurity)
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.UserID = settings.UserId?.Trim() ?? string.Empty;
                if (IsProduction)
                {
                    if (!string.IsNullOrWhiteSpace(settings.Password))
                        throw new InvalidOperationException(
                            "Plaintext SQL passwords are prohibited in Production configuration.");

                    builder.Password = ProtectedSecretStore.UnprotectCurrentUserBase64(
                        settings.DpapiProtectedPassword);
                }
                else
                {
                    builder.Password = !string.IsNullOrWhiteSpace(settings.DpapiProtectedPassword)
                        ? ProtectedSecretStore.UnprotectCurrentUserBase64(settings.DpapiProtectedPassword)
                        : settings.Password ?? string.Empty;
                }
            }

            return builder.ConnectionString;
        }

        private static string ReadString(JsonElement element, string propertyName, string defaultValue)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
                return defaultValue;

            return value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? defaultValue
                : defaultValue;
        }

        private static bool ReadBool(JsonElement element, string propertyName, bool defaultValue)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
                return defaultValue;

            if (value.ValueKind == JsonValueKind.True)
                return true;

            if (value.ValueKind == JsonValueKind.False)
                return false;

            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed))
                return parsed;

            return defaultValue;
        }

        private static int ReadInt(JsonElement element, string propertyName, int defaultValue)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
                return defaultValue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsedNumber))
                return parsedNumber;

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int parsedString))
                return parsedString;

            return defaultValue;
        }

        private sealed class AppSettings
        {
            public string EnvironmentName { get; set; } = string.Empty;
            public string SiteName { get; set; } = string.Empty;
            public string DepartmentName { get; set; } = string.Empty;
            public string WorkstationName { get; set; } = string.Empty;
            public string ApplicationMode { get; set; } = string.Empty;
            public string SqlServerName { get; set; } = string.Empty;
            public string DatabaseName { get; set; } = string.Empty;
            public bool IntegratedSecurity { get; set; }
            public string UserId { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
            public string DpapiProtectedPassword { get; set; } = string.Empty;
            public bool Encrypt { get; set; }
            public bool TrustServerCertificate { get; set; }
            public int CommandTimeoutSeconds { get; set; }
            public bool EnforceAuditTrail { get; set; }
            public bool EnforceElectronicSignatureStorage { get; set; }
            public bool ApplyStartupDatabaseUpdates { get; set; }
            public int SessionTimeoutMinutes { get; set; }
            public bool DevelopmentAdminFullPermissions { get; set; }
            public bool AllowEarlyMicrobiologyResults { get; set; }
            public bool AllowLegacyPrmSpecificationFallback { get; set; }
            public string ConnectionStringOverride { get; set; } = string.Empty;
            public string ConnectionString { get; set; } = string.Empty;
        }
    }
}
