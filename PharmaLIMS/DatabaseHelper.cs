using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;

#nullable disable

namespace PharmaLIMS
{
    // Core data access, numbering, master-data lookup, registration, and sample-result primitives.
    public static partial class DatabaseHelper
    {
        private static readonly string connectionString = AppConfig.ConnectionString;
        private static readonly DatabaseConnection databaseConnection = new DatabaseConnection();


        private static readonly HashSet<string> AllowedPermissionColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CanAccessWater",
            "CanAccessEM",
            "CanRegisterSamples",
            "CanEnterResults",
            "CanReviewResults",
            "CanApproveResults",
            "CanIssueCOA",
            "CanCancelCOA",
            "CanAccessReports",
            "CanManageUsers",
            "CanManageSettings"
        };

        public static DataTable ExecuteQuery(string query, SqlParameter[] parameters = null, int? commandTimeoutSeconds = null)
        {
            return databaseConnection.ExecuteQuery(query, parameters, commandTimeoutSeconds);
        }

        public static int ExecuteNonQuery(string query, SqlParameter[] parameters = null, int? commandTimeoutSeconds = null)
        {
            return databaseConnection.ExecuteNonQuery(query, parameters, commandTimeoutSeconds);
        }

        public static object ExecuteScalar(string query, SqlParameter[] parameters = null, int? commandTimeoutSeconds = null)
        {
            return databaseConnection.ExecuteScalar(query, parameters, commandTimeoutSeconds);
        }

        public static void ExecuteInTransaction(Action<SqlConnection, SqlTransaction> action)
        {
            databaseConnection.ExecuteInTransaction(action);
        }

        public static DateTime GetAuthoritativeDatabaseTime()
        {
            object value = ExecuteScalar("SELECT SYSDATETIME();");
            if (value == null || value == DBNull.Value)
                throw new InvalidOperationException("SQL Server did not return an authoritative database timestamp.");
            return Convert.ToDateTime(value, CultureInfo.InvariantCulture);
        }

        private static DateTime GetAuthoritativeDatabaseTime(SqlConnection connection, SqlTransaction transaction)
        {
            using SqlCommand command = new SqlCommand("SELECT SYSDATETIME();", connection, transaction)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };
            object value = command.ExecuteScalar();
            if (value == null || value == DBNull.Value)
                throw new InvalidOperationException("SQL Server did not return an authoritative database timestamp.");
            return Convert.ToDateTime(value, CultureInfo.InvariantCulture);
        }

        public static int ExecuteNonQueryWithTransaction(
            string query,
            SqlParameter[] parameters,
            SqlConnection conn,
            SqlTransaction transaction)
        {
            using (SqlCommand cmd = new SqlCommand(query, conn, transaction))
            {
                cmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;

                if (parameters != null)
                    cmd.Parameters.AddRange(parameters);

                return cmd.ExecuteNonQuery();
            }
        }

        private static bool TableExists(string tableName)
        {
            string query = @"
                SELECT COUNT(*)
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = 'dbo'
                  AND TABLE_NAME = @tableName";

            SqlParameter[] pars =
            {
                new SqlParameter("@tableName", SqlDbType.NVarChar, 128) { Value = tableName }
            };

            object result = ExecuteScalar(query, pars);
            return result != null && result != DBNull.Value && Convert.ToInt32(result) > 0;
        }

        private static bool ColumnExists(string tableName, string columnName)
        {
            string query = @"
                SELECT COUNT(*)
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = @tableName
                  AND COLUMN_NAME = @columnName";

            SqlParameter[] pars =
            {
                new SqlParameter("@tableName", SqlDbType.NVarChar, 128) { Value = tableName },
                new SqlParameter("@columnName", columnName)
            };

            object result = ExecuteScalar(query, pars);
            return result != null && result != DBNull.Value && Convert.ToInt32(result) > 0;
        }

        private static int GetNextSequenceValue(
            SqlConnection conn,
            SqlTransaction tx,
            string numberKey,
            int yearNo,
            string prefix,
            string sequenceKey)
        {
            using (SqlCommand cmd = new SqlCommand(@"
                DECLARE @NextNumber INT;

                IF EXISTS
                (
                    SELECT 1
                    FROM dbo.LIMS_NumberSequences WITH (UPDLOCK, HOLDLOCK)
                    WHERE SequenceKey = @sequenceKey
                       OR (NumberKey = @numberKey AND YearNo = @yearNo)
                )
                BEGIN
                    UPDATE dbo.LIMS_NumberSequences
                    SET LastNumber = LastNumber + 1,
                        UpdatedAt = SYSDATETIME()
                    WHERE SequenceKey = @sequenceKey
                       OR (NumberKey = @numberKey AND YearNo = @yearNo);

                    SELECT @NextNumber = LastNumber
                    FROM dbo.LIMS_NumberSequences
                    WHERE SequenceKey = @sequenceKey
                       OR (NumberKey = @numberKey AND YearNo = @yearNo);
                END
                ELSE
                BEGIN
                    INSERT INTO dbo.LIMS_NumberSequences
                    (
                        NumberKey,
                        YearNo,
                        LastNumber,
                        Prefix,
                        CreatedAt,
                        UpdatedAt,
                        SequenceKey
                    )
                    VALUES
                    (
                        @numberKey,
                        @yearNo,
                        1,
                        @prefix,
                        SYSDATETIME(),
                        SYSDATETIME(),
                        @sequenceKey
                    );

                    SET @NextNumber = 1;
                END

                SELECT @NextNumber;", conn, tx))
            {
                cmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                cmd.Parameters.Add("@numberKey", SqlDbType.NVarChar, 50).Value = numberKey;
                cmd.Parameters.Add("@yearNo", SqlDbType.Int).Value = yearNo;
                cmd.Parameters.Add("@prefix", SqlDbType.NVarChar, 50).Value = prefix;
                cmd.Parameters.Add("@sequenceKey", SqlDbType.NVarChar, 100).Value = sequenceKey;

                object result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 1;
            }
        }

        private static int GetNextSequenceValue(string numberKey, int yearNo, string prefix, string sequenceKey)
        {
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                using (SqlTransaction tx = conn.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    try
                    {
                        int next = GetNextSequenceValue(conn, tx, numberKey, yearNo, prefix, sequenceKey);
                        tx.Commit();
                        return next;
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }

        public static string GetNextWaterSampleNumber(string waterPrefix)
        {
            string cleanPrefix = string.Equals(waterPrefix, "PTW", StringComparison.OrdinalIgnoreCase) ? "PTW" : "PW";

            using SqlConnection conn = new SqlConnection(connectionString);
            conn.Open();
            using SqlTransaction tx = conn.BeginTransaction(IsolationLevel.Serializable);
            try
            {
                DateTime databaseNow = GetAuthoritativeDatabaseTime(conn, tx);
                int yearNo = databaseNow.Year;
                string yearText = yearNo.ToString(CultureInfo.InvariantCulture);
                string sequenceKey = cleanPrefix + "-" + yearText;
                string samplePrefix = sequenceKey + "-";

                using SqlCommand command = new SqlCommand(@"
DECLARE @ExistingMax INT = 0;
DECLARE @CurrentLast INT = NULL;

SELECT @ExistingMax = ISNULL(MAX(TRY_CONVERT(INT, SUBSTRING(SampleNumber, LEN(@samplePrefix) + 1, 20))), 0)
FROM dbo.Samples WITH (UPDLOCK, HOLDLOCK)
WHERE SampleNumber LIKE @samplePrefix + N'%'
  AND TRY_CONVERT(INT, SUBSTRING(SampleNumber, LEN(@samplePrefix) + 1, 20)) IS NOT NULL;

SELECT @CurrentLast = LastNumber
FROM dbo.LIMS_NumberSequences WITH (UPDLOCK, HOLDLOCK)
WHERE SequenceKey = @sequenceKey
   OR (NumberKey = @numberKey AND YearNo = @yearNo);

IF @CurrentLast IS NULL
BEGIN
    INSERT INTO dbo.LIMS_NumberSequences
    (
        NumberKey, YearNo, LastNumber, Prefix, CreatedAt, UpdatedAt, SequenceKey
    )
    VALUES
    (
        @numberKey, @yearNo, @ExistingMax, @prefix, SYSDATETIME(), SYSDATETIME(), @sequenceKey
    );
END
ELSE IF @CurrentLast < @ExistingMax
BEGIN
    UPDATE dbo.LIMS_NumberSequences
    SET LastNumber = @ExistingMax,
        Prefix = @prefix,
        SequenceKey = @sequenceKey,
        UpdatedAt = SYSDATETIME()
    WHERE SequenceKey = @sequenceKey
       OR (NumberKey = @numberKey AND YearNo = @yearNo);
END;

UPDATE dbo.LIMS_NumberSequences
SET LastNumber = LastNumber + 1,
    UpdatedAt = SYSDATETIME()
WHERE SequenceKey = @sequenceKey
   OR (NumberKey = @numberKey AND YearNo = @yearNo);

SELECT LastNumber
FROM dbo.LIMS_NumberSequences
WHERE SequenceKey = @sequenceKey
   OR (NumberKey = @numberKey AND YearNo = @yearNo);", conn, tx);
                command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                command.Parameters.Add("@numberKey", SqlDbType.NVarChar, 50).Value = cleanPrefix;
                command.Parameters.Add("@yearNo", SqlDbType.Int).Value = yearNo;
                command.Parameters.Add("@prefix", SqlDbType.NVarChar, 50).Value = cleanPrefix;
                command.Parameters.Add("@sequenceKey", SqlDbType.NVarChar, 100).Value = sequenceKey;
                command.Parameters.Add("@samplePrefix", SqlDbType.NVarChar, 100).Value = samplePrefix;

                object? value = command.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    throw new InvalidOperationException("The water sample number sequence did not return a value.");

                int next = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                tx.Commit();
                return samplePrefix + next.ToString("0000", CultureInfo.InvariantCulture);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        public static string GetNextSampleNumber()
        {
            return GetNextWaterSampleNumber("PW");
        }

        public static string GetNextEMEventNumber()
        {
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (SqlTransaction tx = conn.BeginTransaction(IsolationLevel.Serializable))
                {
                    try
                    {
                        string eventNumber = GetNextEMEventNumber(conn, tx);
                        tx.Commit();
                        return eventNumber;
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }

        public static string GetNextEMEventNumber(SqlConnection conn, SqlTransaction tx)
        {
            if (conn == null)
                throw new ArgumentNullException(nameof(conn));
            if (tx == null)
                throw new ArgumentNullException(nameof(tx));

            DateTime databaseNow = GetAuthoritativeDatabaseTime(conn, tx);
            string dateKey = databaseNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            int yearNo = databaseNow.Year;
            const string sequenceKey = "EM-GLOBAL";

            using (SqlCommand cmd = new SqlCommand(@"
                            DECLARE @StoredLast INT = 0;
                            DECLARE @ExistingLast INT = 0;
                            DECLARE @NextNumber INT;
                            DECLARE @LockResult INT;

                            EXEC @LockResult = sys.sp_getapplock
                                @Resource = N'PharmaLIMS-EM-Event-Number',
                                @LockMode = N'Exclusive',
                                @LockOwner = N'Transaction',
                                @LockTimeout = 15000;

                            IF @LockResult < 0
                                THROW 51021, 'Unable to acquire the EM event numbering lock.', 1;

                            SELECT @StoredLast = ISNULL(MAX(LastNumber), 0)
                            FROM dbo.LIMS_NumberSequences WITH (UPDLOCK, HOLDLOCK)
                            WHERE SequenceKey = @SequenceKey
                               OR (NumberKey = N'EM' AND YearNo = @YearNo);

                            IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NOT NULL
                            BEGIN
                                SELECT @ExistingLast = ISNULL(MAX(TRY_CONVERT(INT,
                                    CASE
                                        WHEN CHARINDEX(N'-', REVERSE(EventNo)) > 0
                                        THEN RIGHT(EventNo, CHARINDEX(N'-', REVERSE(EventNo)) - 1)
                                        ELSE NULL
                                    END)), 0)
                                FROM dbo.EM_Events WITH (UPDLOCK, HOLDLOCK)
                                WHERE EventNo LIKE N'EM-%';
                            END;

                            SET @NextNumber =
                                CASE WHEN @StoredLast > @ExistingLast THEN @StoredLast ELSE @ExistingLast END + 1;

                            UPDATE dbo.LIMS_NumberSequences WITH (UPDLOCK, HOLDLOCK)
                            SET LastNumber = @NextNumber,
                                UpdatedAt = SYSDATETIME()
                            WHERE NumberKey = N'EM' AND YearNo = @YearNo;

                            IF @@ROWCOUNT = 0
                            BEGIN
                                UPDATE dbo.LIMS_NumberSequences WITH (UPDLOCK, HOLDLOCK)
                                SET LastNumber = @NextNumber,
                                    UpdatedAt = SYSDATETIME()
                                WHERE SequenceKey = @SequenceKey;

                                IF @@ROWCOUNT = 0
                                BEGIN
                                    BEGIN TRY
                                        INSERT INTO dbo.LIMS_NumberSequences
                                            (NumberKey, YearNo, LastNumber, Prefix, CreatedAt, UpdatedAt, SequenceKey)
                                        VALUES
                                            (N'EM', @YearNo, @NextNumber, N'EM', GETDATE(), GETDATE(), @SequenceKey);
                                    END TRY
                                    BEGIN CATCH
                                        IF ERROR_NUMBER() IN (2601, 2627)
                                        BEGIN
                                            UPDATE dbo.LIMS_NumberSequences WITH (UPDLOCK, HOLDLOCK)
                                            SET LastNumber = CASE WHEN LastNumber >= @NextNumber THEN LastNumber + 1 ELSE @NextNumber END,
                                                @NextNumber = CASE WHEN LastNumber >= @NextNumber THEN LastNumber + 1 ELSE @NextNumber END,
                                                UpdatedAt = SYSDATETIME()
                                            WHERE NumberKey = N'EM' AND YearNo = @YearNo;

                                            IF @@ROWCOUNT = 0 THROW;
                                        END
                                        ELSE
                                            THROW;
                                    END CATCH;
                                END;
                            END;

                            SELECT @NextNumber;", conn, tx))
            {
                cmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                cmd.Parameters.Add("@SequenceKey", SqlDbType.NVarChar, 100).Value = sequenceKey;
                cmd.Parameters.Add("@YearNo", SqlDbType.Int).Value = yearNo;
                object result = cmd.ExecuteScalar();
                int next = result == null || result == DBNull.Value ? 1 : Convert.ToInt32(result);
                return "EM-" + dateKey + "-" + next.ToString("0000", CultureInfo.InvariantCulture);
            }
        }

        public static string GetNextCertificateNumber()
        {
            const string sequenceKey = "COA-GLOBAL";

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                using (SqlTransaction tx = conn.BeginTransaction(IsolationLevel.Serializable))
                {
                    try
                    {
                        DateTime databaseNow = GetAuthoritativeDatabaseTime(conn, tx);
                        string dateKey = databaseNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                        int yearNo = databaseNow.Year;
                        string certificatePrefix = "COA-" + dateKey + "-";
                        using (SqlCommand cmd = new SqlCommand(@"
                            DECLARE @StoredLast INT = 0;
                            DECLARE @ExistingLast INT = 0;
                            DECLARE @NextNumber INT;

                            SELECT @StoredLast = ISNULL(MAX(LastNumber), 0)
                            FROM dbo.LIMS_NumberSequences WITH (UPDLOCK, HOLDLOCK)
                            WHERE SequenceKey = @SequenceKey
                               OR (NumberKey = N'COA' AND YearNo = @YearNo);

                            IF OBJECT_ID(N'dbo.Certificates', N'U') IS NOT NULL
                            BEGIN
                                SELECT @ExistingLast = ISNULL(MAX(TRY_CONVERT(INT,
                                    CASE
                                        WHEN CHARINDEX(N'-', REVERSE(CertificateNumber)) > 0
                                        THEN RIGHT(CertificateNumber, CHARINDEX(N'-', REVERSE(CertificateNumber)) - 1)
                                        ELSE NULL
                                    END)), 0)
                                FROM dbo.Certificates WITH (UPDLOCK, HOLDLOCK);
                            END;

                            SET @NextNumber =
                                CASE WHEN @StoredLast > @ExistingLast THEN @StoredLast ELSE @ExistingLast END + 1;

                            IF EXISTS
                            (
                                SELECT 1
                                FROM dbo.LIMS_NumberSequences WITH (UPDLOCK, HOLDLOCK)
                                WHERE SequenceKey = @SequenceKey
                                   OR (NumberKey = N'COA' AND YearNo = @YearNo)
                            )
                            BEGIN
                                UPDATE dbo.LIMS_NumberSequences
                                SET LastNumber = @NextNumber,
                                    UpdatedAt = SYSDATETIME()
                                WHERE SequenceKey = @SequenceKey
                                   OR (NumberKey = N'COA' AND YearNo = @YearNo);
                            END
                            ELSE
                            BEGIN
                                INSERT INTO dbo.LIMS_NumberSequences
                                    (NumberKey, YearNo, LastNumber, Prefix, CreatedAt, UpdatedAt, SequenceKey)
                                VALUES
                                    (N'COA', @YearNo, @NextNumber, N'COA', GETDATE(), GETDATE(), @SequenceKey);
                            END;

                            SELECT @NextNumber;", conn, tx))
                        {
                            cmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                            cmd.Parameters.Add("@SequenceKey", SqlDbType.NVarChar, 100).Value = sequenceKey;
                            cmd.Parameters.Add("@YearNo", SqlDbType.Int).Value = yearNo;

                            object result = cmd.ExecuteScalar();
                            int next = result == null || result == DBNull.Value ? 1 : Convert.ToInt32(result);
                            tx.Commit();
                            return certificatePrefix + next.ToString("0000");
                        }
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }

        private static string GetNextQualityEventNumber(SqlConnection conn, SqlTransaction tx)
        {
            DateTime databaseNow = GetAuthoritativeDatabaseTime(conn, tx);
            string dateKey = databaseNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            int periodKey = int.Parse(dateKey, CultureInfo.InvariantCulture);
            string sequenceKey = "QE-" + dateKey;
            int next = GetNextSequenceValue(conn, tx, "QE", periodKey, "QE", sequenceKey);
            return "QE-" + dateKey + "-" + next.ToString("0000");
        }

        public static int EnsureOpenSourceQualityEvent(
            SqlConnection connection,
            SqlTransaction transaction,
            string sourceModule,
            int sourceRecordId,
            string sourceReference,
            string eventType,
            string severity,
            string detectedBy,
            string description,
            string immediateAction)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (sourceRecordId <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRecordId));
            if (string.IsNullOrWhiteSpace(sourceModule)) throw new ArgumentException("Source module is required.", nameof(sourceModule));
            if (string.IsNullOrWhiteSpace(eventType)) throw new ArgumentException("Event type is required.", nameof(eventType));

            using (SqlCommand schema = new SqlCommand(@"
IF OBJECT_ID(N'dbo.QualityEvents',N'U') IS NULL OR OBJECT_ID(N'dbo.QualityEventActions',N'U') IS NULL
    THROW 51490, 'Quality Event schema is missing. The controlled exception could not be recorded.', 1;
IF COL_LENGTH(N'dbo.QualityEvents',N'SourceModule') IS NULL OR COL_LENGTH(N'dbo.QualityEvents',N'SourceRecordID') IS NULL
    THROW 51491, 'Quality Event source-link columns are missing. The controlled exception could not be recorded.', 1;", connection, transaction))
            {
                schema.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                schema.ExecuteNonQuery();
            }

            int qualityEventId = 0;
            using (SqlCommand existing = new SqlCommand(@"
SELECT TOP(1) QualityEventID
FROM dbo.QualityEvents WITH(UPDLOCK,HOLDLOCK)
WHERE UPPER(LTRIM(RTRIM(ISNULL(SourceModule,N''))))=UPPER(LTRIM(RTRIM(@SourceModule)))
  AND SourceRecordID=@SourceRecordID
  AND UPPER(LTRIM(RTRIM(ISNULL(EventType,N''))))=UPPER(LTRIM(RTRIM(@EventType)))
  AND UPPER(LTRIM(RTRIM(ISNULL(CurrentStatus,N'Open')))) NOT IN(N'CLOSED',N'QA CLOSED',N'CANCELLED',N'REJECTED CLOSED')
ORDER BY QualityEventID DESC;", connection, transaction))
            {
                existing.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                existing.Parameters.Add("@SourceModule", SqlDbType.NVarChar, 80).Value = sourceModule.Trim();
                existing.Parameters.Add("@SourceRecordID", SqlDbType.Int).Value = sourceRecordId;
                existing.Parameters.Add("@EventType", SqlDbType.NVarChar, 120).Value = eventType.Trim();
                object? existingValue = existing.ExecuteScalar();
                if (existingValue != null && existingValue != DBNull.Value)
                    qualityEventId = Convert.ToInt32(existingValue, CultureInfo.InvariantCulture);
            }

            bool created = false;
            string eventNumber = string.Empty;
            if (qualityEventId == 0)
            {
                eventNumber = GetNextQualityEventNumber(connection, transaction);
                using SqlCommand create = new SqlCommand(@"
INSERT dbo.QualityEvents
(EventNumber,EventType,Severity,SampleNumber,SourceModule,SourceRecordID,CurrentStatus,DetectedBy,DetectedDate,DetectionSource,InitialDescription,ImmediateAction,CAPARequired,CreatedBy,CreatedDate,ModifiedBy,ModifiedDate)
OUTPUT INSERTED.QualityEventID
VALUES(@EventNumber,@EventType,@Severity,@SourceReference,@SourceModule,@SourceRecordID,N'Open',@DetectedBy,SYSDATETIME(),@SourceModule,@Description,@ImmediateAction,1,@DetectedBy,SYSDATETIME(),@DetectedBy,SYSDATETIME());", connection, transaction);
                create.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                create.Parameters.Add("@EventNumber", SqlDbType.NVarChar, 60).Value = eventNumber;
                create.Parameters.Add("@EventType", SqlDbType.NVarChar, 120).Value = eventType.Trim();
                create.Parameters.Add("@Severity", SqlDbType.NVarChar, 60).Value = string.IsNullOrWhiteSpace(severity) ? "Major" : severity.Trim();
                create.Parameters.Add("@SourceReference", SqlDbType.NVarChar, 80).Value = string.IsNullOrWhiteSpace(sourceReference) ? (object)DBNull.Value : sourceReference.Trim();
                create.Parameters.Add("@SourceModule", SqlDbType.NVarChar, 80).Value = sourceModule.Trim();
                create.Parameters.Add("@SourceRecordID", SqlDbType.Int).Value = sourceRecordId;
                create.Parameters.Add("@DetectedBy", SqlDbType.NVarChar, 120).Value = string.IsNullOrWhiteSpace(detectedBy) ? (object)DBNull.Value : detectedBy.Trim();
                create.Parameters.Add("@Description", SqlDbType.NVarChar, -1).Value = string.IsNullOrWhiteSpace(description) ? "Controlled exception recorded by PharmaLIMS." : description.Trim();
                create.Parameters.Add("@ImmediateAction", SqlDbType.NVarChar, -1).Value = string.IsNullOrWhiteSpace(immediateAction) ? "Record placed under investigation pending QA disposition." : immediateAction.Trim();
                qualityEventId = Convert.ToInt32(create.ExecuteScalar(), CultureInfo.InvariantCulture);
                created = true;
            }
            else
            {
                using SqlCommand touch = new SqlCommand(@"
UPDATE dbo.QualityEvents
SET ModifiedBy=@User,ModifiedDate=SYSDATETIME(),
    InitialDescription=CASE WHEN NULLIF(LTRIM(RTRIM(ISNULL(InitialDescription,N''))),N'') IS NULL THEN @Description ELSE InitialDescription END
WHERE QualityEventID=@QualityEventID;", connection, transaction);
                touch.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                touch.Parameters.Add("@User", SqlDbType.NVarChar, 120).Value = string.IsNullOrWhiteSpace(detectedBy) ? (object)DBNull.Value : detectedBy.Trim();
                touch.Parameters.Add("@Description", SqlDbType.NVarChar, -1).Value = string.IsNullOrWhiteSpace(description) ? "Controlled exception recorded by PharmaLIMS." : description.Trim();
                touch.Parameters.Add("@QualityEventID", SqlDbType.Int).Value = qualityEventId;
                touch.ExecuteNonQuery();
            }

            using (SqlCommand action = new SqlCommand(@"
INSERT dbo.QualityEventActions(QualityEventID,ActionType,ActionDescription,PerformedBy,PerformedDate)
VALUES(@QualityEventID,@ActionType,@Description,@User,SYSDATETIME());", connection, transaction))
            {
                action.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                action.Parameters.Add("@QualityEventID", SqlDbType.Int).Value = qualityEventId;
                action.Parameters.Add("@ActionType", SqlDbType.NVarChar, 100).Value = created ? "System Opened" : "Additional Excursion Recorded";
                action.Parameters.Add("@Description", SqlDbType.NVarChar, -1).Value = string.IsNullOrWhiteSpace(description) ? "Controlled exception recorded by PharmaLIMS." : description.Trim();
                action.Parameters.Add("@User", SqlDbType.NVarChar, 120).Value = string.IsNullOrWhiteSpace(detectedBy) ? (object)DBNull.Value : detectedBy.Trim();
                action.ExecuteNonQuery();
            }

            AddAuditTrailAdvanced(connection, transaction, "QualityEvents", qualityEventId,
                created ? "Open Quality Event" : "Update Quality Event",
                created ? string.Empty : "Open", "Open",
                string.IsNullOrWhiteSpace(description) ? "Controlled exception recorded by PharmaLIMS." : description.Trim(),
                detectedBy, "CurrentStatus", null, sourceReference, sourceModule);

            return qualityEventId;
        }

        public static bool HasOpenSourceQualityEvent(string sourceModule, int sourceRecordId)
        {
            object? result = ExecuteScalar(@"
SELECT CASE WHEN EXISTS
(
    SELECT 1 FROM dbo.QualityEvents
    WHERE UPPER(LTRIM(RTRIM(ISNULL(SourceModule,N''))))=UPPER(LTRIM(RTRIM(@SourceModule)))
      AND SourceRecordID=@SourceRecordID
      AND UPPER(LTRIM(RTRIM(ISNULL(CurrentStatus,N'Open')))) NOT IN(N'CLOSED',N'QA CLOSED',N'CANCELLED',N'REJECTED CLOSED')
) THEN 1 ELSE 0 END;", new[]
            {
                new SqlParameter("@SourceModule", sourceModule ?? string.Empty),
                new SqlParameter("@SourceRecordID", sourceRecordId)
            });
            return Convert.ToInt32(result ?? 0, CultureInfo.InvariantCulture) == 1;
        }

        public static DataTable GetAllTests()
        {
            string query = @"
                SELECT
                    TestID,
                    TestName,
                    TestCategory,
                    Unit,
                    AlertLimit,
                    ActionLimit
                FROM Tests
                ORDER BY TestCategory, TestName";

            return ExecuteQuery(query);
        }

        public static DataTable GetWaterSamplingPointsByType(string waterType)
        {
            string normalizedType = waterType ?? "";

            if (normalizedType.Equals("Purified Water", StringComparison.OrdinalIgnoreCase) ||
                normalizedType.Equals("Purified Water (PWS)", StringComparison.OrdinalIgnoreCase))
                normalizedType = "Purified";

            if (normalizedType.Equals("Potable Water", StringComparison.OrdinalIgnoreCase) ||
                normalizedType.Equals("Potable Water (PTWS)", StringComparison.OrdinalIgnoreCase))
                normalizedType = "Potable";

            string query = @"
                SELECT
                    Id AS PointID,
                    PointCode,
                    PointName,
                    Location,
                    WaterType,
                    Status,
                    PointCode + ' - ' + PointName + ' - ' + Location AS DisplayName
                FROM WaterSamplingPoints
                WHERE Status = 'Active'
                  AND WaterType = @waterType
                ORDER BY PointCode";

            SqlParameter[] pars =
            {
                new SqlParameter("@waterType", normalizedType)
            };

            return ExecuteQuery(query, pars);
        }

        private static string NormalizeWaterProfileCode(string waterType)
        {
            string value = waterType == null ? "" : waterType.Trim().ToLowerInvariant();

            if (value == "pw" ||
                value == "purified" ||
                value == "purified water" ||
                value.Contains("pws") ||
                value.Contains("purified"))
                return "PW";

            if (value == "ptw" ||
                value == "potable" ||
                value == "potable water" ||
                value.Contains("ptws") ||
                value.Contains("drinking") ||
                value.Contains("potable"))
                return "PTW";

            return value.ToUpperInvariant();
        }

        public static DataTable GetActiveWaterTestIdsForProfile(string waterType)
        {
            string profileCode = NormalizeWaterProfileCode(waterType);

            if (profileCode != "PW" && profileCode != "PTW")
                throw new InvalidOperationException("Unsupported water test profile: " + (waterType ?? string.Empty) + ".");

            if (!TableExists("WaterTestProfiles") ||
                !TableExists("WaterTestProfileTests") ||
                !TableExists("WaterSpecifications") ||
                !TableExists("WaterTestProfileSignatures") ||
                !ColumnExists("WaterTestProfiles", "ControlledReference") ||
                !ColumnExists("WaterSpecifications", "ProfileID"))
            {
                throw new InvalidOperationException(
                    "Controlled Water Test Profile workflow is not installed. " +
                    "Sample registration is blocked to prevent assigning guessed or hard-coded tests. " +
                    "Run System Preflight and Development Database Maintenance before registering water samples.");
            }

            string query = @"
                SELECT
                    t.TestID,
                    t.TestName,
                    t.TestCategory,
                    ISNULL(wpt.SortOrder, t.SortOrder) AS SortOrder
                FROM dbo.WaterTestProfiles wp
                INNER JOIN dbo.WaterTestProfileTests wpt ON wp.ProfileID = wpt.ProfileID
                INNER JOIN dbo.Tests t ON wpt.TestID = t.TestID
                WHERE UPPER(LTRIM(RTRIM(wp.ProfileCode))) = @profileCode
                  AND ISNULL(wp.IsActive, 0) = 1
                  AND wp.ApprovalStatus = N'Approved'
                  AND NULLIF(LTRIM(RTRIM(ISNULL(wp.ControlledReference,N''))),N'') IS NOT NULL
                  AND wp.ApprovedBy IS NOT NULL
                  AND wp.ApprovedAt IS NOT NULL
                  AND (wp.EffectiveFrom IS NULL OR wp.EffectiveFrom <= CAST(GETDATE() AS date))
                  AND (wp.EffectiveTo IS NULL OR wp.EffectiveTo >= CAST(GETDATE() AS date))
                  AND ISNULL(wpt.IsActive, 1) = 1
                  AND ISNULL(t.IsActive, 0) = 1
                  AND EXISTS
                  (
                      SELECT 1
                      FROM dbo.WaterSpecifications specification
                      WHERE specification.ProfileID = wp.ProfileID
                        AND specification.TestID = t.TestID
                        AND ISNULL(specification.IsActive,0) = 1
                        AND specification.ApprovalStatus = N'Approved'
                        AND (specification.PointCode IS NULL OR LTRIM(RTRIM(specification.PointCode))=N'')
                        AND NULLIF(LTRIM(RTRIM(specification.SpecificationText)),N'') IS NOT NULL
                        AND (specification.EffectiveFrom IS NULL OR specification.EffectiveFrom <= CAST(GETDATE() AS date))
                        AND (specification.EffectiveTo IS NULL OR specification.EffectiveTo >= CAST(GETDATE() AS date))
                  )
                ORDER BY ISNULL(wpt.SortOrder, t.SortOrder), t.TestCategory, t.TestName";

            SqlParameter[] pars =
            {
                new SqlParameter("@profileCode", profileCode)
            };

            DataTable result = ExecuteQuery(query, pars);
            if (result.Rows.Count == 0)
            {
                throw new InvalidOperationException(
                    "No currently effective, approved controlled tests/specifications are configured for Water Test Profile " + profileCode + ". " +
                    "Sample registration is blocked until the profile is reviewed, approved, activated, and passes System Preflight.");
            }

            return result;
        }

        public static DataTable GetEffectiveWaterTestSpecification(string sampleType, int testId, string pointCode)
        {
            DataTable empty = new DataTable();
            empty.Columns.Add("LowerLimit", typeof(decimal));
            empty.Columns.Add("UpperLimit", typeof(decimal));
            empty.Columns.Add("AlertLimit", typeof(decimal));
            empty.Columns.Add("ActionLimit", typeof(decimal));
            empty.Columns.Add("SpecificationText", typeof(string));

            if (!TableExists("WaterSpecifications"))
                return empty;

            string profileCode = NormalizeWaterProfileCode(sampleType);
            string cleanPointCode = pointCode == null ? "" : pointCode.Trim();

            string query = @"
                SELECT TOP 1
                    specification.LowerLimit,
                    specification.UpperLimit,
                    specification.AlertLimit,
                    specification.ActionLimit,
                    specification.SpecificationText
                FROM dbo.WaterTestProfiles profile
                INNER JOIN dbo.WaterSpecifications specification
                    ON specification.ProfileID = profile.ProfileID
                WHERE UPPER(LTRIM(RTRIM(profile.ProfileCode))) = @profileCode
                  AND ISNULL(profile.IsActive,0) = 1
                  AND profile.ApprovalStatus = N'Approved'
                  AND profile.ApprovedBy IS NOT NULL
                  AND profile.ApprovedAt IS NOT NULL
                  AND NULLIF(LTRIM(RTRIM(ISNULL(profile.ControlledReference,N''))),N'') IS NOT NULL
                  AND (profile.EffectiveFrom IS NULL OR profile.EffectiveFrom <= CAST(GETDATE() AS date))
                  AND (profile.EffectiveTo IS NULL OR profile.EffectiveTo >= CAST(GETDATE() AS date))
                  AND specification.TestID = @testId
                  AND ISNULL(specification.IsActive,0) = 1
                  AND specification.ApprovalStatus = N'Approved'
                  AND NULLIF(LTRIM(RTRIM(specification.SpecificationText)),N'') IS NOT NULL
                  AND (specification.EffectiveFrom IS NULL OR specification.EffectiveFrom <= CAST(GETDATE() AS date))
                  AND (specification.EffectiveTo IS NULL OR specification.EffectiveTo >= CAST(GETDATE() AS date))
                  AND (specification.PointCode IS NULL OR specification.PointCode = N'' OR specification.PointCode = @pointCode)
                ORDER BY
                    CASE WHEN specification.PointCode = @pointCode THEN 0 ELSE 1 END,
                    specification.EffectiveFrom DESC,
                    specification.SpecificationID DESC";

            SqlParameter[] pars =
            {
                new SqlParameter("@profileCode", profileCode),
                new SqlParameter("@testId", testId),
                new SqlParameter("@pointCode", cleanPointCode)
            };

            return ExecuteQuery(query, pars);
        }


        public static DataTable GetEnvironmentalAreaGroups()
        {
            string query = @"
                SELECT DISTINCT AreaGroup
                FROM EM_Areas
                WHERE IsActive = 1
                  AND AreaGroup IS NOT NULL
                  AND LTRIM(RTRIM(AreaGroup)) <> ''
                ORDER BY AreaGroup";

            return ExecuteQuery(query);
        }

        public static DataTable GetEnvironmentalGrades()
        {
            string query = @"
        SELECT DISTINCT Grade
        FROM EM_Areas
        WHERE IsActive = 1
          AND Grade IS NOT NULL
          AND LTRIM(RTRIM(Grade)) <> ''
        ORDER BY Grade";

            return ExecuteQuery(query);

        }

        public static DataTable GetEnvironmentalAreas(string areaGroup, string grade)
        {
            string query = @"
                SELECT
                    Id AS PointID,
                    AreaCode AS PointCode,
                    AreaName + ' - ' + ISNULL(AreaGroup, '') + ' - ' + Grade AS Location,
                    AreaName,
                    AreaGroup,
                    Grade
                FROM EM_Areas
                WHERE IsActive = 1";

            List<SqlParameter> pars = new List<SqlParameter>();

            if (!string.IsNullOrWhiteSpace(areaGroup) && areaGroup != "All")
            {
                query += " AND AreaGroup = @areaGroup";
                pars.Add(new SqlParameter("@areaGroup", areaGroup));
            }

            if (!string.IsNullOrWhiteSpace(grade) && grade != "All")
            {
                query += " AND Grade = @grade";
                pars.Add(new SqlParameter("@grade", grade));
            }

            query += " ORDER BY AreaGroup, Grade, AreaCode";

            return ExecuteQuery(query, pars.ToArray());
        }

        private static string NormalizeEMSamplingMethod(string method)
        {
            string value = method == null ? "" : method.Trim();

            if (string.IsNullOrWhiteSpace(value) ||
                value.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Both", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Both Methods", StringComparison.OrdinalIgnoreCase))
                return "Both";

            if (value.Equals("Active Air", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Air Sampling", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Active Air Sampling", StringComparison.OrdinalIgnoreCase))
                return "Active Air Sampling";

            if (value.Equals("Settle", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Settle Plate", StringComparison.OrdinalIgnoreCase))
                return "Settle Plate";

            return value;
        }

        public static DataTable GetEMAreaTemplates(int areaId, string method)
        {
            string normalizedMethod = NormalizeEMSamplingMethod(method);

            string query = @"
                SELECT
                    Id,
                    AreaId,
                    LTRIM(RTRIM(Method)) AS Method,
                    LTRIM(RTRIM(PlateCode)) AS PlateCode,
                    SequenceNo,
                    IsActive
                FROM dbo.EM_AreaTemplates
                WHERE AreaId = @areaId
                  AND ISNULL(IsActive, 1) = 1
                  AND PlateCode IS NOT NULL
                  AND LTRIM(RTRIM(PlateCode)) <> ''";

            List<SqlParameter> pars = new List<SqlParameter>
            {
                new SqlParameter("@areaId", areaId)
            };

            if (!normalizedMethod.Equals("Both", StringComparison.OrdinalIgnoreCase))
            {
                query += " AND UPPER(LTRIM(RTRIM(Method))) = UPPER(@method)";
                pars.Add(new SqlParameter("@method", normalizedMethod));
            }

            query += @"
                ORDER BY
                    CASE
                        WHEN LTRIM(RTRIM(Method)) = 'Settle Plate' THEN 1
                        WHEN LTRIM(RTRIM(Method)) = 'Active Air Sampling' THEN 2
                        ELSE 3
                    END,
                    SequenceNo,
                    PlateCode";

            return ExecuteQuery(query, pars.ToArray());
        }

        public static DataTable GetEMGradeLimit(string grade, string method)
        {
            string query = @"
                SELECT TOP 1 *
                FROM EM_GradeLimits
                WHERE ISNULL(IsActive,1)=1
                  AND UPPER(LTRIM(RTRIM(ISNULL(Grade,N''))))=UPPER(LTRIM(RTRIM(@grade)))
                  AND UPPER(LTRIM(RTRIM(ISNULL(Method,N''))))=UPPER(LTRIM(RTRIM(@method)))
                ORDER BY Id DESC";

            SqlParameter[] pars =
            {
                new SqlParameter("@grade", grade ?? ""),
                new SqlParameter("@method", method ?? "")
            };

            return ExecuteQuery(query, pars);
        }

        public static int InsertSample(
            string sampleNumber,
            int? pointId,
            string sampleType,
            string samplingMethod,
            string grade,
            int? exposureTime,
            int? samplingVolume,
            string activity,
            string sampledBy,
            string status)
        {
            string query = @"
                INSERT INTO Samples
                (
                    SampleNumber,
                    PointID,
                    SampleType,
                    SamplingMethod,
                    Grade,
                    ExposureTime,
                    SamplingVolume,
                    Activity,
                    SamplingDateTime,
                    SampledBy,
                    Status,
                    LabelPrinted
                )
                VALUES
                (
                    @sampleNumber,
                    @pointId,
                    @sampleType,
                    @samplingMethod,
                    @grade,
                    @exposureTime,
                    @samplingVolume,
                    @activity,
                    GETDATE(),
                    @sampledBy,
                    @status,
                    0
                );

                SELECT SCOPE_IDENTITY();";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleNumber", sampleNumber),
                new SqlParameter("@pointId", pointId.HasValue ? (object)pointId.Value : DBNull.Value),
                new SqlParameter("@sampleType", sampleType),
                new SqlParameter("@samplingMethod", string.IsNullOrWhiteSpace(samplingMethod) ? "N/A" : samplingMethod),
                new SqlParameter("@grade", string.IsNullOrWhiteSpace(grade) ? (object)DBNull.Value : grade),
                new SqlParameter("@exposureTime", exposureTime.HasValue ? (object)exposureTime.Value : DBNull.Value),
                new SqlParameter("@samplingVolume", samplingVolume.HasValue ? (object)samplingVolume.Value : DBNull.Value),
                new SqlParameter("@activity", string.IsNullOrWhiteSpace(activity) ? (object)DBNull.Value : activity),
                new SqlParameter("@sampledBy", sampledBy),
                new SqlParameter("@status", status)
            };

            return Convert.ToInt32(ExecuteScalar(query, pars));
        }

        public static int InsertSampleTests(int sampleId, int testId)
        {
            string query = @"
                INSERT INTO SampleTests
                (
                    SampleID,
                    TestID
                )
                VALUES
                (
                    @sampleId,
                    @testId
                )";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleId", sampleId),
                new SqlParameter("@testId", testId)
            };

            return ExecuteNonQuery(query, pars);
        }

        public static int InsertEMEvent(
            string eventNo,
            int areaId,
            DateTime eventDate,
            string arNo,
            string sanitizationDetails,
            string sanitizationTime,
            string disinfectantUsed,
            string mediaUsed,
            string mediaLotNo,
            string samplingTimeFrom,
            string samplingTimeTo,
            string activityNoOfPersons,
            string airSamplerNo,
            string airSamplingTime,
            string incubationTemperature,
            string incubatorNo1,
            string incubatorNo2,
            string incubationStart,
            string incubationEnd,
            string remarks)
        {
            string query = @"
                INSERT INTO EM_Events
                (
                    EventNo,
                    AreaId,
                    EventDate,
                    ARNo,
                    SanitizationDetails,
                    SanitizationTime,
                    DisinfectantUsed,
                    MediaUsed,
                    MediaLotNo,
                    SamplingTimeFrom,
                    SamplingTimeTo,
                    ActivityNoOfPersons,
                    AirSamplerNo,
                    AirSamplingTime,
                    IncubationTemperature,
                    IncubatorNo1,
                    IncubatorNo2,
                    IncubationStart,
                    IncubationEnd,
                    FinalResult,
                    Remarks,
                    CreatedAt
                )
                VALUES
                (
                    @eventNo,
                    @areaId,
                    @eventDate,
                    @arNo,
                    @sanitizationDetails,
                    @sanitizationTime,
                    @disinfectantUsed,
                    @mediaUsed,
                    @mediaLotNo,
                    @samplingTimeFrom,
                    @samplingTimeTo,
                    @activityNoOfPersons,
                    @airSamplerNo,
                    @airSamplingTime,
                    @incubationTemperature,
                    @incubatorNo1,
                    @incubatorNo2,
                    @incubationStart,
                    @incubationEnd,
                    'Pending',
                    @remarks,
                    GETDATE()
                );

                SELECT SCOPE_IDENTITY();";

            SqlParameter[] pars =
            {
                new SqlParameter("@eventNo", eventNo),
                new SqlParameter("@areaId", areaId),
                new SqlParameter("@eventDate", eventDate.Date),
                new SqlParameter("@arNo", string.IsNullOrWhiteSpace(arNo) ? (object)DBNull.Value : arNo),
                new SqlParameter("@sanitizationDetails", string.IsNullOrWhiteSpace(sanitizationDetails) ? (object)DBNull.Value : sanitizationDetails),
                new SqlParameter("@sanitizationTime", string.IsNullOrWhiteSpace(sanitizationTime) ? (object)DBNull.Value : sanitizationTime),
                new SqlParameter("@disinfectantUsed", string.IsNullOrWhiteSpace(disinfectantUsed) ? (object)DBNull.Value : disinfectantUsed),
                new SqlParameter("@mediaUsed", string.IsNullOrWhiteSpace(mediaUsed) ? (object)DBNull.Value : mediaUsed),
                new SqlParameter("@mediaLotNo", string.IsNullOrWhiteSpace(mediaLotNo) ? (object)DBNull.Value : mediaLotNo),
                new SqlParameter("@samplingTimeFrom", string.IsNullOrWhiteSpace(samplingTimeFrom) ? (object)DBNull.Value : samplingTimeFrom),
                new SqlParameter("@samplingTimeTo", string.IsNullOrWhiteSpace(samplingTimeTo) ? (object)DBNull.Value : samplingTimeTo),
                new SqlParameter("@activityNoOfPersons", string.IsNullOrWhiteSpace(activityNoOfPersons) ? (object)DBNull.Value : activityNoOfPersons),
                new SqlParameter("@airSamplerNo", string.IsNullOrWhiteSpace(airSamplerNo) ? (object)DBNull.Value : airSamplerNo),
                new SqlParameter("@airSamplingTime", string.IsNullOrWhiteSpace(airSamplingTime) ? (object)DBNull.Value : airSamplingTime),
                new SqlParameter("@incubationTemperature", string.IsNullOrWhiteSpace(incubationTemperature) ? (object)DBNull.Value : incubationTemperature),
                new SqlParameter("@incubatorNo1", string.IsNullOrWhiteSpace(incubatorNo1) ? (object)DBNull.Value : incubatorNo1),
                new SqlParameter("@incubatorNo2", string.IsNullOrWhiteSpace(incubatorNo2) ? (object)DBNull.Value : incubatorNo2),
                new SqlParameter("@incubationStart", string.IsNullOrWhiteSpace(incubationStart) ? (object)DBNull.Value : incubationStart),
                new SqlParameter("@incubationEnd", string.IsNullOrWhiteSpace(incubationEnd) ? (object)DBNull.Value : incubationEnd),
                new SqlParameter("@remarks", string.IsNullOrWhiteSpace(remarks) ? (object)DBNull.Value : remarks)
            };

            return Convert.ToInt32(ExecuteScalar(query, pars));
        }

        public static int InsertEMEventPlatesFromTemplate(int eventId, int areaId, string method)
        {
            if (eventId <= 0)
                throw new ArgumentException("Invalid EM event id.", nameof(eventId));

            if (areaId <= 0)
                throw new ArgumentException("Invalid EM area id.", nameof(areaId));

            string normalizedMethod = NormalizeEMSamplingMethod(method);

            string query = @"
                INSERT INTO dbo.EM_EventPlates
                (
                    EventId,
                    Method,
                    PlateCode,
                    SequenceNo,
                    TotalCount,
                    FungalCount,
                    ColoniesObserved,
                    CorrectedCount,
                    ResultCFU,
                    Status,
                    CreatedAt
                )
                SELECT
                    @eventId,
                    LTRIM(RTRIM(T.Method)),
                    LTRIM(RTRIM(T.PlateCode)),
                    T.SequenceNo,
                    NULL,
                    NULL,
                    NULL,
                    NULL,
                    NULL,
                    'Pending',
                    GETDATE()
                FROM dbo.EM_AreaTemplates T
                WHERE T.AreaId = @areaId
                  AND ISNULL(T.IsActive, 1) = 1
                  AND T.PlateCode IS NOT NULL
                  AND LTRIM(RTRIM(T.PlateCode)) <> ''
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM dbo.EM_EventPlates P
                      WHERE P.EventId = @eventId
                        AND LTRIM(RTRIM(P.PlateCode)) = LTRIM(RTRIM(T.PlateCode))
                  )";

            List<SqlParameter> pars = new List<SqlParameter>
            {
                new SqlParameter("@eventId", eventId),
                new SqlParameter("@areaId", areaId)
            };

            if (!normalizedMethod.Equals("Both", StringComparison.OrdinalIgnoreCase))
            {
                query += " AND UPPER(LTRIM(RTRIM(T.Method))) = UPPER(@method)";
                pars.Add(new SqlParameter("@method", normalizedMethod));
            }

            return ExecuteNonQuery(query, pars.ToArray());
        }

        public static DataTable GetSampleTests(int sampleId)
        {
            string query = @"
                SELECT
                    st.SampleTestID,
                    st.TestID,
                    COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName,N'') AS TestName,
                    CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN ISNULL(st.TestCategorySnapshot,N'') ELSE ISNULL(t.TestCategory,N'') END AS TestCategory,
                    CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.AlertLimitSnapshot ELSE t.AlertLimit END AS AlertLimit,
                    CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.ActionLimitSnapshot ELSE t.ActionLimit END AS ActionLimit,
                    CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN ISNULL(st.UnitSnapshot,N'') ELSE ISNULL(t.Unit,N'') END AS Unit,
                    st.ResultValue,
                    st.Remarks
                FROM SampleTests st
                LEFT JOIN Tests t ON st.TestID = t.TestID
                WHERE st.SampleID = @sampleId
                ORDER BY CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN ISNULL(st.TestCategorySnapshot,N'') ELSE ISNULL(t.TestCategory,N'') END,
                         COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName,N'')";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleId", sampleId)
            };

            return ExecuteQuery(query, pars);
        }

        public static int UpdateSampleTestResult(int sampleTestId, decimal result, string remarks, bool? isPass, string enteredBy)
        {
            string query = @"
                UPDATE SampleTests
                SET
                    ResultValue = @result,
                    Remarks = @remarks,
                    IsPass = @isPass,
                    ResultEnteredDate = GETDATE(),
                    EnteredBy = @enteredBy
                WHERE SampleTestID = @sampleTestId";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleTestId", sampleTestId),
                new SqlParameter("@result", result),
                new SqlParameter("@remarks", string.IsNullOrWhiteSpace(remarks) ? (object)DBNull.Value : remarks),
                new SqlParameter("@isPass", isPass.HasValue ? (object)isPass.Value : DBNull.Value),
                new SqlParameter("@enteredBy", enteredBy)
            };

            return ExecuteNonQuery(query, pars);
        }

        public static void EnsureSampleTestStatusColumns()
        {
            string[] requiredColumns = { "ResultStatus", "DeviationType", "LimitDescription" };
            foreach (string column in requiredColumns)
            {
                if (!ColumnExists("SampleTests", column))
                    throw new InvalidOperationException("Required column dbo.SampleTests." + column + " is missing. Apply Database/Migrations/20260714_001_Harden_GMP_Workflows.sql.");
            }
        }

        public static int UpdateSampleTestResultStatus(
            int sampleTestId,
            string resultStatus,
            string deviationType,
            string limitDescription)
        {
            EnsureSampleTestStatusColumns();

            string query = @"
                UPDATE dbo.SampleTests
                SET
                    ResultStatus = @resultStatus,
                    DeviationType = @deviationType,
                    LimitDescription = @limitDescription
                WHERE SampleTestID = @sampleTestId";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleTestId", sampleTestId),
                new SqlParameter("@resultStatus", string.IsNullOrWhiteSpace(resultStatus) ? (object)DBNull.Value : resultStatus.Trim()),
                new SqlParameter("@deviationType", string.IsNullOrWhiteSpace(deviationType) ? (object)DBNull.Value : deviationType.Trim()),
                new SqlParameter("@limitDescription", string.IsNullOrWhiteSpace(limitDescription) ? (object)DBNull.Value : limitDescription.Trim())
            };

            return ExecuteNonQuery(query, pars);
        }

        public static int UpdateSampleStatus(int sampleId, string status)
        {
            string query = "UPDATE Samples SET Status = @status WHERE SampleID = @sampleId";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleId", sampleId),
                new SqlParameter("@status", status)
            };

            return ExecuteNonQuery(query, pars);
        }

        public static void SyncWaterPlanStatusForSample(int sampleId)
        {
            if (!TableExists("Water_Plans") || !TableExists("Water_PlanSamples"))
                return;

            DataTable linked = ExecuteQuery(@"
SELECT TOP 1 p.WaterPlanID,p.PlanNo,p.Status
FROM dbo.Water_PlanSamples ps INNER JOIN dbo.Water_Plans p ON p.WaterPlanID=ps.WaterPlanID
WHERE ps.SampleID=@SampleID;", new[] { new SqlParameter("@SampleID", sampleId) });
            if (linked.Rows.Count == 0) return;

            int planId = Convert.ToInt32(linked.Rows[0]["WaterPlanID"], CultureInfo.InvariantCulture);
            string planNo = linked.Rows[0]["PlanNo"]?.ToString() ?? "";
            string oldStatus = linked.Rows[0]["Status"]?.ToString() ?? "";

            ExecuteNonQuery(@"
UPDATE ps SET Status=CASE WHEN s.Status IN(N'Approved',N'COA Issued') THEN N'Approved' ELSE ps.Status END
FROM dbo.Water_PlanSamples ps INNER JOIN dbo.Samples s ON s.SampleID=ps.SampleID
WHERE ps.WaterPlanID=@Plan;

UPDATE p SET Status=CASE
 WHEN p.Status=N'Cancelled' THEN p.Status
 WHEN EXISTS(SELECT 1 FROM dbo.Water_PlanSamples x WHERE x.WaterPlanID=p.WaterPlanID AND x.Status=N'Rejected') THEN N'Recollection Required'
 WHEN NOT EXISTS
 (
   SELECT 1 FROM dbo.Water_PlanSamples x LEFT JOIN dbo.Samples s ON s.SampleID=x.SampleID
   WHERE x.WaterPlanID=p.WaterPlanID AND (x.SampleID IS NULL OR ISNULL(s.Status,N'') NOT IN(N'Approved',N'COA Issued'))
 ) THEN N'Completed'
 ELSE N'Analysis In Progress' END
FROM dbo.Water_Plans p WHERE p.WaterPlanID=@Plan;",
                new[] { new SqlParameter("@Plan", planId) });

            string newStatus = ExecuteScalar("SELECT Status FROM dbo.Water_Plans WHERE WaterPlanID=@Plan",
                new[] { new SqlParameter("@Plan", planId) })?.ToString() ?? oldStatus;
            if (!newStatus.Equals(oldStatus, StringComparison.OrdinalIgnoreCase))
                AddAuditTrailAdvanced("Water_Plans", planId, "Water Plan Status Synchronized", oldStatus, newStatus,
                    "Automatically synchronized from approved water samples", ResolveAuthenticatedSigner(Login.CurrentUser),
                    "Status", "", planNo, "Water");
        }

    }
}
