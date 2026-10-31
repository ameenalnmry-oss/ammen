using System;
using System.Globalization;

namespace PharmaLIMS.Infrastructure;

public static class PreflightTimestamp
{
    public static string Format(DateTime timestamp) =>
        timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
