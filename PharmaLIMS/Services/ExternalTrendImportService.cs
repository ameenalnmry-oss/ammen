using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace PharmaLIMS.Services
{
    public sealed class ExternalTrendImportService
    {
        private const long MaximumFileSizeBytes = 25L * 1024L * 1024L;
        private const long MaximumArchiveExpandedBytes = 80L * 1024L * 1024L;
        private const long MaximumWorksheetXmlBytes = 20L * 1024L * 1024L;
        private const long MaximumSharedStringsXmlBytes = 12L * 1024L * 1024L;
        private const int MaximumCompressionRatio = 200;
        private const int MaximumDataRows = 10000;
        private const int MaximumColumns = 128;
        private const int MaximumCellCharacters = 4000;
        private const int MaximumSharedStrings = 100000;
        private const int MaximumWorksheets = 20;
        private const int ReadCommandTimeoutSeconds = 8;
        private const int ReadLockTimeoutMilliseconds = 4000;
        private static readonly TimeSpan[] ReadRetryDelays =
        {
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(900)
        };

        private readonly DatabaseConnection readConnection = new();

        private static readonly string[] AllowedModules =
        {
            "Water",
            "Environmental Monitoring"
        };

        private static readonly Dictionary<string, string> HeaderAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["recorddate"] = "RecordDate",
            ["date"] = "RecordDate",
            ["samplingdate"] = "RecordDate",
            ["samplingdatetime"] = "RecordDate",
            ["collectiondate"] = "RecordDate",
            ["collectiondatetime"] = "RecordDate",
            ["entitycode"] = "EntityCode",
            ["pointcode"] = "EntityCode",
            ["samplingpoint"] = "EntityCode",
            ["areacode"] = "EntityCode",
            ["area"] = "EntityCode",
            ["locationcode"] = "EntityCode",
            ["entityname"] = "EntityName",
            ["pointname"] = "EntityName",
            ["areaname"] = "EntityName",
            ["location"] = "Location",
            ["locationname"] = "Location",
            ["areaclassification"] = "AreaClassification",
            ["classification"] = "AreaClassification",
            ["areaclass"] = "AreaClassification",
            ["classifiedstatus"] = "AreaClassification",
            ["grade"] = "AreaClassification",
            ["method"] = "Method",
            ["methodname"] = "Method",
            ["parameter"] = "Parameter",
            ["parametername"] = "Parameter",
            ["test"] = "Parameter",
            ["testname"] = "Parameter",
            ["result"] = "Result",
            ["resultvalue"] = "Result",
            ["value"] = "Result",
            ["unit"] = "Unit",
            ["unitname"] = "Unit",
            ["alertlimit"] = "AlertLimit",
            ["alert"] = "AlertLimit",
            ["actionlimit"] = "ActionLimit",
            ["action"] = "ActionLimit",
            ["sourcerecordid"] = "SourceRecordID",
            ["recordid"] = "SourceRecordID",
            ["externalid"] = "SourceRecordID",
            ["remarks"] = "Remarks",
            ["remark"] = "Remarks",
            ["comments"] = "Remarks",
            ["comment"] = "Remarks"
        };

        private static readonly string[] RequiredColumns =
        {
            "RecordDate",
            "EntityCode",
            "Parameter",
            "Result",
            "Unit"
        };

        public TrendImportPreview ParseFile(string path, string moduleName)
        {
            ValidateModule(moduleName);

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("The selected import file was not found.", path);

            FileInfo file = new(path);
            if (file.Length <= 0)
                throw new InvalidOperationException("The selected import file is empty.");
            if (file.Length > MaximumFileSizeBytes)
                throw new InvalidOperationException("The import file exceeds the controlled 25 MB limit.");

            string extension = file.Extension.ToLowerInvariant();
            if (extension is not ".csv" and not ".xlsx")
                throw new InvalidOperationException("Only CSV and XLSX files are accepted. XLSM, XLS, PDF and password-protected files are not allowed.");

            byte[] bytes = File.ReadAllBytes(path);
            int cachedFormulaCellCount = 0;
            List<List<string>> matrix = extension == ".csv"
                ? ReadCsv(bytes)
                : ReadXlsx(bytes, out cachedFormulaCellCount);

            if (matrix.Count < 2)
                throw new InvalidOperationException("The import file must contain one header row and at least one data row.");
            if (matrix.Count - 1 > MaximumDataRows)
                throw new InvalidOperationException($"The import file contains more than {MaximumDataRows:N0} data rows.");

            Dictionary<string, int> columns = ResolveColumns(matrix[0]);
            var preview = new TrendImportPreview
            {
                SourcePath = path,
                FileName = file.Name,
                FileExtension = extension,
                FileSizeBytes = file.Length,
                FileHashSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                OriginalFile = bytes,
                ModuleName = moduleName
            };

            if (cachedFormulaCellCount > 0)
            {
                preview.Issues.Add(new TrendImportValidationIssue
                {
                    Severity = "Warning",
                    RowNumber = 0,
                    Message = $"{cachedFormulaCellCount:N0} formula cell(s) were read from their cached displayed values. The original workbook and SHA-256 hash are retained as source evidence; review the preview before staging."
                });
            }

            foreach (string required in RequiredColumns)
            {
                if (!columns.ContainsKey(required))
                {
                    preview.Issues.Add(new TrendImportValidationIssue
                    {
                        Severity = "Error",
                        RowNumber = 1,
                        Message = $"Required column '{required}' was not found. Download and use the controlled template."
                    });
                }
            }

            if (moduleName == "Environmental Monitoring" && !columns.ContainsKey("AreaClassification"))
            {
                preview.Issues.Add(new TrendImportValidationIssue
                {
                    Severity = "Error",
                    RowNumber = 1,
                    Message = "Required column 'AreaClassification' was not found. Use Classified / Grade D / ISO 8 or Unclassified for every EM row."
                });
            }

            if (moduleName == "Environmental Monitoring" && !columns.ContainsKey("Method"))
            {
                preview.Issues.Add(new TrendImportValidationIssue
                {
                    Severity = "Error",
                    RowNumber = 1,
                    Message = "Required column 'Method' was not found. Environmental Monitoring trend rows must identify the sampling method (for example Settle Plate or Active Air Sampling)."
                });
            }

            if (preview.HasErrors)
                return preview;

            var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rowFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DateTime authoritativeCalendarDate = GetAuthoritativeDatabaseTimestamp().Date;

            for (int index = 1; index < matrix.Count; index++)
            {
                int sourceRowNumber = index + 1;
                List<string> values = matrix[index];
                if (values.All(string.IsNullOrWhiteSpace))
                    continue;

                if (values.Any(LooksLikeSpreadsheetFormula))
                {
                    preview.Issues.Add(new TrendImportValidationIssue
                    {
                        Severity = "Error",
                        RowNumber = sourceRowNumber,
                        Message = "Formula-like CSV content is not accepted. Import values only."
                    });
                    continue;
                }

                TrendImportRow? row = ParseRow(values, columns, sourceRowNumber, moduleName, authoritativeCalendarDate, preview.Issues);
                if (row == null)
                    continue;

                if (!string.IsNullOrWhiteSpace(row.SourceRecordID) && !sourceIds.Add(row.SourceRecordID))
                {
                    preview.Issues.Add(new TrendImportValidationIssue
                    {
                        Severity = "Error",
                        RowNumber = sourceRowNumber,
                        Message = $"Duplicate SourceRecordID '{row.SourceRecordID}' exists in the file."
                    });
                    continue;
                }

                string fingerprint = string.Join("|",
                    row.RecordDateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    row.EntityCode.Trim().ToUpperInvariant(),
                    row.AreaClassification.Trim().ToUpperInvariant(),
                    (row.MethodName ?? string.Empty).Trim().ToUpperInvariant(),
                    row.ParameterName.Trim().ToUpperInvariant(),
                    row.ResultQualifier ?? string.Empty,
                    row.ResultValue.ToString(CultureInfo.InvariantCulture),
                    row.UnitName.Trim().ToUpperInvariant());

                // A unique external record ID is the controlled identity for legitimate
                // replicate measurements. Use the value fingerprint only when the source
                // did not provide a record ID.
                if (string.IsNullOrWhiteSpace(row.SourceRecordID) && !rowFingerprints.Add(fingerprint))
                {
                    preview.Issues.Add(new TrendImportValidationIssue
                    {
                        Severity = "Error",
                        RowNumber = sourceRowNumber,
                        Message = "A duplicate date/entity/parameter/result row exists in the same file."
                    });
                    continue;
                }

                preview.Rows.Add(row);
            }

            foreach (IGrouping<string, TrendImportRow> parameterRows in preview.Rows.GroupBy(
                         row => row.ParameterName.Trim() + "|" + row.AreaClassification.Trim() + "|" + (row.MethodName ?? string.Empty).Trim(),
                         StringComparer.OrdinalIgnoreCase))
            {
                TrendImportRow firstRow = parameterRows.First();
                string[] units = parameterRows.Select(row => row.UnitName.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (units.Length > 1)
                {
                    preview.Issues.Add(new TrendImportValidationIssue
                    {
                        Severity = "Error",
                        RowNumber = 0,
                        Message = $"Parameter '{firstRow.ParameterName}' / method '{firstRow.MethodName}' in '{firstRow.AreaClassification}' contains inconsistent units: {string.Join(", ", units)}. Split or correct the source data."
                    });
                }

                decimal?[] alertLimits = parameterRows.Select(row => row.AlertLimit).Distinct().ToArray();
                decimal?[] actionLimits = parameterRows.Select(row => row.ActionLimit).Distinct().ToArray();
                if (alertLimits.Length > 1 || actionLimits.Length > 1)
                {
                    preview.Issues.Add(new TrendImportValidationIssue
                    {
                        Severity = "Warning",
                        RowNumber = 0,
                        Message = $"Parameter '{firstRow.ParameterName}' / method '{firstRow.MethodName}' in '{firstRow.AreaClassification}' contains changing alert/action limits. Row-specific historical limits are retained; the trend review will flag the limit change for QA reconciliation rather than forcing one limit across the full period."
                    });
                }
            }

            if (preview.Rows.Count == 0)
            {
                preview.Issues.Add(new TrendImportValidationIssue
                {
                    Severity = "Error",
                    RowNumber = 0,
                    Message = "No valid numeric trend rows were found."
                });
            }

            if (!columns.ContainsKey("SourceRecordID"))
            {
                preview.Issues.Add(new TrendImportValidationIssue
                {
                    Severity = "Warning",
                    RowNumber = 1,
                    Message = "SourceRecordID is not present. File-hash duplicate protection remains active, but cross-file source-record reconciliation is limited."
                });
            }

            return preview;
        }

        public TrendImportStageResult StageImport(TrendImportPreview preview, string sourceSystem, string reason, string importedBy)
        {
            ArgumentNullException.ThrowIfNull(preview);
            ValidateModule(preview.ModuleName);

            if (preview.HasErrors || preview.Rows.Count == 0)
                throw new InvalidOperationException("The file contains validation errors and cannot be staged.");
            if (string.IsNullOrWhiteSpace(sourceSystem))
                throw new InvalidOperationException("Source System is required.");
            if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
                throw new InvalidOperationException("Enter a meaningful import reason of at least 10 characters.");
            if (string.IsNullOrWhiteSpace(importedBy))
                throw new InvalidOperationException("An authenticated user is required.");

            int batchId = 0;
            string? existingImportNumber = null;
            string? existingStatus = null;
            DateTimeOffset now = GetAuthoritativeDatabaseTimestamp();
            string importNumber = $"TRI-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..32].ToUpperInvariant();
            string importedRole = DatabaseHelper.GetUserRole(importedBy.Trim());
            if (!RoleIs(importedRole, "Admin") && !RoleIs(importedRole, "Administrator") &&
                !RoleIs(importedRole, "QA") && !RoleIs(importedRole, "Supervisor") &&
                !RoleIs(importedRole, "Analyst"))
                throw new InvalidOperationException("The authenticated account is not authorized to stage external trend data.");

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                using (SqlCommand duplicate = new(@"
SELECT TOP (1) ImportBatchID, ImportNumber, Status
FROM dbo.ExternalTrendImportBatches WITH (UPDLOCK, HOLDLOCK)
WHERE FileHashSha256 = @Hash;", connection, transaction))
                {
                    duplicate.Parameters.Add("@Hash", SqlDbType.Char, 64).Value = preview.FileHashSha256;
                    using SqlDataReader reader = duplicate.ExecuteReader();
                    if (reader.Read())
                    {
                        batchId = reader.GetInt32(0);
                        existingImportNumber = reader.GetString(1);
                        existingStatus = reader.GetString(2);
                        return;
                    }
                }

                using (SqlCommand insertBatch = new(@"
INSERT dbo.ExternalTrendImportBatches
(
    ImportNumber, ModuleName, SourceSystem, OriginalFileName, FileExtension,
    FileSizeBytes, FileHashSha256, OriginalFile, ImportReason, [RowCount],
    Status, ImportedBy, ImportedRole, ImportedAt, SourceWorkstation
)
OUTPUT INSERTED.ImportBatchID
VALUES
(
    @ImportNumber, @ModuleName, @SourceSystem, @OriginalFileName, @FileExtension,
    @FileSizeBytes, @FileHash, @OriginalFile, @ImportReason, @RowCount,
    N'Pending Approval', @ImportedBy, @ImportedRole, @ImportedAt, @Workstation
);", connection, transaction))
                {
                    insertBatch.Parameters.Add("@ImportNumber", SqlDbType.NVarChar, 50).Value = importNumber;
                    insertBatch.Parameters.Add("@ModuleName", SqlDbType.NVarChar, 40).Value = preview.ModuleName;
                    insertBatch.Parameters.Add("@SourceSystem", SqlDbType.NVarChar, 150).Value = sourceSystem.Trim();
                    insertBatch.Parameters.Add("@OriginalFileName", SqlDbType.NVarChar, 260).Value = preview.FileName;
                    insertBatch.Parameters.Add("@FileExtension", SqlDbType.NVarChar, 10).Value = preview.FileExtension;
                    insertBatch.Parameters.Add("@FileSizeBytes", SqlDbType.BigInt).Value = preview.FileSizeBytes;
                    insertBatch.Parameters.Add("@FileHash", SqlDbType.Char, 64).Value = preview.FileHashSha256;
                    insertBatch.Parameters.Add("@OriginalFile", SqlDbType.VarBinary, -1).Value = preview.OriginalFile;
                    insertBatch.Parameters.Add("@ImportReason", SqlDbType.NVarChar, 1000).Value = reason.Trim();
                    insertBatch.Parameters.Add("@RowCount", SqlDbType.Int).Value = preview.Rows.Count;
                    insertBatch.Parameters.Add("@ImportedBy", SqlDbType.NVarChar, 100).Value = importedBy.Trim();
                    insertBatch.Parameters.Add("@ImportedRole", SqlDbType.NVarChar, 100).Value = importedRole;
                    insertBatch.Parameters.Add("@ImportedAt", SqlDbType.DateTimeOffset).Value = now;
                    insertBatch.Parameters.Add("@Workstation", SqlDbType.NVarChar, 200).Value = Environment.MachineName;
                    batchId = Convert.ToInt32(insertBatch.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                InsertRowsInSmallBatches(connection, transaction, preview.Rows, batchId, now);

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection,
                    transaction,
                    "ExternalTrendImportBatches",
                    batchId,
                    "External Trend Import Staged",
                    string.Empty,
                    "Pending Approval",
                    reason.Trim(),
                    importedBy.Trim(),
                    "Status",
                    null,
                    importNumber,
                    "Reports and Trends");
            });

            return new TrendImportStageResult
            {
                BatchId = batchId,
                ImportNumber = existingImportNumber ?? importNumber,
                Status = existingStatus ?? "Pending Approval",
                AlreadyExists = existingImportNumber != null
            };
        }

        private static void InsertRowsInSmallBatches(
            SqlConnection connection,
            SqlTransaction transaction,
            IReadOnlyList<TrendImportRow> rows,
            int batchId,
            DateTimeOffset createdAt)
        {
            const int rowsPerBatch = 40;

            for (int offset = 0; offset < rows.Count; offset += rowsPerBatch)
            {
                int count = Math.Min(rowsPerBatch, rows.Count - offset);
                StringBuilder sql = new(@"
INSERT dbo.ExternalTrendImportRows
(
    ImportBatchID, SourceRowNumber, SourceRecordID, RecordDateTime,
    EntityCode, EntityName, AreaClassification, LocationName, MethodName,
    ParameterName, ResultValue, ResultQualifier, UnitName, AlertLimit, ActionLimit,
    ResultStatus, Remarks, CreatedAt
)
VALUES
");

                using SqlCommand command = new()
                {
                    Connection = connection,
                    Transaction = transaction,
                    CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 60)
                };

                for (int index = 0; index < count; index++)
                {
                    TrendImportRow row = rows[offset + index];
                    string suffix = index.ToString(CultureInfo.InvariantCulture);

                    if (index > 0)
                        sql.AppendLine(",");

                    sql.Append($"(@Batch{suffix}, @Row{suffix}, @Source{suffix}, @Date{suffix}, " +
                               $"@Code{suffix}, @Name{suffix}, @Class{suffix}, @Location{suffix}, @Method{suffix}, " +
                               $"@Parameter{suffix}, @Result{suffix}, @Qualifier{suffix}, @Unit{suffix}, @Alert{suffix}, @Action{suffix}, " +
                               $"@Status{suffix}, @Remarks{suffix}, @Created{suffix})");

                    command.Parameters.Add($"@Batch{suffix}", SqlDbType.Int).Value = batchId;
                    command.Parameters.Add($"@Row{suffix}", SqlDbType.Int).Value = row.SourceRowNumber;
                    command.Parameters.Add($"@Source{suffix}", SqlDbType.NVarChar, 150).Value = DbValue(row.SourceRecordID);
                    command.Parameters.Add($"@Date{suffix}", SqlDbType.DateTimeOffset).Value = row.RecordDateTime;
                    command.Parameters.Add($"@Code{suffix}", SqlDbType.NVarChar, 150).Value = row.EntityCode;
                    command.Parameters.Add($"@Name{suffix}", SqlDbType.NVarChar, 300).Value = DbValue(row.EntityName);
                    command.Parameters.Add($"@Class{suffix}", SqlDbType.NVarChar, 30).Value = row.AreaClassification;
                    command.Parameters.Add($"@Location{suffix}", SqlDbType.NVarChar, 300).Value = DbValue(row.LocationName);
                    command.Parameters.Add($"@Method{suffix}", SqlDbType.NVarChar, 200).Value = DbValue(row.MethodName);
                    command.Parameters.Add($"@Parameter{suffix}", SqlDbType.NVarChar, 300).Value = row.ParameterName;

                    SqlParameter result = command.Parameters.Add($"@Result{suffix}", SqlDbType.Decimal);
                    result.Precision = 38;
                    result.Scale = 10;
                    result.Value = row.ResultValue;
                    command.Parameters.Add($"@Qualifier{suffix}", SqlDbType.NVarChar, 2).Value = DbValue(row.ResultQualifier);

                    command.Parameters.Add($"@Unit{suffix}", SqlDbType.NVarChar, 100).Value = row.UnitName;

                    SqlParameter alert = command.Parameters.Add($"@Alert{suffix}", SqlDbType.Decimal);
                    alert.Precision = 38;
                    alert.Scale = 10;
                    alert.Value = row.AlertLimit.HasValue ? row.AlertLimit.Value : DBNull.Value;

                    SqlParameter action = command.Parameters.Add($"@Action{suffix}", SqlDbType.Decimal);
                    action.Precision = 38;
                    action.Scale = 10;
                    action.Value = row.ActionLimit.HasValue ? row.ActionLimit.Value : DBNull.Value;

                    command.Parameters.Add($"@Status{suffix}", SqlDbType.NVarChar, 20).Value = row.ResultStatus;
                    command.Parameters.Add($"@Remarks{suffix}", SqlDbType.NVarChar, 1000).Value = DbValue(row.Remarks);
                    command.Parameters.Add($"@Created{suffix}", SqlDbType.DateTimeOffset).Value = createdAt;
                }

                sql.Append(';');
                command.CommandText = sql.ToString();
                command.ExecuteNonQuery();
            }
        }

        private static object DbValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
        }

        public void ApproveImport(int batchId, string approvedBy, string meaning, string reason)
        {
            ChangeStatus(batchId, "Approved", approvedBy, meaning, reason);
        }

        public void RejectImport(int batchId, string rejectedBy, string meaning, string reason)
        {
            ChangeStatus(batchId, "Rejected", rejectedBy, meaning, reason);
        }

        private static bool IsTransientReadContention(SqlException exception) =>
            exception.Number == -2 || exception.Number == 1222;

        private async Task<DataTable> ExecuteReadWithRetryAsync(
            string query,
            SqlParameter[]? parameters = null,
            CancellationToken cancellationToken = default)
        {
            SqlException? lastSqlException = null;
            string controlledQuery = $"SET LOCK_TIMEOUT {ReadLockTimeoutMilliseconds};\n" + query;

            for (int attempt = 0; attempt < ReadRetryDelays.Length; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TimeSpan delay = ReadRetryDelays[attempt];
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                try
                {
                    return await readConnection.ExecuteQueryAsync(
                        controlledQuery,
                        parameters,
                        ReadCommandTimeoutSeconds,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (SqlException ex) when (IsTransientReadContention(ex) && attempt + 1 < ReadRetryDelays.Length)
                {
                    lastSqlException = ex;
                    ApplicationLogger.Warning(
                        $"External Trend read encountered transient SQL contention. Retry {attempt + 1} of {ReadRetryDelays.Length - 1}.",
                        ex);
                }
            }

            if (lastSqlException != null)
                throw lastSqlException;

            throw new InvalidOperationException("External Trend read did not complete.");
        }

        public Task<DataTable> GetImportHistoryAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteReadWithRetryAsync(@"
SELECT TOP (250)
    ImportBatchID,
    ImportNumber,
    ModuleName AS Module,
    SourceSystem,
    OriginalFileName,
    [RowCount] AS [RowCount],
    Status,
    ImportedBy,
    ImportedRole,
    ImportedAt,
    ApprovedBy,
    ApprovedRole,
    ApprovedAt,
    RejectedBy,
    RejectedRole,
    RejectedAt,
    FileHashSha256
FROM dbo.ExternalTrendImportBatches WITH (READPAST)
ORDER BY ImportBatchID DESC;", cancellationToken: cancellationToken);
        }

        public Task<DataTable> GetBatchMethodsAsync(int batchId, CancellationToken cancellationToken = default)
        {
            return ExecuteReadWithRetryAsync(@"
SELECT DISTINCT COALESCE(NULLIF(MethodName, N''), N'Unspecified method') AS MethodName
FROM dbo.ExternalTrendImportRows WITH (READPAST)
WHERE ImportBatchID = @BatchID
ORDER BY MethodName;",
                new[] { new SqlParameter("@BatchID", SqlDbType.Int) { Value = batchId } },
                cancellationToken);
        }

        public async Task<IReadOnlyList<string>> GetBatchParametersAsync(
            int batchId,
            string? methodName = null,
            CancellationToken cancellationToken = default)
        {
            DataTable table = await ExecuteReadWithRetryAsync(@"
SELECT DISTINCT ParameterName
FROM dbo.ExternalTrendImportRows WITH (READPAST)
WHERE ImportBatchID = @BatchID
  AND (@MethodName IS NULL OR COALESCE(NULLIF(MethodName, N''), N'Unspecified method') = @MethodName)
ORDER BY ParameterName;",
                new[]
                {
                    new SqlParameter("@BatchID", SqlDbType.Int) { Value = batchId },
                    new SqlParameter("@MethodName", SqlDbType.NVarChar, 200) { Value = string.IsNullOrWhiteSpace(methodName) ? DBNull.Value : methodName }
                },
                cancellationToken).ConfigureAwait(false);

            return table.Rows.Cast<DataRow>()
                .Select(row => Convert.ToString(row["ParameterName"], CultureInfo.InvariantCulture) ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }

        public DataTable LoadApprovedTrend(int batchId, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(parameterName))
                throw new InvalidOperationException("Select a parameter to analyze.");

            return DatabaseHelper.ExecuteQuery(@"
SELECT
    ISNULL(NULLIF(r.SourceRecordID, N''), CONCAT(b.ImportNumber, N'-', r.SourceRowNumber)) AS SampleNumber,
    r.EntityCode AS PointCode,
    r.RecordDateTime AS SamplingDate,
    r.RecordDateTime AS SamplingDateTime,
    r.ParameterName AS TestName,
    r.ResultValue,
    r.ResultQualifier,
    r.UnitName AS Unit,
    r.AlertLimit,
    r.ActionLimit,
    r.ResultStatus AS Status,
    b.ImportNumber,
    b.ModuleName,
    b.SourceSystem,
    r.EntityName,
    r.AreaClassification,
    r.LocationName,
    r.MethodName,
    r.SourceRecordID,
    r.Remarks,
    b.FileHashSha256
FROM dbo.ExternalTrendImportRows r
INNER JOIN dbo.ExternalTrendImportBatches b ON b.ImportBatchID = r.ImportBatchID
WHERE r.ImportBatchID = @BatchID
  AND b.Status = N'Approved'
  AND r.ParameterName = @ParameterName
ORDER BY r.RecordDateTime, r.SourceRowNumber;",
                new[]
                {
                    new SqlParameter("@BatchID", SqlDbType.Int) { Value = batchId },
                    new SqlParameter("@ParameterName", SqlDbType.NVarChar, 300) { Value = parameterName.Trim() }
                });
        }

        public DataTable BuildPreviewTable(TrendImportPreview preview)
        {
            DataTable table = new();
            table.Columns.Add("Row", typeof(int));
            table.Columns.Add("Record Date", typeof(string));
            table.Columns.Add("Entity Code", typeof(string));
            table.Columns.Add("Entity Name", typeof(string));
            table.Columns.Add("Area Classification", typeof(string));
            table.Columns.Add("Method", typeof(string));
            table.Columns.Add("Parameter", typeof(string));
            table.Columns.Add("Result Qualifier", typeof(string));
            table.Columns.Add("Result", typeof(decimal));
            table.Columns.Add("Unit", typeof(string));
            table.Columns.Add("Alert Limit", typeof(decimal));
            table.Columns.Add("Action Limit", typeof(decimal));
            table.Columns.Add("Status", typeof(string));
            table.Columns.Add("Source Record ID", typeof(string));
            table.Columns.Add("Remarks", typeof(string));

            foreach (TrendImportRow row in preview.Rows)
            {
                DataRow data = table.NewRow();
                data["Row"] = row.SourceRowNumber;
                data["Record Date"] = row.RecordDateTime.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
                data["Entity Code"] = row.EntityCode;
                data["Entity Name"] = DbText(row.EntityName);
                data["Area Classification"] = row.AreaClassification;
                data["Method"] = DbText(row.MethodName);
                data["Parameter"] = row.ParameterName;
                data["Result Qualifier"] = DbText(row.ResultQualifier);
                data["Result"] = row.ResultValue;
                data["Unit"] = row.UnitName;
                data["Alert Limit"] = row.AlertLimit.HasValue ? row.AlertLimit.Value : DBNull.Value;
                data["Action Limit"] = row.ActionLimit.HasValue ? row.ActionLimit.Value : DBNull.Value;
                data["Status"] = row.ResultStatus;
                data["Source Record ID"] = DbText(row.SourceRecordID);
                data["Remarks"] = DbText(row.Remarks);
                table.Rows.Add(data);
            }

            return table;
        }

        public string BuildTemplate(string moduleName)
        {
            ValidateModule(moduleName);
            string method = moduleName == "Water" ? "Membrane Filtration" : "Active Air Sampling";
            string entity = moduleName == "Water" ? "PWS-01" : "D1-AA-01";
            string parameter = moduleName == "Water" ? "Total Aerobic Microbial Count" : "Viable Count";
            string unit = moduleName == "Water" ? "CFU/mL" : "CFU/m3";

            return string.Join(Environment.NewLine,
                "RecordDate,EntityCode,EntityName,Location,AreaClassification,Method,Parameter,Result,Unit,AlertLimit,ActionLimit,SourceRecordID,Remarks",
                $"{DatabaseHelper.GetAuthoritativeDatabaseTime():yyyy-MM-dd HH:mm},{entity},Example Entity,Example Location,{(moduleName == "Water" ? "Not Applicable" : "Classified")},{method},{parameter},1,{unit},5,10,EXT-0001,Template example - replace or remove this row");
        }

        private static void ChangeStatus(int batchId, string targetStatus, string user, string meaning, string reason)
        {
            if (batchId <= 0)
                throw new ArgumentOutOfRangeException(nameof(batchId));
            if (string.IsNullOrWhiteSpace(user))
                throw new InvalidOperationException("An authenticated user is required.");
            if (string.IsNullOrWhiteSpace(meaning))
                throw new InvalidOperationException("Electronic signature meaning is required.");
            if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
                throw new InvalidOperationException("A meaningful reason is required.");

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                string userRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection,
                    transaction,
                    user.Trim(),
                    "CanApproveResults",
                    targetStatus == "Approved"
                        ? "approve external trend data"
                        : "reject external trend data");

                string importNumber;
                string importedBy;
                string currentStatus;

                using (SqlCommand read = new(@"
SELECT ImportNumber, ImportedBy, Status
FROM dbo.ExternalTrendImportBatches WITH (UPDLOCK, HOLDLOCK)
WHERE ImportBatchID = @BatchID;", connection, transaction))
                {
                    read.Parameters.Add("@BatchID", SqlDbType.Int).Value = batchId;
                    using SqlDataReader reader = read.ExecuteReader();
                    if (!reader.Read())
                        throw new InvalidOperationException("The selected import batch was not found.");
                    importNumber = reader.GetString(0);
                    importedBy = reader.GetString(1);
                    currentStatus = reader.GetString(2);
                }

                if (!currentStatus.Equals("Pending Approval", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Only Pending Approval imports can be {targetStatus.ToLowerInvariant()}.");

                // Match the project-wide Development-only workflow allowance used by EM/PRM:
                // an Admin may exercise multiple workflow stages while the application is
                // compiled/running in Development with DevelopmentAdminFullPermissions enabled.
                // Production always enforces independent import approval.
                bool developmentAdminOverride = AppConfig.DevelopmentAdminFullPermissions &&
                    (userRole.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                     userRole.Equals("Administrator", StringComparison.OrdinalIgnoreCase));
                if (!developmentAdminOverride && importedBy.Equals(user.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Segregation of duties: the importer cannot approve or reject the same batch.");

                string sql = targetStatus == "Approved"
                    ? @"
UPDATE dbo.ExternalTrendImportBatches
SET Status = N'Approved', ApprovedBy = @UserName, ApprovedAt = @ActionAt,
    ApprovedRole = @UserRole, ApprovalMeaning = @Meaning, ApprovalReason = @Reason
WHERE ImportBatchID = @BatchID AND Status = N'Pending Approval';"
                    : @"
UPDATE dbo.ExternalTrendImportBatches
SET Status = N'Rejected', RejectedBy = @UserName, RejectedAt = @ActionAt,
    RejectedRole = @UserRole, RejectionMeaning = @Meaning, RejectionReason = @Reason
WHERE ImportBatchID = @BatchID AND Status = N'Pending Approval';";

                using SqlCommand update = new(sql, connection, transaction);
                update.Parameters.Add("@UserName", SqlDbType.NVarChar, 100).Value = user.Trim();
                update.Parameters.Add("@UserRole", SqlDbType.NVarChar, 100).Value = userRole;
                update.Parameters.Add("@ActionAt", SqlDbType.DateTimeOffset).Value = GetAuthoritativeDatabaseTimestamp();
                update.Parameters.Add("@Meaning", SqlDbType.NVarChar, 300).Value = meaning.Trim();
                update.Parameters.Add("@Reason", SqlDbType.NVarChar, 1000).Value = reason.Trim();
                update.Parameters.Add("@BatchID", SqlDbType.Int).Value = batchId;
                if (update.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The import status changed before this action completed. Refresh and try again.");

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection,
                    transaction,
                    "ExternalTrendImportBatches",
                    batchId,
                    "External Trend Import " + targetStatus,
                    currentStatus,
                    targetStatus,
                    reason.Trim(),
                    user.Trim(),
                    "Status",
                    null,
                    importNumber,
                    "Reports and Trends");
            });
        }

        private static TrendImportRow? ParseRow(
            IReadOnlyList<string> values,
            IReadOnlyDictionary<string, int> columns,
            int rowNumber,
            string moduleName,
            DateTime authoritativeCalendarDate,
            ICollection<TrendImportValidationIssue> issues)
        {
            string dateText = GetValue(values, columns, "RecordDate");
            string entityCode = GetValue(values, columns, "EntityCode").Trim();
            string parameter = GetValue(values, columns, "Parameter").Trim();
            string resultText = GetValue(values, columns, "Result").Trim();
            string unit = GetValue(values, columns, "Unit").Trim();
            string methodNameText = GetValue(values, columns, "Method").Trim();
            string areaClassification = GetValue(values, columns, "AreaClassification").Trim();
            bool hasError = false;

            if (!TryParseDate(dateText, out DateTimeOffset recordDate))
            {
                AddRowError(issues, rowNumber, $"RecordDate '{dateText}' is invalid. Use yyyy-MM-dd HH:mm.");
                hasError = true;
            }
            else if (recordDate.Date > authoritativeCalendarDate.Date)
            {
                AddRowError(issues, rowNumber, "RecordDate cannot be on a future calendar date.");
                hasError = true;
            }

            if (string.IsNullOrWhiteSpace(entityCode))
            {
                AddRowError(issues, rowNumber, "EntityCode is required.");
                hasError = true;
            }
            if (string.IsNullOrWhiteSpace(parameter))
            {
                AddRowError(issues, rowNumber, "Parameter is required.");
                hasError = true;
            }
            if (string.IsNullOrWhiteSpace(unit))
            {
                AddRowError(issues, rowNumber, "Unit is required. Use '-' only when the controlled method has no unit.");
                hasError = true;
            }
            if (!TryParseQualifiedDecimal(resultText, out decimal result, out string? resultQualifier))
            {
                AddRowError(issues, rowNumber, $"Result '{resultText}' is not a supported numeric result. Use a number or a controlled qualifier such as <10, <=10, >20 or >=20.");
                hasError = true;
            }


            if (moduleName == "Environmental Monitoring" && string.IsNullOrWhiteSpace(methodNameText))
            {
                AddRowError(issues, rowNumber, "Method is required for Environmental Monitoring trend rows.");
                hasError = true;
            }

            if (moduleName == "Water" && string.IsNullOrWhiteSpace(areaClassification))
                areaClassification = "Not Applicable";

            if (!TryNormalizeAreaClassification(areaClassification, out string normalizedClassification) ||
                (moduleName == "Environmental Monitoring" && normalizedClassification == "Not Applicable") ||
                (moduleName == "Water" && normalizedClassification != "Not Applicable"))
            {
                AddRowError(issues, rowNumber,
                    "AreaClassification must be Classified (Grade D / ISO 8 accepted) or Unclassified for Environmental Monitoring; use Not Applicable for Water.");
                hasError = true;
            }

            decimal? alert = ParseOptionalDecimal(GetValue(values, columns, "AlertLimit"), rowNumber, "AlertLimit", issues, ref hasError);
            decimal? action = ParseOptionalDecimal(GetValue(values, columns, "ActionLimit"), rowNumber, "ActionLimit", issues, ref hasError);

            if (alert.HasValue && action.HasValue && action.Value < alert.Value)
            {
                AddRowError(issues, rowNumber, "ActionLimit cannot be lower than AlertLimit for the supported upper-limit trend model.");
                hasError = true;
            }

            if (hasError)
                return null;

            string status = EvaluateQualifiedUpperLimitStatus(result, resultQualifier, alert, action);

            if (entityCode.Length > 150)
            {
                AddRowError(issues, rowNumber, "EntityCode exceeds 150 characters.");
                return null;
            }
            if (parameter.Length > 300)
            {
                AddRowError(issues, rowNumber, "Parameter exceeds 300 characters.");
                return null;
            }
            if (unit.Length > 100)
            {
                AddRowError(issues, rowNumber, "Unit exceeds 100 characters.");
                return null;
            }

            string? sourceRecordId = NullIfWhiteSpace(GetValue(values, columns, "SourceRecordID"));
            string? entityName = NullIfWhiteSpace(GetValue(values, columns, "EntityName"));
            string? locationName = NullIfWhiteSpace(GetValue(values, columns, "Location"));
            string? methodName = NullIfWhiteSpace(methodNameText);
            string? remarks = NullIfWhiteSpace(GetValue(values, columns, "Remarks"));
            if ((sourceRecordId?.Length ?? 0) > 150 || (entityName?.Length ?? 0) > 300 ||
                (locationName?.Length ?? 0) > 300 || (methodName?.Length ?? 0) > 200 ||
                (remarks?.Length ?? 0) > 1000)
            {
                AddRowError(issues, rowNumber, "One or more optional text values exceed the controlled field length.");
                return null;
            }

            return new TrendImportRow
            {
                SourceRowNumber = rowNumber,
                SourceRecordID = sourceRecordId,
                RecordDateTime = recordDate,
                EntityCode = entityCode,
                EntityName = entityName,
                AreaClassification = normalizedClassification,
                LocationName = locationName,
                MethodName = methodName,
                ParameterName = parameter,
                ResultValue = result,
                ResultQualifier = resultQualifier,
                UnitName = unit,
                AlertLimit = alert,
                ActionLimit = action,
                ResultStatus = status,
                Remarks = remarks
            };
        }

        private static Dictionary<string, int> ResolveColumns(IReadOnlyList<string> headers)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < headers.Count; index++)
            {
                string normalized = NormalizeHeader(headers[index]);
                if (HeaderAliases.TryGetValue(normalized, out string? canonical) && !result.ContainsKey(canonical))
                    result[canonical] = index;
            }
            return result;
        }

        private static bool TryNormalizeAreaClassification(string value, out string normalized)
        {
            string compact = Regex.Replace(value ?? string.Empty, "[^A-Za-z0-9]", string.Empty);
            if (compact.Equals("Classified", StringComparison.OrdinalIgnoreCase) ||
                compact.Equals("ClassD", StringComparison.OrdinalIgnoreCase) ||
                compact.Equals("GradeD", StringComparison.OrdinalIgnoreCase) ||
                compact.Equals("GradeDProduction", StringComparison.OrdinalIgnoreCase) ||
                compact.Equals("ISO8", StringComparison.OrdinalIgnoreCase) ||
                compact.Equals("ISO8Production", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "Classified";
                return true;
            }
            if (compact.Equals("Unclassified", StringComparison.OrdinalIgnoreCase) ||
                compact.Equals("NonClassified", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "Unclassified";
                return true;
            }
            if (compact.Equals("NotApplicable", StringComparison.OrdinalIgnoreCase) ||
                compact.Equals("NA", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "Not Applicable";
                return true;
            }

            normalized = string.Empty;
            return false;
        }

        private static string NormalizeHeader(string value) =>
            Regex.Replace(value ?? string.Empty, "[^A-Za-z0-9]", string.Empty).ToLowerInvariant();

        private static string GetValue(IReadOnlyList<string> values, IReadOnlyDictionary<string, int> columns, string name)
        {
            return columns.TryGetValue(name, out int index) && index >= 0 && index < values.Count
                ? values[index]?.Trim() ?? string.Empty
                : string.Empty;
        }

        private static bool LooksLikeSpreadsheetFormula(string? value)
        {
            string trimmed = (value ?? string.Empty).TrimStart();
            if (trimmed.Length == 0 || trimmed == "-" || trimmed == "+")
                return false;

            if (trimmed[0] is '=' or '@')
                return true;

            if (trimmed[0] is '+' or '-')
            {
                // Signed numeric values are data, not formulas.  Other leading +/- content
                // is rejected because spreadsheet applications may interpret it as a formula.
                if (decimal.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out _) ||
                    decimal.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.CurrentCulture, out _))
                    return false;
                return true;
            }

            return false;
        }

        private static bool TryParseDecimal(string? text, out decimal value)
        {
            string normalized = (text ?? string.Empty).Trim();
            return decimal.TryParse(normalized, NumberStyles.Float | NumberStyles.AllowThousands,
                       CultureInfo.InvariantCulture, out value) ||
                   decimal.TryParse(normalized, NumberStyles.Float | NumberStyles.AllowThousands,
                       CultureInfo.CurrentCulture, out value);
        }

        private static bool TryParseQualifiedDecimal(string text, out decimal value, out string? qualifier)
        {
            value = 0m;
            qualifier = null;
            string normalized = (text ?? string.Empty).Trim()
                .Replace("≤", "<=", StringComparison.Ordinal)
                .Replace("≥", ">=", StringComparison.Ordinal);
            foreach (string candidate in new[] { "<=", ">=", "<", ">" })
            {
                if (!normalized.StartsWith(candidate, StringComparison.Ordinal))
                    continue;
                qualifier = candidate;
                normalized = normalized[candidate.Length..].Trim();
                break;
            }
            return decimal.TryParse(normalized, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value) ||
                   decimal.TryParse(normalized, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value);
        }

        private static string EvaluateQualifiedUpperLimitStatus(decimal result, string? qualifier, decimal? alert, decimal? action)
        {
            if (!alert.HasValue && !action.HasValue)
                return "UNASSESSED";

            string q = qualifier ?? string.Empty;
            if (q is "<" or "<=")
            {
                // Upper-bounded results can only be classified PASS when the reported bound
                // itself is at or below the lowest applicable controlled limit.
                decimal? lowestLimit = alert ?? action;
                return lowestLimit.HasValue && result <= lowestLimit.Value ? "PASS" : "UNASSESSED";
            }

            if (q == ">")
            {
                // The true value is strictly greater than the reported lower bound.  If the
                // bound itself reaches the action level, an Action excursion is certain.
                // When the bound only reaches Alert while an Action level also exists, the
                // true value could be either Alert or Action, so do not under-classify it.
                if (action.HasValue && result >= action.Value)
                    return "FAIL";
                if (alert.HasValue && result >= alert.Value)
                    return action.HasValue ? "UNASSESSED" : "ALERT";
                return "UNASSESSED";
            }

            if (q == ">=")
            {
                // For >= the boundary itself is possible.  Under NMT logic equality with the
                // action level is not yet an Action excursion, therefore it remains unresolved
                // unless the lower bound is strictly above Action.
                if (action.HasValue && result > action.Value)
                    return "FAIL";
                if (alert.HasValue && result > alert.Value)
                    return action.HasValue ? "UNASSESSED" : "ALERT";
                return "UNASSESSED";
            }

            return action.HasValue && result > action.Value
                ? "FAIL"
                : alert.HasValue && result > alert.Value
                    ? "ALERT"
                    : "PASS";
        }

        private static bool TryParseDate(string text, out DateTimeOffset value)
        {
            string trimmed = text.Trim();
            string[] offsetFormats =
            {
                "yyyy-MM-dd HH:mm zzz",
                "yyyy-MM-dd HH:mm:ss zzz",
                "yyyy-MM-ddTHH:mmzzz",
                "yyyy-MM-ddTHH:mm:sszzz",
                "yyyy-MM-ddTHH:mm:ssK"
            };
            if (DateTimeOffset.TryParseExact(trimmed, offsetFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out value))
                return true;

            string[] localFormats =
            {
                "yyyy-MM-dd HH:mm",
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-ddTHH:mm",
                "yyyy-MM-ddTHH:mm:ss",
                "yyyy-MM-dd",
                "M/d/yyyy H:mm",
                "M/d/yyyy h:mm tt"
            };
            if (DateTime.TryParseExact(trimmed, localFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out DateTime localClock))
            {
                // External files normally contain a site-local clock without a timezone.
                // Encode it with a neutral offset so the calendar date never changes merely
                // because PharmaLIMS is opened on a workstation with another timezone.
                value = new DateTimeOffset(DateTime.SpecifyKind(localClock, DateTimeKind.Unspecified), TimeSpan.Zero);
                return true;
            }

            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double serial) &&
                serial is >= 20000 and <= 80000)
            {
                DateTime excelClock = DateTime.SpecifyKind(DateTime.FromOADate(serial), DateTimeKind.Unspecified);
                value = new DateTimeOffset(excelClock, TimeSpan.Zero);
                return true;
            }

            if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out value))
                return true;
            if (DateTime.TryParse(trimmed, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime currentClock))
            {
                value = new DateTimeOffset(DateTime.SpecifyKind(currentClock, DateTimeKind.Unspecified), TimeSpan.Zero);
                return true;
            }
            return false;
        }

        private static decimal? ParseOptionalDecimal(
            string text,
            int rowNumber,
            string field,
            ICollection<TrendImportValidationIssue> issues,
            ref bool hasError)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            if (TryParseDecimal(text, out decimal value))
                return value;

            AddRowError(issues, rowNumber, $"{field} '{text}' is invalid.");
            hasError = true;
            return null;
        }

        private static void AddRowError(ICollection<TrendImportValidationIssue> issues, int rowNumber, string message)
        {
            issues.Add(new TrendImportValidationIssue
            {
                Severity = "Error",
                RowNumber = rowNumber,
                Message = message
            });
        }

        private static List<List<string>> ReadCsv(byte[] bytes)
        {
            string content;
            using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true))
                content = reader.ReadToEnd();

            char delimiter = DetectDelimiter(content);
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;

            for (int index = 0; index < content.Length; index++)
            {
                char ch = content[index];
                if (quoted)
                {
                    if (ch == '"' && index + 1 < content.Length && content[index + 1] == '"')
                    {
                        cell.Append('"');
                        index++;
                    }
                    else if (ch == '"')
                    {
                        quoted = false;
                    }
                    else
                    {
                        cell.Append(ch);
                    }
                    continue;
                }

                if (ch == '"' && cell.Length == 0)
                {
                    quoted = true;
                }
                else if (ch == delimiter)
                {
                    if (cell.Length > MaximumCellCharacters)
                        throw new InvalidOperationException("A CSV cell exceeds the controlled text-length limit.");
                    row.Add(cell.ToString());
                    if (row.Count > MaximumColumns)
                        throw new InvalidOperationException("The CSV file contains more columns than the controlled import limit.");
                    cell.Clear();
                }
                else if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
                        index++;
                    if (cell.Length > MaximumCellCharacters)
                        throw new InvalidOperationException("A CSV cell exceeds the controlled text-length limit.");
                    row.Add(cell.ToString());
                    if (row.Count > MaximumColumns)
                        throw new InvalidOperationException("The CSV file contains more columns than the controlled import limit.");
                    cell.Clear();
                    rows.Add(row);
                    if (rows.Count > MaximumDataRows + 1)
                        throw new InvalidOperationException($"The import file contains more than {MaximumDataRows:N0} data rows.");
                    row = new List<string>();
                }
                else
                {
                    cell.Append(ch);
                }
            }

            if (quoted)
                throw new InvalidOperationException("The CSV file contains an unclosed quoted field.");
            if (cell.Length > 0 || row.Count > 0)
            {
                if (cell.Length > MaximumCellCharacters)
                    throw new InvalidOperationException("A CSV cell exceeds the controlled text-length limit.");
                row.Add(cell.ToString());
                if (row.Count > MaximumColumns)
                    throw new InvalidOperationException("The CSV file contains more columns than the controlled import limit.");
                rows.Add(row);
                if (rows.Count > MaximumDataRows + 1)
                    throw new InvalidOperationException($"The import file contains more than {MaximumDataRows:N0} data rows.");
            }

            return rows.Where(item => item.Any(value => !string.IsNullOrWhiteSpace(value))).ToList();
        }

        private static char DetectDelimiter(string content)
        {
            string header = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
            var options = new[] { ',', ';', '\t' };
            return options.OrderByDescending(delimiter => header.Count(ch => ch == delimiter)).First();
        }

        private static List<List<string>> ReadXlsx(byte[] bytes, out int cachedFormulaCellCount)
        {
            cachedFormulaCellCount = 0;
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read, false);
            if (archive.GetEntry("xl/vbaProject.bin") != null)
                throw new InvalidOperationException("Macro-enabled workbooks are not allowed.");

            long totalExpandedBytes = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                totalExpandedBytes = checked(totalExpandedBytes + entry.Length);
                if (totalExpandedBytes > MaximumArchiveExpandedBytes)
                    throw new InvalidOperationException("The XLSX workbook expands beyond the controlled processing limit.");

                if (entry.Length > 1024L * 1024L && entry.CompressedLength > 0 &&
                    entry.Length / Math.Max(1L, entry.CompressedLength) > MaximumCompressionRatio)
                {
                    throw new InvalidOperationException("The XLSX workbook contains an unsafe compression ratio.");
                }
            }

            List<ZipArchiveEntry> sheets = archive.Entries
                .Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
                                entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (sheets.Count == 0)
                throw new InvalidOperationException("The XLSX workbook does not contain a readable worksheet.");
            if (sheets.Count > MaximumWorksheets)
                throw new InvalidOperationException("The XLSX workbook contains more worksheets than the controlled import limit.");

            List<string> sharedStrings = ReadSharedStrings(archive);
            XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            foreach (ZipArchiveEntry sheet in sheets)
            {
                XDocument document = LoadXmlEntry(sheet, MaximumWorksheetXmlBytes, "worksheet");
                int sheetFormulaCellCount = 0;
                var result = new List<List<string>>();
                foreach (XElement rowElement in document.Descendants(main + "row"))
                {
                    var cells = new SortedDictionary<int, string>();
                    foreach (XElement cell in rowElement.Elements(main + "c"))
                    {
                        int column = ColumnIndex((string?)cell.Attribute("r"));
                        if (column >= MaximumColumns)
                            throw new InvalidOperationException("The XLSX worksheet contains a column beyond the controlled import limit.");

                        string type = (string?)cell.Attribute("t") ?? string.Empty;
                        if (cell.Element(main + "f") != null)
                        {
                            // XLSX stores the last calculated/displayed result in <v>.
                            // Import that immutable cached value while retaining the exact
                            // original workbook and hash. A formula without a cached result
                            // cannot be interpreted reproducibly and remains blocked.
                            if (cell.Element(main + "v") == null)
                                throw new InvalidOperationException("A formula cell in the import worksheet has no cached displayed value. Recalculate and save the workbook in Excel, then import it again.");
                            sheetFormulaCellCount++;
                        }
                        string value;
                        if (type == "inlineStr")
                        {
                            value = string.Concat(cell.Descendants(main + "t").Select(item => item.Value));
                        }
                        else
                        {
                            value = cell.Element(main + "v")?.Value ?? string.Empty;
                            if (type == "s" && int.TryParse(value, out int sharedIndex) &&
                                sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
                                value = sharedStrings[sharedIndex];
                        }

                        if (value.Length > MaximumCellCharacters)
                            throw new InvalidOperationException("An XLSX cell exceeds the controlled text-length limit.");
                        cells[column] = value;
                    }

                    if (cells.Count == 0)
                    {
                        result.Add(new List<string>());
                    }
                    else
                    {
                        int max = cells.Keys.Max();
                        var row = Enumerable.Repeat(string.Empty, max + 1).ToList();
                        foreach ((int index, string value) in cells)
                            row[index] = value;
                        result.Add(row);
                    }

                    if (result.Count > MaximumDataRows + 1)
                        throw new InvalidOperationException($"The import file contains more than {MaximumDataRows:N0} data rows.");
                }

                result = result.Where(item => item.Any(value => !string.IsNullOrWhiteSpace(value))).ToList();
                int headerIndex = result
                    .Take(Math.Min(result.Count, 50))
                    .Select((row, index) => new { Index = index, Columns = ResolveColumns(row) })
                    .Where(candidate => RequiredColumns.All(candidate.Columns.ContainsKey))
                    .Select(candidate => candidate.Index)
                    .DefaultIfEmpty(-1)
                    .First();
                if (headerIndex >= 0)
                {
                    cachedFormulaCellCount = sheetFormulaCellCount;
                    return result.Skip(headerIndex).ToList();
                }
            }

            throw new InvalidOperationException("No worksheet contains the required controlled import columns within its first 50 populated rows. Use the Import Data worksheet or download the controlled template.");
        }

        private static List<string> ReadSharedStrings(ZipArchive archive)
        {
            ZipArchiveEntry? entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
                return new List<string>();

            XDocument document = LoadXmlEntry(entry, MaximumSharedStringsXmlBytes, "shared strings");
            XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            List<string> values = document.Descendants(main + "si")
                .Select(item => string.Concat(item.Descendants(main + "t").Select(text => text.Value)))
                .ToList();

            if (values.Count > MaximumSharedStrings)
                throw new InvalidOperationException("The XLSX shared-string table exceeds the controlled processing limit.");
            if (values.Any(value => value.Length > MaximumCellCharacters))
                throw new InvalidOperationException("An XLSX shared string exceeds the controlled text-length limit.");

            return values;
        }

        private static XDocument LoadXmlEntry(ZipArchiveEntry entry, long maximumBytes, string label)
        {
            if (entry.Length > maximumBytes)
                throw new InvalidOperationException($"The XLSX {label} XML expands beyond the controlled processing limit.");

            using Stream stream = entry.Open();
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = maximumBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };
            using XmlReader reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.None);
        }

        private static int ColumnIndex(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                return 0;
            int value = 0;
            foreach (char ch in reference)
            {
                if (!char.IsLetter(ch))
                    break;
                value = checked(value * 26 + (char.ToUpperInvariant(ch) - 'A' + 1));
            }
            return Math.Max(0, value - 1);
        }

        private static DateTimeOffset GetAuthoritativeDatabaseTimestamp()
        {
            try
            {
                object value = DatabaseHelper.ExecuteScalar("SELECT SYSDATETIMEOFFSET();");
                if (value is DateTimeOffset timestamp)
                    return timestamp;
                if (DateTimeOffset.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out timestamp))
                {
                    return timestamp;
                }

                throw new InvalidOperationException("SQL Server did not return an authoritative database timestamp.");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "Authoritative database time is unavailable. The controlled external-trend action was not recorded.",
                    ex);
            }
        }

        private static void ValidateModule(string moduleName)
        {
            if (!AllowedModules.Contains(moduleName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("External trend import currently supports Water and Environmental Monitoring only.");
        }

        private static bool RoleIs(string role, string expected) =>
            string.Equals(role, expected, StringComparison.OrdinalIgnoreCase);

        private static void AddNullableText(SqlCommand command, string name, int size, string? value)
        {
            command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = DbText(value);
        }

        private static void AddNullableDecimal(SqlCommand command, string name, decimal? value)
        {
            SqlParameter parameter = command.Parameters.Add(name, SqlDbType.Decimal);
            parameter.Precision = 38;
            parameter.Scale = 10;
            parameter.Value = value.HasValue ? value.Value : DBNull.Value;
        }

        private static object DbText(string? value) =>
            string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

        private static string? NullIfWhiteSpace(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public sealed class TrendImportPreview
    {
        public string SourcePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FileExtension { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public string FileHashSha256 { get; set; } = string.Empty;
        public byte[] OriginalFile { get; set; } = Array.Empty<byte>();
        public string ModuleName { get; set; } = string.Empty;
        public List<TrendImportRow> Rows { get; } = new();
        public List<TrendImportValidationIssue> Issues { get; } = new();
        public bool HasErrors => Issues.Any(issue => issue.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
    }

    public sealed class TrendImportStageResult
    {
        public int BatchId { get; init; }
        public string ImportNumber { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public bool AlreadyExists { get; init; }
    }

    public sealed class TrendImportRow
    {
        public int SourceRowNumber { get; set; }
        public string? SourceRecordID { get; set; }
        public DateTimeOffset RecordDateTime { get; set; }
        public string EntityCode { get; set; } = string.Empty;
        public string? EntityName { get; set; }
        public string AreaClassification { get; set; } = string.Empty;
        public string? LocationName { get; set; }
        public string? MethodName { get; set; }
        public string ParameterName { get; set; } = string.Empty;
        public decimal ResultValue { get; set; }
        public string? ResultQualifier { get; set; }
        public string UnitName { get; set; } = string.Empty;
        public decimal? AlertLimit { get; set; }
        public decimal? ActionLimit { get; set; }
        public string ResultStatus { get; set; } = "UNASSESSED";
        public string? Remarks { get; set; }
    }

    public sealed class TrendImportValidationIssue
    {
        public string Severity { get; set; } = "Error";
        public int RowNumber { get; set; }
        public string Message { get; set; } = string.Empty;
        public string DisplayText => RowNumber > 0
            ? $"{Severity} - Row {RowNumber}: {Message}"
            : $"{Severity}: {Message}";
    }
}
