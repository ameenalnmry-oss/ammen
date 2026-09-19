using System.Diagnostics;
using System.Text;
using System.IO;

namespace PharmaLIMS.Infrastructure
{
    internal static class ControlledTempFiles
    {
        private static readonly string RootDirectory = Path.Combine(Path.GetTempPath(), "PharmaLIMS-Controlled");
        private static readonly TimeSpan DeleteDelay = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan StaleAge = TimeSpan.FromHours(24);

        internal static string WriteHtmlAndOpen(string prefix, string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                throw new InvalidOperationException("The controlled preview document is empty.");

            Directory.CreateDirectory(RootDirectory);
            CleanupStaleFiles();

            string safePrefix = SanitizePrefix(prefix);
            string path = Path.Combine(
                RootDirectory,
                safePrefix + "_" + Guid.NewGuid().ToString("N") + ".html");

            using (FileStream stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.WriteThrough))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(html);
                writer.Flush();
            }

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                TryDelete(path);
                throw;
            }

            ScheduleDelete(path);
            return path;
        }

        private static string SanitizePrefix(string prefix)
        {
            string value = string.IsNullOrWhiteSpace(prefix) ? "PharmaLIMS_Document" : prefix.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');

            value = value.Replace("..", "_", StringComparison.Ordinal);
            if (value.Length > 80)
                value = value[..80];

            return value;
        }

        private static void ScheduleDelete(string path)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(DeleteDelay).ConfigureAwait(false);
                    TryDelete(path);
                }
                catch (Exception ex)
                {
                    ApplicationLogger.Warning("Controlled temporary document cleanup failed: " + ex.GetType().Name);
                }
            });
        }

        private static void CleanupStaleFiles()
        {
            try
            {
                if (!Directory.Exists(RootDirectory))
                    return;

                DateTime cutoff = DateTime.UtcNow.Subtract(StaleAge);
                foreach (string file in Directory.EnumerateFiles(RootDirectory, "*.html", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff)
                            TryDelete(file);
                    }
                    catch
                    {
                        // Best-effort cleanup must never block the regulated workflow.
                    }
                }
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("Unable to inspect stale controlled temporary documents: " + ex.GetType().Name);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // A browser may still hold the file. The next stale-file cleanup will retry.
            }
        }
    }
}
