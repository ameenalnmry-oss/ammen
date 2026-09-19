using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PharmaLIMS.Services;

/// <summary>
/// Reads approved, immutable external EM data for a focused area-and-parameter review
/// and stores electronically approved review snapshots with a reproducible source anchor.
/// This service intentionally does not infer microbiological root cause: it reports
/// statistical signals for QA review and investigation.
/// </summary>
public enum ExternalTrendDataMode
{
    Approved,
    PendingDraft
}

public sealed class ExternalTrendThreeCycleService
{
    private const int ExternalTrendSelectorTimeoutSeconds = 60;
    private const int ExternalTrendReviewTimeoutSeconds = 90;
    private readonly object selectorCatalogSync = new();
    private DataTable? selectorCatalog;
    private readonly ExternalTrendDataMode dataMode;

    public ExternalTrendThreeCycleService(ExternalTrendDataMode dataMode = ExternalTrendDataMode.Approved)
    {
        this.dataMode = dataMode;
    }

    public ExternalTrendDataMode DataMode => dataMode;
    public bool IsDraftMode => dataMode == ExternalTrendDataMode.PendingDraft;
    private string BatchStatus => IsDraftMode ? "Pending Approval" : "Approved";

    private static DataTable ExecuteTrendRead(string query, SqlParameter[]? parameters = null, int? timeoutSeconds = null) =>
        DatabaseHelper.ExecuteQuery(query, parameters, timeoutSeconds ?? ExternalTrendReviewTimeoutSeconds);

    public DateTimeOffset GetDatabaseTime()
    {
        object value = DatabaseHelper.ExecuteScalar("SELECT SYSDATETIMEOFFSET();");
        return value is DateTimeOffset timestamp
            ? timestamp
            : DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, CultureInfo.InvariantCulture);
    }

    private static bool HasResultQualifierColumn()
    {
        object result = DatabaseHelper.ExecuteScalar(@"
SELECT CASE
    WHEN COL_LENGTH(N'dbo.ExternalTrendImportRows', N'ResultQualifier') IS NULL THEN 0
    ELSE 1
END;");
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
    }

    public bool IsApprovalSnapshotSchemaReady()
    {
        object result = DatabaseHelper.ExecuteScalar(@"
SELECT CASE WHEN
       OBJECT_ID(N'dbo.EMTrendReviewSnapshots',N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'MethodName') IS NOT NULL
   AND COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'UnitName') IS NOT NULL
   AND COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'PeriodCount') IS NOT NULL
   AND COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'PeriodDefinitionJson') IS NOT NULL
   AND COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'SourceAnchorBatchID') IS NOT NULL
   AND COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'SourceAnchorImportNumber') IS NOT NULL
   AND COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'SourceCutoffAt') IS NOT NULL
   AND COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'SourceBatchManifestJson') IS NOT NULL
   AND COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'SourceBatchManifestSha256') IS NOT NULL
   AND COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'SnapshotHashSha256') IS NOT NULL
   AND COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'ReviewerRole') IS NOT NULL
   AND COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'SignatureMeaning') IS NOT NULL
   AND COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'SignatureReason') IS NOT NULL
   AND COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'SignedAt') IS NOT NULL
   AND EXISTS
       (
           SELECT 1 FROM sys.foreign_keys
           WHERE parent_object_id=OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
             AND name=N'FK_EMTrendReviewSnapshots_SourceAnchorBatch'
             AND is_disabled=0 AND is_not_trusted=0
       )
   AND EXISTS
       (
           SELECT 1 FROM sys.check_constraints
           WHERE parent_object_id=OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
             AND name=N'CK_EMTrendReviewSnapshots_DatesV2'
             AND is_disabled=0 AND is_not_trusted=0
       )
   AND EXISTS
       (
           SELECT 1 FROM sys.check_constraints
           WHERE parent_object_id=OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
             AND name=N'CK_EMTrendReviewSnapshots_ControlledV2'
             AND is_disabled=0 AND is_not_trusted=0
       )
   AND EXISTS
       (
           SELECT 1 FROM sys.triggers
           WHERE parent_id=OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
             AND name=N'TR_EMTrendReviewSnapshots_Immutable'
             AND is_disabled=0
       )
THEN 1 ELSE 0 END;");

        return Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
    }

    public bool IsPerformanceSchemaReady()
    {
        object result = DatabaseHelper.ExecuteScalar(@"
SELECT CASE WHEN
       EXISTS
       (
           SELECT 1 FROM sys.indexes
           WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
             AND name = N'IX_ExternalTrendImportRows_MethodParameterDate_20260829'
             AND is_disabled = 0
       )
   AND EXISTS
       (
           SELECT 1 FROM sys.indexes
           WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportBatches')
             AND name = N'IX_ExternalTrendImportBatches_ApprovedEM_20260829'
             AND is_disabled = 0
       )
THEN 1 ELSE 0 END;");
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
    }

    public ExternalTrendImportAvailability GetImportAvailability()
    {
        DataTable table = ExecuteTrendRead(@"
SELECT
    COUNT(DISTINCT CASE WHEN b.Status = N'Approved' THEN b.ImportBatchID END) AS ApprovedBatches,
    COUNT(DISTINCT CASE WHEN b.Status = N'Pending Approval' THEN b.ImportBatchID END) AS PendingBatches,
    COUNT(DISTINCT CASE WHEN b.Status = N'Rejected' THEN b.ImportBatchID END) AS RejectedBatches,
    SUM(CASE WHEN b.Status = N'Approved' AND r.ResultValue IS NOT NULL THEN CONVERT(BIGINT,1) ELSE CONVERT(BIGINT,0) END) AS ApprovedNumericRows,
    SUM(CASE WHEN b.Status = N'Pending Approval' AND r.ResultValue IS NOT NULL THEN CONVERT(BIGINT,1) ELSE CONVERT(BIGINT,0) END) AS PendingNumericRows,
    MAX(CASE WHEN b.Status = N'Approved' THEN COALESCE(b.ApprovedAt,b.ImportedAt) END) AS LatestApprovedAt,
    MAX(CASE WHEN b.Status = N'Pending Approval' THEN b.ImportedAt END) AS LatestPendingAt
FROM dbo.ExternalTrendImportBatches b
LEFT JOIN dbo.ExternalTrendImportRows r ON r.ImportBatchID = b.ImportBatchID
WHERE b.ModuleName = N'Environmental Monitoring';", timeoutSeconds: ExternalTrendSelectorTimeoutSeconds);

        if (table.Rows.Count == 0)
            return new ExternalTrendImportAvailability(0, 0, 0, 0, 0, null, null);
        DataRow row = table.Rows[0];
        return new ExternalTrendImportAvailability(
            row.IsNull("ApprovedBatches") ? 0 : Convert.ToInt32(row["ApprovedBatches"], CultureInfo.InvariantCulture),
            row.IsNull("PendingBatches") ? 0 : Convert.ToInt32(row["PendingBatches"], CultureInfo.InvariantCulture),
            row.IsNull("RejectedBatches") ? 0 : Convert.ToInt32(row["RejectedBatches"], CultureInfo.InvariantCulture),
            row.IsNull("ApprovedNumericRows") ? 0L : Convert.ToInt64(row["ApprovedNumericRows"], CultureInfo.InvariantCulture),
            row.IsNull("PendingNumericRows") ? 0L : Convert.ToInt64(row["PendingNumericRows"], CultureInfo.InvariantCulture),
            row.IsNull("LatestApprovedAt") ? null : row.Field<DateTimeOffset?>("LatestApprovedAt"),
            row.IsNull("LatestPendingAt") ? null : row.Field<DateTimeOffset?>("LatestPendingAt"));
    }

    private static string CanonicalizeAreaCode(string? rawAreaCode)
    {
        string normalized = (rawAreaCode ?? string.Empty).Trim().Replace('_', '-').Replace(' ', '-').ToUpperInvariant();
        string core = normalized;
        if (core.StartsWith("EXT-EM-AA-", StringComparison.Ordinal))
            core = core[10..];
        else if (core.StartsWith("EXT-EM-", StringComparison.Ordinal))
            core = core[7..];
        else if (core.StartsWith("EM-", StringComparison.Ordinal))
            core = core[3..];

        core = core
            .Replace("BLUK", "BULK", StringComparison.Ordinal)
            .Replace("FIILING", "FILLING", StringComparison.Ordinal)
            .Replace("SIFITING", "SIFTING", StringComparison.Ordinal)
            .Replace("DIRTRY", "DIRTY", StringComparison.Ordinal)
            .Replace("EQUIPEMENT", "EQUIPMENT", StringComparison.Ordinal)
            .Replace("ARAE", "AREA", StringComparison.Ordinal)
            .Replace("IN-PROCESS", "INPROCESS", StringComparison.Ordinal);
        while (core.Contains("--", StringComparison.Ordinal))
            core = core.Replace("--", "-", StringComparison.Ordinal);
        return "EM-" + core;
    }

    private static string NormalizeRawAreaCode(string? rawAreaCode) =>
        (rawAreaCode ?? string.Empty).Trim().Replace('_', '-').Replace(' ', '-').ToUpperInvariant();

    private static string NormalizeTrendPopulation(string? rawClassification)
    {
        string value = (rawClassification ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith("UNSPECIFIED", StringComparison.Ordinal) ||
            value.StartsWith("QA CLASSIFICATION REQUIRED", StringComparison.Ordinal))
            return "Classification Required";
        if (value.StartsWith("UNCLASSIFIED", StringComparison.Ordinal) ||
            value.StartsWith("NONCLASSIFIED", StringComparison.Ordinal) ||
            value.StartsWith("NON-CLASSIFIED", StringComparison.Ordinal))
            return "Unclassified";

        string compact = value.Replace("_", " ", StringComparison.Ordinal)
                              .Replace("-", " ", StringComparison.Ordinal);
        while (compact.Contains("  ", StringComparison.Ordinal))
            compact = compact.Replace("  ", " ", StringComparison.Ordinal);

        if (compact.Equals("CLASSIFIED", StringComparison.Ordinal) ||
            compact.Equals("D", StringComparison.Ordinal) ||
            compact.Contains("ISO 8", StringComparison.Ordinal) ||
            compact.Contains("ISO8", StringComparison.Ordinal) ||
            compact.Contains("GRADE D", StringComparison.Ordinal))
            return "ISO 8 Production";

        return "Classification Required";
    }

    private static SqlParameter[] MethodParameterParameters(string methodName, string parameterName, params SqlParameter[] extra)
    {
        List<SqlParameter> parameters = new()
        {
            new SqlParameter("@MethodName", SqlDbType.NVarChar, 200) { Value = methodName.Trim() },
            new SqlParameter("@ParameterName", SqlDbType.NVarChar, 300) { Value = parameterName.Trim() }
        };
        parameters.AddRange(extra);
        return parameters.ToArray();
    }

    private DataTable GetSelectorCatalog(bool refresh = false)
    {
        lock (selectorCatalogSync)
        {
            if (!refresh && selectorCatalog != null)
                return selectorCatalog;

            selectorCatalog = ExecuteTrendRead(@"
SET LOCK_TIMEOUT 3000;
SELECT r.EntityCode AS RawAreaCode,
       MAX(NULLIF(LTRIM(RTRIM(r.EntityName)), N'')) AS EntityName,
       MAX(NULLIF(LTRIM(RTRIM(r.LocationName)), N'')) AS LocationName,
       COALESCE(NULLIF(LTRIM(RTRIM(r.AreaClassification)), N''), N'Unspecified') AS AreaClassification,
       COALESCE(NULLIF(LTRIM(RTRIM(r.MethodName)), N''), N'Unspecified method') AS MethodName,
       r.ParameterName,
       MIN(CAST(r.RecordDateTime AS date)) AS FirstDate,
       MAX(CAST(r.RecordDateTime AS date)) AS LastDate,
       COUNT_BIG(*) AS ObservationCount
FROM dbo.ExternalTrendImportRows r
INNER JOIN dbo.ExternalTrendImportBatches b
    ON b.ImportBatchID = r.ImportBatchID
WHERE b.Status = @BatchStatus
  AND b.ModuleName = N'Environmental Monitoring'
  AND (@RequireApproval = 0 OR b.ApprovedAt IS NOT NULL)
  AND NULLIF(LTRIM(RTRIM(r.EntityCode)), N'') IS NOT NULL
  AND NULLIF(LTRIM(RTRIM(r.ParameterName)), N'') IS NOT NULL
  AND r.ResultValue IS NOT NULL
GROUP BY r.EntityCode,
         COALESCE(NULLIF(LTRIM(RTRIM(r.AreaClassification)), N''), N'Unspecified'),
         COALESCE(NULLIF(LTRIM(RTRIM(r.MethodName)), N''), N'Unspecified method'),
         r.ParameterName;",
                new[]
                {
                    new SqlParameter("@BatchStatus", SqlDbType.NVarChar, 30) { Value = BatchStatus },
                    new SqlParameter("@RequireApproval", SqlDbType.Bit) { Value = IsDraftMode ? 0 : 1 }
                },
                timeoutSeconds: ExternalTrendSelectorTimeoutSeconds);
            return selectorCatalog;
        }
    }

    public DataTable GetAreas()
    {
        DataTable raw = GetSelectorCatalog(refresh: true);
        DataTable result = new();
        result.Columns.Add("AreaCode", typeof(string));
        result.Columns.Add("AreaName", typeof(string));
        result.Columns.Add("LocationName", typeof(string));
        result.Columns.Add("TrendPopulation", typeof(string));
        result.Columns.Add("AreaClassification", typeof(string));
        result.Columns.Add("IsComparable", typeof(bool));

        foreach (IGrouping<string, DataRow> group in raw.Rows.Cast<DataRow>()
                     .GroupBy(row => CanonicalizeAreaCode(Convert.ToString(row["RawAreaCode"], CultureInfo.InvariantCulture)), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            // Legacy imports created before controlled classification can contain
            // AreaClassification='Unspecified'.  Do not let those historical rows
            // falsely turn a later explicitly Classified area into a mixed/hidden area.
            // If an explicit classification exists, it controls the selector profile;
            // otherwise the area remains visible as Classification Required.
            HashSet<string> explicitPopulations = group
                .Select(row => NormalizeTrendPopulation(Convert.ToString(row["AreaClassification"], CultureInfo.InvariantCulture)))
                .Where(value => !value.Equals("Classification Required", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool mixed = explicitPopulations.Count > 1;
            string population = mixed
                ? "Mixed classification"
                : explicitPopulations.SingleOrDefault() ?? "Classification Required";
            string classification = mixed
                ? "Mixed - QA reconciliation required"
                : population.Equals("ISO 8 Production", StringComparison.OrdinalIgnoreCase)
                    ? "Classified"
                    : population.Equals("Unclassified", StringComparison.OrdinalIgnoreCase)
                        ? "Unclassified"
                        : "QA Classification Required";
            string areaName = group.Select(row => Convert.ToString(row["EntityName"], CultureInfo.InvariantCulture)?.Trim())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? group.Key;
            string locationName = group.Select(row => Convert.ToString(row["LocationName"], CultureInfo.InvariantCulture)?.Trim())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
            // Mixed historical classification is intentionally visible for QA reconciliation,
            // but it is not comparable and must not enter trend comparison/draft exports.
            bool isComparable = !mixed;
            result.Rows.Add(group.Key, areaName, locationName, population, classification, isComparable);
        }
        return result;
    }

    public DataTable GetMethods(string? areaCode)
    {
        string[] scope = string.IsNullOrWhiteSpace(areaCode) ? Array.Empty<string>() : new[] { areaCode.Trim() };
        return GetMethodsForAreas(scope);
    }

    public DataTable GetMethodsForAreas(IEnumerable<string>? areaCodes)
    {
        DataTable catalog = GetSelectorCatalog();
        HashSet<string> scope = (areaCodes ?? Array.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> methods = catalog.Rows.Cast<DataRow>()
            .Where(row => scope.Count == 0 || scope.Contains(CanonicalizeAreaCode(Convert.ToString(row["RawAreaCode"], CultureInfo.InvariantCulture))))
            .Select(row => Convert.ToString(row["MethodName"], CultureInfo.InvariantCulture) ?? "Unspecified method")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        DataTable result = new();
        result.Columns.Add("MethodName", typeof(string));
        foreach (string method in methods) result.Rows.Add(method);
        return result;
    }

    public DataTable GetParameters(string? areaCode, string? methodName)
    {
        string[] scope = string.IsNullOrWhiteSpace(areaCode) ? Array.Empty<string>() : new[] { areaCode.Trim() };
        return GetParametersForAreas(scope, methodName);
    }

    public DataTable GetParametersForAreas(IEnumerable<string>? areaCodes, string? methodName)
    {
        DataTable catalog = GetSelectorCatalog();
        HashSet<string> scope = (areaCodes ?? Array.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> parameters = catalog.Rows.Cast<DataRow>()
            .Where(row => scope.Count == 0 || scope.Contains(CanonicalizeAreaCode(Convert.ToString(row["RawAreaCode"], CultureInfo.InvariantCulture))))
            .Where(row => string.IsNullOrWhiteSpace(methodName) || (Convert.ToString(row["MethodName"], CultureInfo.InvariantCulture) ?? "Unspecified method").Equals(methodName, StringComparison.OrdinalIgnoreCase))
            .Select(row => Convert.ToString(row["ParameterName"], CultureInfo.InvariantCulture) ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        DataTable result = new();
        result.Columns.Add("ParameterName", typeof(string));
        foreach (string parameter in parameters) result.Rows.Add(parameter);
        return result;
    }

    public DataTable GetReviewableAreas(string methodName, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(methodName)) throw new ArgumentException("Method is required.", nameof(methodName));
        if (string.IsNullOrWhiteSpace(parameterName)) throw new ArgumentException("Parameter is required.", nameof(parameterName));
        DataTable catalog = GetSelectorCatalog();
        DataTable result = new();
        result.Columns.Add("AreaCode", typeof(string));
        foreach (string code in catalog.Rows.Cast<DataRow>()
                     .Where(row => (Convert.ToString(row["MethodName"], CultureInfo.InvariantCulture) ?? "Unspecified method").Equals(methodName, StringComparison.OrdinalIgnoreCase))
                     .Where(row => (Convert.ToString(row["ParameterName"], CultureInfo.InvariantCulture) ?? string.Empty).Equals(parameterName, StringComparison.OrdinalIgnoreCase))
                     .Select(row => CanonicalizeAreaCode(Convert.ToString(row["RawAreaCode"], CultureInfo.InvariantCulture)))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            result.Rows.Add(code);
        return result;
    }

    private static DateTimeOffset SourceCalendarBoundary(DateTime date) =>
        new(DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified), TimeSpan.Zero);

    private static bool IsUnspecifiedMethod(string methodName) =>
        methodName.Trim().Equals("Unspecified method", StringComparison.OrdinalIgnoreCase);

    private static string MethodFilterPredicate(string methodName, string rowAlias = "r") =>
        IsUnspecifiedMethod(methodName)
            ? $"({rowAlias}.MethodName IS NULL OR {rowAlias}.MethodName = N'')"
            : $"{rowAlias}.MethodName = @MethodName";

    private DataTable ReadApprovedObservationRows(string methodName, string parameterName, DateTime from, DateTime to)
    {
        bool includeResultQualifier = HasResultQualifierColumn();
        string qualifierProjection = includeResultQualifier ? "r.ResultQualifier" : "CAST(N'' AS nvarchar(100)) AS ResultQualifier";
        string methodPredicate = MethodFilterPredicate(methodName);
        return ExecuteTrendRead($@"
SET LOCK_TIMEOUT 3000;
SELECT r.RecordDateTime, r.ResultValue, r.UnitName, r.AlertLimit, r.ActionLimit,
       r.MethodName, r.ParameterName, r.SourceRecordID, r.EntityCode AS RawAreaCode,
       {qualifierProjection}, r.LocationName, COALESCE(b.ApprovedAt, b.ImportedAt) AS ApprovedAt, b.ImportBatchID, r.SourceRowNumber,
       b.ImportNumber, b.SourceSystem, b.OriginalFileName, b.FileHashSha256
FROM dbo.ExternalTrendImportRows r
INNER JOIN dbo.ExternalTrendImportBatches b
    ON b.ImportBatchID = r.ImportBatchID
WHERE b.Status = @BatchStatus
  AND b.ModuleName = N'Environmental Monitoring'
  AND (@RequireApproval = 0 OR b.ApprovedAt IS NOT NULL)
  AND {methodPredicate}
  AND r.ParameterName = @ParameterName
  AND r.RecordDateTime >= @From
  AND r.RecordDateTime < @ToExclusive
OPTION (RECOMPILE);",
            MethodParameterParameters(methodName, parameterName,
                new SqlParameter("@From", SqlDbType.DateTimeOffset) { Value = SourceCalendarBoundary(from.Date) },
                new SqlParameter("@ToExclusive", SqlDbType.DateTimeOffset) { Value = SourceCalendarBoundary(to.Date.AddDays(1)) },
                new SqlParameter("@BatchStatus", SqlDbType.NVarChar, 30) { Value = BatchStatus },
                new SqlParameter("@RequireApproval", SqlDbType.Bit) { Value = IsDraftMode ? 0 : 1 }));
    }

    private static string ObservationFingerprint(DataRow row, string canonicalArea)
    {
        static string Upper(object value) => (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Trim().ToUpperInvariant();
        string date = row.Field<DateTimeOffset>("RecordDateTime").ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        string result = row.Field<decimal?>("ResultValue")?.ToString("G29", CultureInfo.InvariantCulture) ?? string.Empty;
        return string.Join("|", "FP", canonicalArea, date, Upper(row["MethodName"]), Upper(row["ParameterName"]), result,
            Upper(row["ResultQualifier"]), Upper(row["UnitName"]), Upper(row["LocationName"]));
    }

    private static DataTable DeduplicateObservations(DataTable raw)
    {
        DataTable output = raw.Clone();
        output.Columns.Add("CanonicalAreaCode", typeof(string));
        List<ObservationCandidate> candidates = raw.Rows.Cast<DataRow>().Select(row =>
        {
            string canonical = CanonicalizeAreaCode(Convert.ToString(row["RawAreaCode"], CultureInfo.InvariantCulture));
            string fingerprint = ObservationFingerprint(row, canonical);
            string rawIdentity = NormalizeRawAreaCode(Convert.ToString(row["RawAreaCode"], CultureInfo.InvariantCulture));
            return new ObservationCandidate(row, canonical, rawIdentity, fingerprint);
        }).ToList();

        Dictionary<string, int> aliasCounts = candidates
            .GroupBy(item => $"{item.CanonicalArea}|{(Convert.ToString(item.Row["MethodName"], CultureInfo.InvariantCulture) ?? string.Empty).Trim().ToUpperInvariant()}|{(Convert.ToString(item.Row["ParameterName"], CultureInfo.InvariantCulture) ?? string.Empty).Trim().ToUpperInvariant()}|{item.Fingerprint}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.RawAreaIdentity).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);

        IEnumerable<ObservationCandidate> winners = candidates
            .GroupBy(item =>
            {
                string method = (Convert.ToString(item.Row["MethodName"], CultureInfo.InvariantCulture) ?? string.Empty).Trim().ToUpperInvariant();
                string parameter = (Convert.ToString(item.Row["ParameterName"], CultureInfo.InvariantCulture) ?? string.Empty).Trim().ToUpperInvariant();
                string aliasKey = $"{item.CanonicalArea}|{method}|{parameter}|{item.Fingerprint}";
                string sourceRecordId = (Convert.ToString(item.Row["SourceRecordID"], CultureInfo.InvariantCulture) ?? string.Empty).Trim().ToUpperInvariant();
                string identity = aliasCounts[aliasKey] > 1 ? "ALIAS|" + item.Fingerprint : !string.IsNullOrWhiteSpace(sourceRecordId) ? "ID|" + sourceRecordId : item.Fingerprint;
                return $"{item.CanonicalArea}|{method}|{parameter}|{identity}";
            }, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(item => item.Row.Field<decimal?>("AlertLimit").HasValue || item.Row.Field<decimal?>("ActionLimit").HasValue ? 0 : 1)
                .ThenByDescending(item => item.Row.Field<DateTimeOffset>("ApprovedAt"))
                .ThenByDescending(item => Convert.ToInt32(item.Row["ImportBatchID"], CultureInfo.InvariantCulture))
                .ThenByDescending(item => Convert.ToInt32(item.Row["SourceRowNumber"], CultureInfo.InvariantCulture))
                .First())
            .OrderBy(item => item.Row.Field<DateTimeOffset>("RecordDateTime"));

        foreach (ObservationCandidate winner in winners)
        {
            DataRow row = output.NewRow();
            foreach (DataColumn column in raw.Columns) row[column.ColumnName] = winner.Row[column.ColumnName];
            row["CanonicalAreaCode"] = winner.CanonicalArea;
            output.Rows.Add(row);
        }
        return output;
    }

    private static string SourceManifestBatchQuery(bool withLock, string methodName)
    {
        string batchHint = withLock ? " WITH (HOLDLOCK)" : string.Empty;
        string methodPredicate = MethodFilterPredicate(methodName);
        return $@"
SET LOCK_TIMEOUT 3000;
SELECT r.EntityCode AS RawAreaCode, b.ImportBatchID, b.ImportNumber, b.SourceSystem, b.OriginalFileName,
       b.FileHashSha256, b.ApprovedAt, COUNT_BIG(*) AS RelevantRowCount
FROM dbo.ExternalTrendImportRows r
INNER JOIN dbo.ExternalTrendImportBatches b{batchHint}
    ON b.ImportBatchID = r.ImportBatchID
WHERE b.Status = N'Approved'
  AND b.ModuleName = N'Environmental Monitoring'
  AND b.ApprovedAt IS NOT NULL
  AND {methodPredicate}
  AND r.ParameterName = @ParameterName
  AND r.RecordDateTime >= @From
  AND r.RecordDateTime < @ToExclusive
GROUP BY r.EntityCode, b.ImportBatchID, b.ImportNumber, b.SourceSystem, b.OriginalFileName, b.FileHashSha256, b.ApprovedAt
ORDER BY b.ImportBatchID
OPTION (RECOMPILE);";
    }

    private static Dictionary<string, SourceBatchManifest> BuildSourceManifestMap(
        string methodName,
        string parameterName,
        DateTime from,
        DateTime to,
        DataTable rows)
    {
        Dictionary<string, SourceBatchManifest> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, DataRow> areaGroup in rows.Rows.Cast<DataRow>()
                     .GroupBy(row => CanonicalizeAreaCode(Convert.ToString(row["RawAreaCode"], CultureInfo.InvariantCulture)), StringComparer.OrdinalIgnoreCase))
        {
            List<SourceBatchDescriptor> batches = areaGroup
                .GroupBy(row => Convert.ToInt32(row["ImportBatchID"], CultureInfo.InvariantCulture))
                .Select(batchGroup =>
                {
                    DataRow first = batchGroup.First();
                    return new SourceBatchDescriptor(
                        Convert.ToInt32(first["ImportBatchID"], CultureInfo.InvariantCulture),
                        Convert.ToString(first["ImportNumber"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                        Convert.ToString(first["SourceSystem"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                        Convert.ToString(first["OriginalFileName"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                        Convert.ToString(first["FileHashSha256"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                        first.Field<DateTimeOffset>("ApprovedAt"),
                        batchGroup.Sum(row => Convert.ToInt64(row["RelevantRowCount"], CultureInfo.InvariantCulture)));
                })
                .OrderBy(item => item.ImportBatchID)
                .ToList();
            if (batches.Count == 0) continue;
            SourceBatchDescriptor anchor = batches.OrderBy(item => item.ApprovedAt).ThenBy(item => item.ImportBatchID).Last();
            string json = JsonSerializer.Serialize(new
            {
                module = "Environmental Monitoring",
                areaCode = areaGroup.Key,
                methodName = methodName.Trim(),
                parameterName = parameterName.Trim(),
                reviewFrom = from.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                reviewTo = to.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                batches
            });
            result[areaGroup.Key] = new SourceBatchManifest(anchor.ImportBatchID, anchor.ImportNumber, json, Sha256Hex(json));
        }
        return result;
    }

    private static Dictionary<string, SourceBatchManifest> BuildSourceManifestMapFromObservationRows(
        string methodName,
        string parameterName,
        DateTime from,
        DateTime to,
        DataTable rows)
    {
        Dictionary<string, SourceBatchManifest> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, DataRow> areaGroup in rows.Rows.Cast<DataRow>()
                     .GroupBy(row => CanonicalizeAreaCode(Convert.ToString(row["RawAreaCode"], CultureInfo.InvariantCulture)), StringComparer.OrdinalIgnoreCase))
        {
            List<SourceBatchDescriptor> batches = areaGroup
                .GroupBy(row => Convert.ToInt32(row["ImportBatchID"], CultureInfo.InvariantCulture))
                .Select(batchGroup =>
                {
                    DataRow first = batchGroup.First();
                    return new SourceBatchDescriptor(
                        Convert.ToInt32(first["ImportBatchID"], CultureInfo.InvariantCulture),
                        Convert.ToString(first["ImportNumber"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                        Convert.ToString(first["SourceSystem"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                        Convert.ToString(first["OriginalFileName"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                        Convert.ToString(first["FileHashSha256"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                        first.Field<DateTimeOffset>("ApprovedAt"),
                        batchGroup.LongCount());
                })
                .OrderBy(item => item.ImportBatchID)
                .ToList();
            if (batches.Count == 0) continue;
            SourceBatchDescriptor anchor = batches.OrderBy(item => item.ApprovedAt).ThenBy(item => item.ImportBatchID).Last();
            string json = JsonSerializer.Serialize(new
            {
                module = "Environmental Monitoring",
                areaCode = areaGroup.Key,
                methodName = methodName.Trim(),
                parameterName = parameterName.Trim(),
                reviewFrom = from.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                reviewTo = to.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                batches
            });
            result[areaGroup.Key] = new SourceBatchManifest(anchor.ImportBatchID, anchor.ImportNumber, json, Sha256Hex(json));
        }
        return result;
    }

    private Dictionary<string, SourceBatchManifest> GetSourceManifestMap(string methodName, string parameterName, DateTime from, DateTime to)
    {
        DataTable rows = ExecuteTrendRead(
            SourceManifestBatchQuery(withLock: false, methodName),
            MethodParameterParameters(methodName, parameterName,
                new SqlParameter("@From", SqlDbType.DateTimeOffset) { Value = SourceCalendarBoundary(from.Date) },
                new SqlParameter("@ToExclusive", SqlDbType.DateTimeOffset) { Value = SourceCalendarBoundary(to.Date.AddDays(1)) }),
            ExternalTrendReviewTimeoutSeconds);
        return BuildSourceManifestMap(methodName, parameterName, from, to, rows);
    }

    public ExternalTrendGenerationData GetGenerationData(string methodName, string parameterName, DateTime from, DateTime to)
    {
        if (to.Date < from.Date) throw new ArgumentException("End date cannot precede start date.", nameof(to));

        // One bounded SQL read supplies both the observations and their approved-batch provenance.
        // This removes the previous manifest-before / observations / manifest-after three-query fan-out,
        // which could time out on large historical external-EM datasets. The exact same manifest is
        // revalidated under transaction locks during QA approval before any controlled snapshot is saved.
        DataTable rawRows = ReadApprovedObservationRows(methodName, parameterName, from, to);
        Dictionary<string, SourceBatchManifest> source = BuildSourceManifestMapFromObservationRows(methodName, parameterName, from, to, rawRows);
        DataTable rows = DeduplicateObservations(rawRows);

        return new ExternalTrendGenerationData(
            rows,
            source.ToDictionary(item => item.Key, item => item.Value.Sha256, StringComparer.OrdinalIgnoreCase));
    }

    public DataTable GetResults(string areaCode, string methodName, string parameterName, DateTime from, DateTime to)
    {
        ExternalTrendGenerationData generation = GetGenerationData(methodName, parameterName, from, to);
        DataTable result = generation.Rows.Clone();
        foreach (DataRow row in generation.Rows.Rows.Cast<DataRow>().Where(row =>
                     (Convert.ToString(row["CanonicalAreaCode"], CultureInfo.InvariantCulture) ?? string.Empty).Equals(areaCode, StringComparison.OrdinalIgnoreCase)))
            result.ImportRow(row);
        return result;
    }

    public DataTable GetAvailableDateRange(string? areaCode, string methodName, string parameterName)
    {
        string[] scope = string.IsNullOrWhiteSpace(areaCode) ? Array.Empty<string>() : new[] { areaCode.Trim() };
        return GetAvailableDateRangeForAreas(scope, methodName, parameterName);
    }

    public DataTable GetAvailableDateRangeForAreas(IEnumerable<string>? areaCodes, string methodName, string parameterName)
    {
        DataTable catalog = GetSelectorCatalog();
        HashSet<string> scope = (areaCodes ?? Array.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<DataRow> matches = catalog.Rows.Cast<DataRow>()
            .Where(row => scope.Count == 0 || scope.Contains(CanonicalizeAreaCode(Convert.ToString(row["RawAreaCode"], CultureInfo.InvariantCulture))))
            .Where(row => (Convert.ToString(row["MethodName"], CultureInfo.InvariantCulture) ?? "Unspecified method").Equals(methodName, StringComparison.OrdinalIgnoreCase))
            .Where(row => (Convert.ToString(row["ParameterName"], CultureInfo.InvariantCulture) ?? string.Empty).Equals(parameterName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        DataTable result = new();
        result.Columns.Add("FirstDate", typeof(DateTime));
        result.Columns.Add("LastDate", typeof(DateTime));
        result.Columns.Add("ObservationCount", typeof(long));
        if (matches.Count == 0)
        {
            result.Rows.Add(DBNull.Value, DBNull.Value, 0L);
            return result;
        }

        DateTime first = matches.Min(row => Convert.ToDateTime(row["FirstDate"], CultureInfo.InvariantCulture));
        DateTime last = matches.Max(row => Convert.ToDateTime(row["LastDate"], CultureInfo.InvariantCulture));
        long count = matches.Sum(row => Convert.ToInt64(row["ObservationCount"], CultureInfo.InvariantCulture));
        result.Rows.Add(first, last, count);
        return result;
    }

    public string GetSourceManifestFingerprint(string areaCode, string methodName, string parameterName)
    {
        ValidateIdentity(areaCode, methodName, parameterName);
        DataTable range = GetAvailableDateRange(areaCode, methodName, parameterName);
        if (range.Rows.Count == 0 || range.Rows[0].IsNull("FirstDate") || range.Rows[0].IsNull("LastDate"))
            throw new InvalidOperationException("No approved external source batch is available for this area, method, and parameter.");
        DateTime from = Convert.ToDateTime(range.Rows[0]["FirstDate"], CultureInfo.InvariantCulture);
        DateTime to = Convert.ToDateTime(range.Rows[0]["LastDate"], CultureInfo.InvariantCulture);
        Dictionary<string, SourceBatchManifest> map = GetSourceManifestMap(methodName, parameterName, from, to);
        if (!map.TryGetValue(areaCode.Trim(), out SourceBatchManifest? manifest))
            throw new InvalidOperationException("No approved external source batch is available for this area, method, and parameter.");
        return manifest.Sha256;
    }

    public static TrendCycleSummary Summarize(string cycle, DataTable rows)
    {
        List<DataRow> observations = rows.Rows.Cast<DataRow>()
            .Where(row => row.Field<decimal?>("ResultValue").HasValue)
            .ToList();
        List<DataRow> exactObservations = observations
            .Where(row => string.IsNullOrWhiteSpace(Convert.ToString(row["ResultQualifier"], CultureInfo.InvariantCulture)))
            .ToList();
        List<decimal> values = exactObservations
            .Select(row => row.Field<decimal?>("ResultValue")!.Value)
            .OrderBy(value => value)
            .ToList();
        int censoredCount = observations.Count - exactObservations.Count;

        List<decimal> alertValues = observations
            .Select(row => row.Field<decimal?>("AlertLimit"))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Distinct()
            .OrderBy(value => value)
            .ToList();
        List<decimal> actionValues = observations
            .Select(row => row.Field<decimal?>("ActionLimit"))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Distinct()
            .OrderBy(value => value)
            .ToList();
        List<string> units = observations
            .Select(row => (Convert.ToString(row["UnitName"], CultureInfo.InvariantCulture) ?? string.Empty).Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool limitsComplete = observations.Count == 0 || observations.All(row => row.Field<decimal?>("AlertLimit").HasValue && row.Field<decimal?>("ActionLimit").HasValue);
        bool limitsConsistent = alertValues.Count <= 1 && actionValues.Count <= 1;
        bool unitsConsistent = observations.Count == 0 || (units.Count == 1 && !string.IsNullOrWhiteSpace(units[0]));
        int actionCount = observations.Count(row => EvaluateQualifiedSignal(row) == "ACTION");
        int alertCount = observations.Count(row => EvaluateQualifiedSignal(row) == "ALERT");
        int qualifiedNeedsReview = observations.Count(row => EvaluateQualifiedSignal(row) == "NOT ASSESSED" && !string.IsNullOrWhiteSpace(Convert.ToString(row["ResultQualifier"], CultureInfo.InvariantCulture)));

        string dataQuality = observations.Count == 0
            ? "No observations"
            : !unitsConsistent
                ? "Missing or mixed units"
                : !limitsComplete
                    ? "Alert/action limit missing"
                    : !limitsConsistent
                        ? "Approved limits changed within period"
                        : qualifiedNeedsReview > 0
                            ? $"{qualifiedNeedsReview} qualified/censored result(s) require QA assessment; exact-value statistics exclude all {censoredCount} qualified result(s)"
                            : censoredCount > 0
                                ? $"{censoredCount} qualified/censored result(s); exact-value statistics exclude them"
                                : "Complete";

        return new TrendCycleSummary
        {
            Cycle = cycle,
            Count = observations.Count,
            ExactCount = values.Count,
            CensoredCount = censoredCount,
            QualifiedNeedsReviewCount = qualifiedNeedsReview,
            Minimum = values.Count == 0 ? null : values.First(),
            Mean = values.Count == 0 ? null : Math.Round(values.Average(), 2),
            Median = values.Count == 0 ? null : Math.Round(values.Count % 2 == 1 ? values[values.Count / 2] : (values[(values.Count / 2) - 1] + values[values.Count / 2]) / 2, 2),
            Maximum = values.Count == 0 ? null : values.Last(),
            AlertLimit = limitsComplete && limitsConsistent && alertValues.Count == 1 ? alertValues[0] : null,
            ActionLimit = limitsComplete && limitsConsistent && actionValues.Count == 1 ? actionValues[0] : null,
            AlertCount = alertCount,
            ActionCount = actionCount,
            Unit = unitsConsistent && units.Count == 1 ? units[0] : string.Empty,
            LimitsComplete = limitsComplete,
            LimitsConsistent = limitsConsistent,
            UnitsConsistent = unitsConsistent,
            DataQuality = dataQuality
        };
    }

    private static string EvaluateQualifiedSignal(DataRow row)
    {
        decimal result = row.Field<decimal?>("ResultValue")!.Value;
        decimal? alert = row.Field<decimal?>("AlertLimit");
        decimal? action = row.Field<decimal?>("ActionLimit");
        string qualifier = (Convert.ToString(row["ResultQualifier"], CultureInfo.InvariantCulture) ?? string.Empty).Trim();
        if (!alert.HasValue && !action.HasValue)
            return "NOT ASSESSED";
        if (string.IsNullOrWhiteSpace(qualifier))
            return action.HasValue && result > action.Value ? "ACTION" : alert.HasValue && result > alert.Value ? "ALERT" : "PASS";

        if (qualifier is "<" or "<=")
        {
            decimal? lowest = alert ?? action;
            return lowest.HasValue && result <= lowest.Value ? "PASS" : "NOT ASSESSED";
        }

        if (qualifier == ">")
        {
            if (action.HasValue && result >= action.Value) return "ACTION";
            // The lower boundary proves at least an alert excursion even when
            // the unknown true value may also exceed the action limit.
            if (alert.HasValue && result >= alert.Value) return "ALERT";
            return "NOT ASSESSED";
        }
        if (qualifier == ">=")
        {
            if (action.HasValue && result > action.Value) return "ACTION";
            if (alert.HasValue && result > alert.Value) return "ALERT";
            return "NOT ASSESSED";
        }
        return "NOT ASSESSED";
    }

    /// <summary>
    /// Atomically saves one or more QA-approved review snapshots. The same electronic
    /// signature applies to the generated packet set; every snapshot stores its own
    /// immutable source manifest and SHA-256 snapshot hash.
    /// </summary>
    public ExternalTrendReviewSaveResult SaveSignedReviewSnapshots(
        IReadOnlyList<ExternalTrendReviewSnapshotRequest> requests,
        string signedBy,
        string signatureMeaning,
        string signatureReason)
    {
        if (IsDraftMode)
            throw new InvalidOperationException("QA approval is blocked because the current trend was generated from a staged import that is still Pending Approval. Approve the import batch, refresh the selector, regenerate the review, then perform QA snapshot approval.");

        if (requests == null || requests.Count == 0)
            throw new InvalidOperationException("At least one generated trend review is required before electronic approval.");
        if (string.IsNullOrWhiteSpace(signedBy))
            throw new UnauthorizedAccessException("An authenticated user is required for external trend review approval.");
        if (string.IsNullOrWhiteSpace(signatureMeaning))
            throw new InvalidOperationException("Electronic signature meaning is required.");
        if (string.IsNullOrWhiteSpace(signatureReason) || signatureReason.Trim().Length < 5)
            throw new InvalidOperationException("A meaningful electronic-signature reason is required.");

        foreach (ExternalTrendReviewSnapshotRequest request in requests)
            ValidateSnapshotRequest(request);

        List<long> snapshotIds = new();
        string reviewerRole = string.Empty;
        DateTimeOffset signedAt = default;

        DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
        {
            reviewerRole = DatabaseHelper.EnsureQaApprovalAuthorizationInTransaction(
                connection,
                transaction,
                signedBy.Trim(),
                "approve external environmental-monitoring trend review snapshots");

            using (SqlCommand clock = new("SELECT SYSDATETIMEOFFSET();", connection, transaction))
                signedAt = (DateTimeOffset)clock.ExecuteScalar()!;

            Dictionary<string, Dictionary<string, SourceBatchManifest>> manifestCache = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, string>> unitCache = new(StringComparer.OrdinalIgnoreCase);

            foreach (ExternalTrendReviewSnapshotRequest request in requests)
            {
                string cacheKey = request.MethodName.Trim() + "\u001F" + request.ParameterName.Trim();
                DateTime requestFrom = request.Periods.Min(period => period.From.Date);
                DateTime requestTo = request.Periods.Max(period => period.To.Date);
                string scopedCacheKey = cacheKey + "\u001F" + requestFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\u001F" + requestTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!manifestCache.TryGetValue(scopedCacheKey, out Dictionary<string, SourceBatchManifest>? manifests))
                {
                    manifests = ReadSourceManifestMapInTransaction(connection, transaction, request.MethodName, request.ParameterName, requestFrom, requestTo);
                    manifestCache[scopedCacheKey] = manifests;
                }
                if (!manifests.TryGetValue(request.AreaCode.Trim(), out SourceBatchManifest? sourceManifest))
                    throw new InvalidOperationException($"No approved external source batch is available for {request.AreaCode} / {request.MethodName} / {request.ParameterName}.");

                if (!sourceManifest.Sha256.Equals(request.ExpectedSourceManifestSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Approved external source data changed after the review was generated for {request.AreaCode} / {request.MethodName} / {request.ParameterName}. Regenerate the review before approval.");
                }

                if (!unitCache.TryGetValue(scopedCacheKey, out Dictionary<string, string>? unitsByArea))
                {
                    unitsByArea = ReadControlledUnitMapInTransaction(connection, transaction, request.MethodName, request.ParameterName, requestFrom, requestTo);
                    unitCache[scopedCacheKey] = unitsByArea;
                }
                if (!unitsByArea.TryGetValue(request.AreaCode.Trim(), out string? unitName))
                    throw new InvalidOperationException($"The approved external source does not contain a controlled unit for {request.AreaCode}.");

                string summaryJson = JsonSerializer.Serialize(new
                {
                    summaries = request.Summaries,
                    observations = request.Observations,
                    narrative = request.Narrative
                });
                string periodDefinitionJson = JsonSerializer.Serialize(request.Periods.Select(period => new
                {
                    from = period.From.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    to = period.To.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                }));

                string reviewerComment = request.ReviewerComment.Trim();
                string canonicalSnapshot = JsonSerializer.Serialize(new
                {
                    areaCode = request.AreaCode.Trim(),
                    methodName = request.MethodName.Trim(),
                    parameterName = request.ParameterName.Trim(),
                    unitName,
                    periodDefinitionJson,
                    summaryJson,
                    reviewerComment,
                    sourceManifestSha256 = sourceManifest.Sha256,
                    sourceCutoffAt = signedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    signedBy = signedBy.Trim(),
                    reviewerRole,
                    signatureMeaning = signatureMeaning.Trim(),
                    signatureReason = signatureReason.Trim(),
                    signedAt = signedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                });
                string snapshotHash = Sha256Hex(canonicalSnapshot);

                long snapshotId = InsertSignedSnapshot(
                    connection,
                    transaction,
                    request,
                    unitName,
                    summaryJson,
                    periodDefinitionJson,
                    sourceManifest,
                    snapshotHash,
                    signedBy.Trim(),
                    reviewerRole,
                    signatureMeaning.Trim(),
                    signatureReason.Trim(),
                    signedAt);
                snapshotIds.Add(snapshotId);

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection,
                    transaction,
                    "EMTrendReviewSnapshots",
                    checked((int)snapshotId),
                    "External EM Trend Review Approved",
                    string.Empty,
                    "Electronically Approved",
                    signatureReason.Trim(),
                    signedBy.Trim(),
                    "SnapshotHashSha256",
                    request.MethodName + " - " + request.ParameterName,
                    null,
                    "External Trend");
            }
        });

        return new ExternalTrendReviewSaveResult(snapshotIds.Count, signedBy.Trim(), reviewerRole, signedAt, snapshotIds);
    }

    private static long InsertSignedSnapshot(
        SqlConnection connection,
        SqlTransaction transaction,
        ExternalTrendReviewSnapshotRequest request,
        string unitName,
        string summaryJson,
        string periodDefinitionJson,
        SourceBatchManifest sourceManifest,
        string snapshotHash,
        string signedBy,
        string reviewerRole,
        string signatureMeaning,
        string signatureReason,
        DateTimeOffset signedAt)
    {
        TrendReviewPeriodSnapshot p1 = request.Periods[0];
        TrendReviewPeriodSnapshot? p2 = request.Periods.Count > 1 ? request.Periods[1] : null;
        TrendReviewPeriodSnapshot? p3 = request.Periods.Count > 2 ? request.Periods[2] : null;

        using SqlCommand command = new(@"
INSERT dbo.EMTrendReviewSnapshots
(
    AreaCode, ParameterName, MethodName, UnitName,
    Period1Start, Period1End, Period2Start, Period2End, Period3Start, Period3End,
    PeriodCount, PeriodDefinitionJson, SummaryJson, ReviewerComment,
    SourceAnchorBatchID, SourceAnchorImportNumber, SourceCutoffAt,
    SourceBatchManifestJson, SourceBatchManifestSha256, SnapshotHashSha256,
    ReviewerRole, SignatureMeaning, SignatureReason, SignedAt, CreatedBy
)
OUTPUT INSERTED.EMTrendReviewSnapshotID
VALUES
(
    @AreaCode, @ParameterName, @MethodName, @UnitName,
    @P1From, @P1To, @P2From, @P2To, @P3From, @P3To,
    @PeriodCount, @PeriodDefinitionJson, @SummaryJson, @ReviewerComment,
    @SourceAnchorBatchID, @SourceAnchorImportNumber, @SourceCutoffAt,
    @SourceBatchManifestJson, @SourceBatchManifestSha256, @SnapshotHashSha256,
    @ReviewerRole, @SignatureMeaning, @SignatureReason, @SignedAt, @CreatedBy
);", connection, transaction);
        command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
        command.Parameters.Add("@AreaCode", SqlDbType.NVarChar, 150).Value = request.AreaCode.Trim();
        command.Parameters.Add("@ParameterName", SqlDbType.NVarChar, 300).Value = request.ParameterName.Trim();
        command.Parameters.Add("@MethodName", SqlDbType.NVarChar, 200).Value = request.MethodName.Trim();
        command.Parameters.Add("@UnitName", SqlDbType.NVarChar, 100).Value = unitName;
        command.Parameters.Add("@P1From", SqlDbType.Date).Value = p1.From.Date;
        command.Parameters.Add("@P1To", SqlDbType.Date).Value = p1.To.Date;
        command.Parameters.Add("@P2From", SqlDbType.Date).Value = p2 == null ? DBNull.Value : p2.From.Date;
        command.Parameters.Add("@P2To", SqlDbType.Date).Value = p2 == null ? DBNull.Value : p2.To.Date;
        command.Parameters.Add("@P3From", SqlDbType.Date).Value = p3 == null ? DBNull.Value : p3.From.Date;
        command.Parameters.Add("@P3To", SqlDbType.Date).Value = p3 == null ? DBNull.Value : p3.To.Date;
        command.Parameters.Add("@PeriodCount", SqlDbType.Int).Value = request.Periods.Count;
        command.Parameters.Add("@PeriodDefinitionJson", SqlDbType.NVarChar, -1).Value = periodDefinitionJson;
        command.Parameters.Add("@SummaryJson", SqlDbType.NVarChar, -1).Value = summaryJson;
        command.Parameters.Add("@ReviewerComment", SqlDbType.NVarChar, 2000).Value = request.ReviewerComment.Trim();
        command.Parameters.Add("@SourceAnchorBatchID", SqlDbType.Int).Value = sourceManifest.AnchorBatchId;
        command.Parameters.Add("@SourceAnchorImportNumber", SqlDbType.NVarChar, 50).Value = sourceManifest.AnchorImportNumber;
        command.Parameters.Add("@SourceCutoffAt", SqlDbType.DateTimeOffset).Value = signedAt;
        command.Parameters.Add("@SourceBatchManifestJson", SqlDbType.NVarChar, -1).Value = sourceManifest.Json;
        command.Parameters.Add("@SourceBatchManifestSha256", SqlDbType.Char, 64).Value = sourceManifest.Sha256;
        command.Parameters.Add("@SnapshotHashSha256", SqlDbType.Char, 64).Value = snapshotHash;
        command.Parameters.Add("@ReviewerRole", SqlDbType.NVarChar, 100).Value = reviewerRole;
        command.Parameters.Add("@SignatureMeaning", SqlDbType.NVarChar, 300).Value = signatureMeaning;
        command.Parameters.Add("@SignatureReason", SqlDbType.NVarChar, 1000).Value = signatureReason;
        command.Parameters.Add("@SignedAt", SqlDbType.DateTimeOffset).Value = signedAt;
        command.Parameters.Add("@CreatedBy", SqlDbType.NVarChar, 128).Value = signedBy;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, SourceBatchManifest> ReadSourceManifestMapInTransaction(
        SqlConnection connection,
        SqlTransaction transaction,
        string methodName,
        string parameterName,
        DateTime from,
        DateTime to)
    {
        using SqlCommand command = new(SourceManifestBatchQuery(withLock: true, methodName), connection, transaction);
        command.CommandTimeout = ExternalTrendReviewTimeoutSeconds;
        foreach (SqlParameter parameter in MethodParameterParameters(methodName, parameterName,
                     new SqlParameter("@From", SqlDbType.DateTimeOffset) { Value = SourceCalendarBoundary(from.Date) },
                     new SqlParameter("@ToExclusive", SqlDbType.DateTimeOffset) { Value = SourceCalendarBoundary(to.Date.AddDays(1)) }))
            command.Parameters.Add(parameter);
        DataTable rows = new();
        using SqlDataReader reader = command.ExecuteReader();
        rows.Load(reader);
        return BuildSourceManifestMap(methodName, parameterName, from, to, rows);
    }

    private static Dictionary<string, string> ReadControlledUnitMapInTransaction(
        SqlConnection connection,
        SqlTransaction transaction,
        string methodName,
        string parameterName,
        DateTime from,
        DateTime to)
    {
        string methodPredicate = MethodFilterPredicate(methodName);
        using SqlCommand command = new($@"
SELECT DISTINCT r.EntityCode AS RawAreaCode, r.UnitName
FROM dbo.ExternalTrendImportRows r
INNER JOIN dbo.ExternalTrendImportBatches b WITH (HOLDLOCK)
    ON b.ImportBatchID = r.ImportBatchID
WHERE b.Status = N'Approved'
  AND b.ModuleName = N'Environmental Monitoring'
  AND b.ApprovedAt IS NOT NULL
  AND {methodPredicate}
  AND r.ParameterName = @ParameterName
  AND r.RecordDateTime >= @From
  AND r.RecordDateTime < @ToExclusive
OPTION (RECOMPILE);", connection, transaction);
        command.CommandTimeout = ExternalTrendReviewTimeoutSeconds;
        foreach (SqlParameter parameter in MethodParameterParameters(methodName, parameterName,
                     new SqlParameter("@From", SqlDbType.DateTimeOffset) { Value = SourceCalendarBoundary(from.Date) },
                     new SqlParameter("@ToExclusive", SqlDbType.DateTimeOffset) { Value = SourceCalendarBoundary(to.Date.AddDays(1)) }))
            command.Parameters.Add(parameter);

        Dictionary<string, HashSet<string>> units = new(StringComparer.OrdinalIgnoreCase);
        using SqlDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string canonicalArea = CanonicalizeAreaCode(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
            string unit = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
            if (string.IsNullOrWhiteSpace(unit)) continue;
            if (!units.TryGetValue(canonicalArea, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                units[canonicalArea] = set;
            }
            set.Add(unit);
        }

        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string areaCode, HashSet<string> set) in units)
        {
            if (set.Count > 1)
                throw new InvalidOperationException($"The approved external source contains mixed units for {areaCode}. Normalize the controlled source before review approval.");
            result[areaCode] = set.Single();
        }
        return result;
    }

    private static void ValidateIdentity(string areaCode, string methodName, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(areaCode))
            throw new InvalidOperationException("Area code is required for a signed external trend review.");
        if (string.IsNullOrWhiteSpace(methodName))
            throw new InvalidOperationException("Method is required for a signed external trend review.");
        if (string.IsNullOrWhiteSpace(parameterName))
            throw new InvalidOperationException("Parameter is required for a signed external trend review.");
    }

    private static void ValidateSnapshotRequest(ExternalTrendReviewSnapshotRequest request)
    {
        if (request == null)
            throw new InvalidOperationException("Trend review request is missing.");
        ValidateIdentity(request.AreaCode, request.MethodName, request.ParameterName);
        if (request.Periods == null || request.Periods.Count < 1 || request.Periods.Count > 3)
            throw new InvalidOperationException("A signed external EM trend review must contain one to three selected review periods.");
        if (request.Periods.Any(period => period.To.Date < period.From.Date))
            throw new InvalidOperationException("A review period end date cannot precede its start date.");
        if (request.Summaries == null)
            throw new InvalidOperationException("Trend summary data is required.");
        if (request.Observations == null)
            throw new InvalidOperationException("Trend observation snapshot data is required.");
        if (string.IsNullOrWhiteSpace(request.Narrative))
            throw new InvalidOperationException("The generated review narrative is required.");
        if (string.IsNullOrWhiteSpace(request.ReviewerComment) || request.ReviewerComment.Trim().Length < 10)
            throw new InvalidOperationException("Enter a QA reviewer comment of at least 10 characters before electronic approval.");
        if (string.IsNullOrWhiteSpace(request.ExpectedSourceManifestSha256) || request.ExpectedSourceManifestSha256.Trim().Length != 64)
            throw new InvalidOperationException("The generated review does not contain a valid approved-source fingerprint. Regenerate the review.");
    }

    private static string Sha256Hex(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    private sealed record ObservationCandidate(DataRow Row, string CanonicalArea, string RawAreaIdentity, string Fingerprint);

    private sealed record SourceBatchDescriptor(
        int ImportBatchID,
        string ImportNumber,
        string SourceSystem,
        string OriginalFileName,
        string FileHashSha256,
        DateTimeOffset ApprovedAt,
        long RelevantRowCount);

    private sealed record SourceBatchManifest(int AnchorBatchId, string AnchorImportNumber, string Json, string Sha256);
}

public sealed record ExternalTrendImportAvailability(
    int ApprovedBatches,
    int PendingBatches,
    int RejectedBatches,
    long ApprovedNumericRows,
    long PendingNumericRows,
    DateTimeOffset? LatestApprovedAt,
    DateTimeOffset? LatestPendingAt);

public sealed record ExternalTrendGenerationData(DataTable Rows, IReadOnlyDictionary<string, string> SourceManifestSha256ByArea);

public sealed class TrendCycleSummary
{
    public string Cycle { get; init; } = string.Empty;
    public int Count { get; init; }
    public int ExactCount { get; init; }
    public int CensoredCount { get; init; }
    public int QualifiedNeedsReviewCount { get; init; }
    public decimal? Minimum { get; init; }
    public decimal? Mean { get; init; }
    public decimal? Median { get; init; }
    public decimal? Maximum { get; init; }
    public decimal? AlertLimit { get; init; }
    public decimal? ActionLimit { get; init; }
    public int AlertCount { get; init; }
    public int ActionCount { get; init; }
    public string Unit { get; init; } = string.Empty;
    public bool LimitsComplete { get; init; } = true;
    public bool LimitsConsistent { get; init; } = true;
    public bool UnitsConsistent { get; init; } = true;
    public string DataQuality { get; init; } = "Complete";
    public string Signal => Count == 0
        ? "No Data"
        : !LimitsComplete || !UnitsConsistent
            ? "Not Assessed"
            : ActionCount > 0
                ? "Action"
                : AlertCount > 0
                    ? "Alert"
                    : QualifiedNeedsReviewCount > 0
                        ? "Not Assessed"
                        : "Normal";
    public decimal AlertRate => Count == 0 ? 0 : Math.Round((decimal)AlertCount * 100 / Count, 1);
    public decimal ActionRate => Count == 0 ? 0 : Math.Round((decimal)ActionCount * 100 / Count, 1);
}

public sealed record TrendReviewPeriodSnapshot(DateTime From, DateTime To);

public sealed record ExternalTrendObservationSnapshot(
    DateTime RecordDate,
    decimal Result,
    string ResultQualifier,
    decimal? AlertLimit,
    decimal? ActionLimit);

public sealed record ExternalTrendReviewSnapshotRequest(
    string AreaCode,
    string MethodName,
    string ParameterName,
    IReadOnlyList<TrendReviewPeriodSnapshot> Periods,
    IReadOnlyList<TrendCycleSummary> Summaries,
    IReadOnlyList<ExternalTrendObservationSnapshot> Observations,
    string Narrative,
    string ReviewerComment,
    string ExpectedSourceManifestSha256);

public sealed record ExternalTrendReviewSaveResult(
    int SnapshotCount,
    string SignedBy,
    string ReviewerRole,
    DateTimeOffset SignedAt,
    IReadOnlyList<long> SnapshotIds);
