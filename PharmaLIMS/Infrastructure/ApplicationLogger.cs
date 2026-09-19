using System;
using System.IO;
using System.Text.RegularExpressions;

namespace PharmaLIMS.Infrastructure
{
    /// <summary>
    /// Central application logger. Logs are written outside the installation directory so
    /// standard Windows users can record diagnostics without requiring administrator rights.
    /// </summary>
    public static class ApplicationLogger
    {
        private static readonly object SyncRoot = new object();
        private static readonly Regex SecretPattern = new Regex(
            @"(?i)(password|pwd|secret|token|access[_ -]?token|api[_ -]?key|connection[_ -]?string)\s*[:=]\s*[^;\r\n]*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string LogDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PharmaLIMS",
            "Logs");

        public static void Information(string message)
        {
            Write("INFO", message, null);
        }

        public static void Warning(string message, Exception? exception = null)
        {
            Write("WARN", message, exception);
        }

        public static void Error(string message, Exception? exception = null)
        {
            Write("ERROR", message, exception);
        }

        private static void Write(string level, string message, Exception? exception)
        {
            try
            {
                string directory = LogDirectory;
                Directory.CreateDirectory(directory);

                string logPath = Path.Combine(directory, $"PharmaLIMS-{DateTime.Now:yyyyMMdd}.log");
                string safeMessage = RedactSecrets(message ?? string.Empty);
                string safeException = exception == null ? string.Empty : RedactSecrets(exception.ToString());

                string entry =
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {safeMessage}{Environment.NewLine}" +
                    (string.IsNullOrWhiteSpace(safeException)
                        ? string.Empty
                        : safeException + Environment.NewLine) +
                    "--------------------------------------------------" + Environment.NewLine;

                lock (SyncRoot)
                {
                    File.AppendAllText(logPath, entry);
                }
            }
            catch
            {
                // Logging must never interrupt a regulated workflow or mask the original error.
            }
        }

        private static string RedactSecrets(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return SecretPattern.Replace(value, match =>
            {
                int separatorIndex = match.Value.IndexOf('=');
                string key = separatorIndex >= 0
                    ? match.Value.Substring(0, separatorIndex).Trim()
                    : "Secret";

                return key + "=[REDACTED]";
            });
        }
    }
}
