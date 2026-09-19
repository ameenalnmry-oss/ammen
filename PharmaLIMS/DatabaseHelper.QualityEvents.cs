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
    // Quality Event, investigation checklist, and related controlled evidence operations.
    public static partial class DatabaseHelper
    {
        public static int GetPendingResultsCount(int sampleId)
        {
            string query = @"
                SELECT COUNT(*)
                FROM SampleTests
                WHERE SampleID = @sampleId
                  AND ResultValue IS NULL";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleId", sampleId)
            };

            object result = ExecuteScalar(query, pars);
            return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
        }


        public static bool HasOpenQualityEvent(int sampleId)
        {
            if (!TableExists("QualityEvents"))
                throw new InvalidOperationException("Quality Event compliance table is unavailable. Controlled workflow verification cannot continue.");

            string query = @"
                SELECT COUNT(*)
                FROM dbo.QualityEvents
                WHERE SampleID = @sampleId
                  AND CurrentStatus NOT IN ('Closed', 'QA Closed', 'Cancelled', 'Rejected Closed')";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleId", sampleId)
            };

            object result = ExecuteScalar(query, pars);
            return result != null && result != DBNull.Value && Convert.ToInt32(result) > 0;
        }

        public static string GetOpenQualityEventSummary(int sampleId)
        {
            if (!TableExists("QualityEvents"))
                throw new InvalidOperationException("Quality Event compliance table is unavailable. Controlled workflow verification cannot continue.");

            string query = @"
                SELECT TOP 1
                    EventNumber,
                    EventType,
                    CurrentStatus,
                    Severity
                FROM dbo.QualityEvents
                WHERE SampleID = @sampleId
                  AND CurrentStatus NOT IN ('Closed', 'QA Closed', 'Cancelled', 'Rejected Closed')
                ORDER BY QualityEventID DESC";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleId", sampleId)
            };

            DataTable dt = ExecuteQuery(query, pars);
            if (dt.Rows.Count == 0)
                return "";

            DataRow row = dt.Rows[0];
            return row.GetSafeString("EventNumber") + " - " +
                   row.GetSafeString("EventType") + " - " +
                   row.GetSafeString("CurrentStatus") + " - " +
                   row.GetSafeString("Severity");
        }

        public static string CreateOrUpdateQualityEventForOOS(int sampleId, string detectedBy, string initialDescription)
        {
            string summary = string.Empty;
            ExecuteInTransaction((conn, tx) =>
            {
                summary = CreateOrUpdateQualityEventForOOS(conn, tx, sampleId, detectedBy, initialDescription);
            });

            return summary;
        }

        public static string CreateOrUpdateQualityEventForOOS(
            SqlConnection conn,
            SqlTransaction tx,
            int sampleId,
            string detectedBy,
            string initialDescription)
        {
            if (conn == null)
                throw new ArgumentNullException(nameof(conn));
            if (tx == null)
                throw new ArgumentNullException(nameof(tx));
            if (sampleId <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleId));

            using (SqlCommand schemaCmd = new SqlCommand(@"
                IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
                    THROW 51000, 'QualityEvents table is missing.', 1;
                IF OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NULL
                    THROW 51000, 'QualityEventAffectedResults table is missing.', 1;
                IF OBJECT_ID(N'dbo.QualityEventActions', N'U') IS NULL
                    THROW 51000, 'QualityEventActions table is missing.', 1;", conn, tx))
            {
                schemaCmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                schemaCmd.ExecuteNonQuery();
            }

            string sampleNumber;
            using (SqlCommand sampleCmd = new SqlCommand(@"
                SELECT SampleNumber
                FROM dbo.Samples WITH (UPDLOCK, HOLDLOCK)
                WHERE SampleID = @sampleId;", conn, tx))
            {
                sampleCmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                sampleCmd.Parameters.Add("@sampleId", SqlDbType.Int).Value = sampleId;
                object value = sampleCmd.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    throw new InvalidOperationException("The sample no longer exists. Quality Event creation was cancelled.");
                sampleNumber = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            int qualityEventId = 0;
            string eventNumber = string.Empty;
            bool createdEvent = false;

            using (SqlCommand existingCmd = new SqlCommand(@"
                SELECT TOP 1 QualityEventID, EventNumber
                FROM dbo.QualityEvents WITH (UPDLOCK, HOLDLOCK)
                WHERE SampleID = @sampleId
                  AND EventType = 'OOS'
                  AND CurrentStatus NOT IN ('Closed', 'QA Closed', 'Cancelled', 'Rejected Closed')
                ORDER BY QualityEventID DESC", conn, tx))
            {
                existingCmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                existingCmd.Parameters.Add("@sampleId", SqlDbType.Int).Value = sampleId;
                using (SqlDataReader reader = existingCmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        qualityEventId = Convert.ToInt32(reader["QualityEventID"], CultureInfo.InvariantCulture);
                        eventNumber = Convert.ToString(reader["EventNumber"], CultureInfo.InvariantCulture) ?? string.Empty;
                    }
                }
            }

            if (qualityEventId == 0)
            {
                eventNumber = GetNextQualityEventNumber(conn, tx);

                using (SqlCommand createCmd = new SqlCommand(@"
                    INSERT INTO dbo.QualityEvents
                    (
                        EventNumber, EventType, Severity, SampleID, SampleNumber, CurrentStatus,
                        DetectedBy, DetectedDate, DetectionSource, InitialDescription, ImmediateAction,
                        CAPARequired, CreatedDate, ModifiedBy, ModifiedDate
                    )
                    OUTPUT INSERTED.QualityEventID
                    VALUES
                    (
                        @eventNumber, 'OOS', 'Major', @sampleId, @sampleNumber, 'Open',
                        @detectedBy, GETDATE(), 'Result Entry', @initialDescription,
                        'Sample placed on hold. Approval and COA issuance are blocked pending QA disposition.',
                        0, GETDATE(), @detectedBy, GETDATE()
                    );", conn, tx))
                {
                    createCmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    createCmd.Parameters.Add("@eventNumber", SqlDbType.NVarChar, 50).Value = eventNumber;
                    createCmd.Parameters.Add("@sampleId", SqlDbType.Int).Value = sampleId;
                    createCmd.Parameters.Add("@sampleNumber", SqlDbType.NVarChar, 50).Value = string.IsNullOrWhiteSpace(sampleNumber) ? (object)DBNull.Value : sampleNumber.Trim();
                    createCmd.Parameters.Add("@detectedBy", SqlDbType.NVarChar, 100).Value = string.IsNullOrWhiteSpace(detectedBy) ? (object)DBNull.Value : detectedBy.Trim();
                    createCmd.Parameters.Add("@initialDescription", SqlDbType.NVarChar, -1).Value = string.IsNullOrWhiteSpace(initialDescription) ? "OOS detected during result entry." : initialDescription.Trim();
                    qualityEventId = Convert.ToInt32(createCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                    createdEvent = true;
                }

                ExecuteNonQueryWithTransaction(@"
                    INSERT INTO dbo.QualityEventActions
                    (QualityEventID, ActionType, ActionDescription, PerformedBy, PerformedDate)
                    VALUES
                    (@qualityEventId, 'System Opened',
                     'Quality Event opened automatically because one or more OOS results were saved.',
                     @performedBy, GETDATE())",
                    new[]
                    {
                        new SqlParameter("@qualityEventId", qualityEventId),
                        new SqlParameter("@performedBy", string.IsNullOrWhiteSpace(detectedBy) ? (object)DBNull.Value : detectedBy.Trim())
                    }, conn, tx);
            }
            else
            {
                ExecuteNonQueryWithTransaction(@"
                    UPDATE dbo.QualityEvents
                    SET ModifiedBy = @modifiedBy, ModifiedDate = GETDATE()
                    WHERE QualityEventID = @qualityEventId",
                    new[]
                    {
                        new SqlParameter("@qualityEventId", qualityEventId),
                        new SqlParameter("@modifiedBy", string.IsNullOrWhiteSpace(detectedBy) ? (object)DBNull.Value : detectedBy.Trim())
                    }, conn, tx);
            }

            int affectedInserted = ExecuteNonQueryWithTransaction(@"
                INSERT INTO dbo.QualityEventAffectedResults
                (
                    QualityEventID, SampleTestID, TestID, TestName, ResultValue,
                    SpecificationLimit, Unit, FailureType, CreatedDate
                )
                SELECT
                    @qualityEventId, st.SampleTestID, st.TestID,
                    COALESCE(NULLIF(st.TestNameSnapshot,N''), t.TestName, N'Unmapped Test'),
                    st.ResultValue,
                    CASE
                        WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN
                            COALESCE(NULLIF(st.LimitDescription,N''),
                                CASE
                                    WHEN st.ActionLimitSnapshot IS NOT NULL THEN N'Action Limit: ' + CONVERT(NVARCHAR(50), st.ActionLimitSnapshot)
                                    WHEN st.AlertLimitSnapshot IS NOT NULL THEN N'Alert Limit: ' + CONVERT(NVARCHAR(50), st.AlertLimitSnapshot)
                                    ELSE NULL
                                END)
                        ELSE
                            CASE
                                WHEN t.ActionLimit IS NOT NULL THEN N'Action Limit: ' + CONVERT(NVARCHAR(50), t.ActionLimit)
                                WHEN t.AlertLimit IS NOT NULL THEN N'Alert Limit: ' + CONVERT(NVARCHAR(50), t.AlertLimit)
                                ELSE NULL
                            END
                    END,
                    CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.UnitSnapshot ELSE t.Unit END,
                    ISNULL(st.ResultStatus, 'OOS'), SYSDATETIME()
                FROM dbo.SampleTests st
                LEFT JOIN dbo.Tests t ON st.TestID = t.TestID
                WHERE st.SampleID = @sampleId
                  AND UPPER(ISNULL(st.ResultStatus, '')) IN ('OOS', 'ACTION', 'FAIL')
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM dbo.QualityEventAffectedResults qar
                      WHERE qar.QualityEventID = @qualityEventId
                        AND qar.SampleTestID = st.SampleTestID
                  )",
                new[]
                {
                    new SqlParameter("@qualityEventId", qualityEventId),
                    new SqlParameter("@sampleId", sampleId)
                }, conn, tx);

            using (SqlCommand affectedCountCmd = new SqlCommand(@"
                SELECT COUNT(1)
                FROM dbo.QualityEventAffectedResults
                WHERE QualityEventID = @qualityEventId;", conn, tx))
            {
                affectedCountCmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                affectedCountCmd.Parameters.Add("@qualityEventId", SqlDbType.Int).Value = qualityEventId;
                int totalAffected = Convert.ToInt32(affectedCountCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (totalAffected <= 0)
                    throw new InvalidOperationException("The OOS Quality Event has no linked affected result. Result entry was cancelled.");
            }

            if (!createdEvent && affectedInserted > 0)
            {
                ExecuteNonQueryWithTransaction(@"
                    INSERT INTO dbo.QualityEventActions
                    (QualityEventID, ActionType, ActionDescription, PerformedBy, PerformedDate)
                    VALUES
                    (@qualityEventId, 'OOS Result Linked',
                     'Additional OOS/ACTION result evidence was linked during controlled result entry.',
                     @performedBy, GETDATE())",
                    new[]
                    {
                        new SqlParameter("@qualityEventId", qualityEventId),
                        new SqlParameter("@performedBy", string.IsNullOrWhiteSpace(detectedBy) ? (object)DBNull.Value : detectedBy.Trim())
                    }, conn, tx);
            }

            AddAuditTrailAdvanced(
                conn,
                tx,
                "QualityEvents",
                qualityEventId,
                createdEvent ? "Open Quality Event" : "Update Quality Event",
                createdEvent ? string.Empty : "Existing open OOS Quality Event",
                "EventNumber=" + eventNumber + "; AffectedResultsAdded=" + affectedInserted.ToString(CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(initialDescription) ? "OOS/ACTION result evidence linked during result entry." : initialDescription,
                detectedBy,
                "CurrentStatus",
                null,
                sampleNumber,
                "Water");

            ExecuteNonQueryWithTransaction(@"
                UPDATE dbo.Samples
                SET Status = 'Under Investigation',
                    ModifiedBy = @modifiedBy,
                    ModifiedDate = GETDATE()
                WHERE SampleID = @sampleId
                  AND Status NOT IN ('Rejected', 'COA Issued')",
                new[]
                {
                    new SqlParameter("@sampleId", sampleId),
                    new SqlParameter("@modifiedBy", string.IsNullOrWhiteSpace(detectedBy) ? (object)DBNull.Value : detectedBy.Trim())
                }, conn, tx);

            using (SqlCommand summaryCmd = new SqlCommand(@"
                SELECT TOP 1
                    EventNumber, EventType, CurrentStatus, Severity
                FROM dbo.QualityEvents
                WHERE QualityEventID = @qualityEventId;", conn, tx))
            {
                summaryCmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                summaryCmd.Parameters.Add("@qualityEventId", SqlDbType.Int).Value = qualityEventId;
                using (SqlDataReader reader = summaryCmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return (Convert.ToString(reader["EventNumber"], CultureInfo.InvariantCulture) ?? string.Empty) + " - " +
                               (Convert.ToString(reader["EventType"], CultureInfo.InvariantCulture) ?? string.Empty) + " - " +
                               (Convert.ToString(reader["CurrentStatus"], CultureInfo.InvariantCulture) ?? string.Empty) + " - " +
                               (Convert.ToString(reader["Severity"], CultureInfo.InvariantCulture) ?? string.Empty);
                    }
                }
            }

            throw new InvalidOperationException("Quality Event could not be re-read inside the result transaction.");
        }

        public static string CreateOrUpdateQualityEventForAlert(int sampleId, string detectedBy, string initialDescription)
        {
            if (!TableExists("QualityEvents") || !TableExists("QualityEventAffectedResults"))
                return "Quality Events database update is not installed.";

            string sampleNumber = ExecuteScalar(
                "SELECT SampleNumber FROM dbo.Samples WHERE SampleID = @sampleId",
                new[] { new SqlParameter("@sampleId", sampleId) })?.ToString() ?? "";

            ExecuteInTransaction((conn, tx) =>
            {
                int qualityEventId = 0;
                string eventNumber = "";

                using (SqlCommand existingCmd = new SqlCommand(@"
                    SELECT TOP 1 QualityEventID, EventNumber
                    FROM dbo.QualityEvents
                    WHERE SampleID = @sampleId
                      AND EventType = 'Alert'
                      AND CurrentStatus NOT IN ('Closed', 'QA Closed', 'Cancelled', 'Rejected Closed')
                    ORDER BY QualityEventID DESC", conn, tx))
                {
                    existingCmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    existingCmd.Parameters.Add("@sampleId", SqlDbType.Int).Value = sampleId;
                    using (SqlDataReader reader = existingCmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            qualityEventId = Convert.ToInt32(reader["QualityEventID"]);
                            eventNumber = reader["EventNumber"]?.ToString() ?? "";
                        }
                    }
                }

                if (qualityEventId == 0)
                {
                    eventNumber = GetNextQualityEventNumber(conn, tx);

                    using (SqlCommand createCmd = new SqlCommand(@"
                        INSERT INTO dbo.QualityEvents
                        (
                            EventNumber,
                            EventType,
                            Severity,
                            SampleID,
                            SampleNumber,
                            CurrentStatus,
                            DetectedBy,
                            DetectedDate,
                            DetectionSource,
                            InitialDescription,
                            ImmediateAction,
                            CAPARequired,
                            CreatedDate,
                            ModifiedBy,
                            ModifiedDate
                        )
                        OUTPUT INSERTED.QualityEventID
                        VALUES
                        (
                            @eventNumber,
                            'Alert',
                            'Minor',
                            @sampleId,
                            @sampleNumber,
                            'Open',
                            @detectedBy,
                            GETDATE(),
                            'Result Entry',
                            @initialDescription,
                            'Alert result documented. Review trend/history and determine whether CAPA or escalation is required.',
                            0,
                            GETDATE(),
                            @detectedBy,
                            GETDATE()
                        );", conn, tx))
                    {
                        createCmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        createCmd.Parameters.Add("@eventNumber", SqlDbType.NVarChar, 50).Value = eventNumber;
                        createCmd.Parameters.Add("@sampleId", SqlDbType.Int).Value = sampleId;
                        createCmd.Parameters.Add("@sampleNumber", SqlDbType.NVarChar, 50).Value = string.IsNullOrWhiteSpace(sampleNumber) ? (object)DBNull.Value : sampleNumber.Trim();
                        createCmd.Parameters.Add("@detectedBy", SqlDbType.NVarChar, 100).Value = string.IsNullOrWhiteSpace(detectedBy) ? (object)DBNull.Value : detectedBy.Trim();
                        createCmd.Parameters.Add("@initialDescription", SqlDbType.NVarChar, -1).Value = string.IsNullOrWhiteSpace(initialDescription) ? "Alert detected during result entry." : initialDescription.Trim();

                        qualityEventId = Convert.ToInt32(createCmd.ExecuteScalar());
                    }

                    ExecuteNonQueryWithTransaction(@"
                        INSERT INTO dbo.QualityEventActions
                        (
                            QualityEventID,
                            ActionType,
                            ActionDescription,
                            PerformedBy,
                            PerformedDate
                        )
                        VALUES
                        (
                            @qualityEventId,
                            'System Opened',
                            'Quality Event opened automatically because one or more ALERT results were saved.',
                            @performedBy,
                            GETDATE()
                        )",
                        new[]
                        {
                            new SqlParameter("@qualityEventId", qualityEventId),
                            new SqlParameter("@performedBy", string.IsNullOrWhiteSpace(detectedBy) ? (object)DBNull.Value : detectedBy)
                        }, conn, tx);
                }
                else
                {
                    ExecuteNonQueryWithTransaction(@"
                        UPDATE dbo.QualityEvents
                        SET ModifiedBy = @modifiedBy,
                            ModifiedDate = GETDATE()
                        WHERE QualityEventID = @qualityEventId",
                        new[]
                        {
                            new SqlParameter("@qualityEventId", qualityEventId),
                            new SqlParameter("@modifiedBy", string.IsNullOrWhiteSpace(detectedBy) ? (object)DBNull.Value : detectedBy)
                        }, conn, tx);
                }

                ExecuteNonQueryWithTransaction(@"
                    INSERT INTO dbo.QualityEventAffectedResults
                    (
                        QualityEventID,
                        SampleTestID,
                        TestID,
                        TestName,
                        ResultValue,
                        SpecificationLimit,
                        Unit,
                        FailureType,
                        CreatedDate
                    )
                    SELECT
                        @qualityEventId,
                        st.SampleTestID,
                        st.TestID,
                        COALESCE(NULLIF(st.TestNameSnapshot,N''), t.TestName, N'Unmapped Test'),
                        st.ResultValue,
                        CASE
                            WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN
                                COALESCE(NULLIF(st.LimitDescription,N''),
                                    CASE
                                        WHEN st.AlertLimitSnapshot IS NOT NULL AND st.ActionLimitSnapshot IS NOT NULL
                                            THEN N'Alert Limit: ' + CONVERT(NVARCHAR(50), st.AlertLimitSnapshot) +
                                                 N'; Action Limit: ' + CONVERT(NVARCHAR(50), st.ActionLimitSnapshot)
                                        WHEN st.AlertLimitSnapshot IS NOT NULL
                                            THEN N'Alert Limit: ' + CONVERT(NVARCHAR(50), st.AlertLimitSnapshot)
                                        ELSE NULL
                                    END)
                            ELSE
                                CASE
                                    WHEN t.AlertLimit IS NOT NULL AND t.ActionLimit IS NOT NULL
                                        THEN N'Alert Limit: ' + CONVERT(NVARCHAR(50), t.AlertLimit) +
                                             N'; Action Limit: ' + CONVERT(NVARCHAR(50), t.ActionLimit)
                                    WHEN t.AlertLimit IS NOT NULL
                                        THEN N'Alert Limit: ' + CONVERT(NVARCHAR(50), t.AlertLimit)
                                    ELSE NULL
                                END
                        END,
                        CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.UnitSnapshot ELSE t.Unit END,
                        ISNULL(st.ResultStatus, 'ALERT'),
                        SYSDATETIME()
                    FROM dbo.SampleTests st
                    LEFT JOIN dbo.Tests t ON st.TestID = t.TestID
                    WHERE st.SampleID = @sampleId
                      AND UPPER(ISNULL(st.ResultStatus, '')) = 'ALERT'
                      AND NOT EXISTS
                      (
                          SELECT 1
                          FROM dbo.QualityEventAffectedResults qar
                          WHERE qar.QualityEventID = @qualityEventId
                            AND qar.SampleTestID = st.SampleTestID
                      )",
                    new[]
                    {
                        new SqlParameter("@qualityEventId", qualityEventId),
                        new SqlParameter("@sampleId", sampleId)
                    }, conn, tx);
            });

            string summary = GetOpenQualityEventSummary(sampleId);
            return string.IsNullOrWhiteSpace(summary) ? "Alert Quality Event created." : summary;
        }

        public static bool AreOosResultsResolvedForApproval(int sampleId, out string message)
        {
            message = "";

            if (!TableExists("QualityEvents") || !TableExists("QualityEventAffectedResults"))
            {
                message = "Quality Event compliance tables are unavailable. Approval and certificate issuance are blocked until System Preflight / Database Maintenance confirms the controlled schema.";
                return false;
            }

            string query = @"
                SELECT COUNT(1)
                FROM dbo.SampleTests st
                WHERE st.SampleID = @sampleId
                  AND UPPER(ISNULL(st.ResultStatus, '')) IN ('OOS', 'ACTION', 'FAIL')
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM dbo.QualityEventAffectedResults qar
                      INNER JOIN dbo.QualityEvents qe
                          ON qe.QualityEventID = qar.QualityEventID
                      WHERE qe.SampleID = @sampleId
                        AND qar.SampleTestID = st.SampleTestID
                        AND ISNULL(qe.CurrentStatus, '') = 'Closed'
                        AND ISNULL(qe.FinalDisposition, '') IN
                        (
                            'Accept with Justification',
                            'Release After Investigation',
                            'Released After Investigation',
                            'Retest Accepted',
                            'Resample Accepted'
                        )
                  )";

            object result = ExecuteScalar(query, new[] { new SqlParameter("@sampleId", sampleId) });
            int unresolvedCount = result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);

            if (unresolvedCount > 0)
            {
                message = "This sample contains OOS result(s) that are not covered by a closed Quality Event with an acceptable QA final disposition.";
                return false;
            }

            return true;
        }

        internal static void EnsureSampleApprovalQualityGatesInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (sampleId <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleId));

            using SqlCommand command = new SqlCommand(@"
IF OBJECT_ID(N'dbo.QualityEvents',N'U') IS NULL
   OR OBJECT_ID(N'dbo.QualityEventAffectedResults',N'U') IS NULL
BEGIN
    SELECT 3;
END
ELSE IF EXISTS
(
    SELECT 1
    FROM dbo.QualityEvents WITH (UPDLOCK, HOLDLOCK)
    WHERE SampleID=@SampleID
      AND ISNULL(CurrentStatus,N'') NOT IN (N'Closed',N'QA Closed',N'Cancelled',N'Rejected Closed')
)
BEGIN
    SELECT 1;
END
ELSE IF EXISTS
(
    SELECT 1
    FROM dbo.SampleTests st WITH (UPDLOCK, HOLDLOCK)
    WHERE st.SampleID=@SampleID
      AND UPPER(ISNULL(st.ResultStatus,N'')) IN (N'OOS',N'ACTION',N'FAIL')
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.QualityEventAffectedResults qar WITH (UPDLOCK, HOLDLOCK)
          INNER JOIN dbo.QualityEvents qe WITH (UPDLOCK, HOLDLOCK)
              ON qe.QualityEventID=qar.QualityEventID
          WHERE qe.SampleID=@SampleID
            AND qar.SampleTestID=st.SampleTestID
            AND ISNULL(qe.CurrentStatus,N'')=N'Closed'
            AND ISNULL(qe.FinalDisposition,N'') IN
            (
                N'Accept with Justification',
                N'Release After Investigation',
                N'Released After Investigation',
                N'Retest Accepted',
                N'Resample Accepted'
            )
      )
)
BEGIN
    SELECT 2;
END
ELSE
BEGIN
    SELECT 0;
END;", connection, transaction);

            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
            int gate = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);

            if (gate == 1)
                throw new InvalidOperationException("Approval is blocked because an open Quality Event exists for this water sample.");
            if (gate == 2)
                throw new InvalidOperationException("Approval is blocked because OOS, action-limit, or failed results are not covered by a closed Quality Event with an acceptable QA final disposition.");
            if (gate == 3)
                throw new InvalidOperationException("Quality Event compliance tables are unavailable. Approval was blocked fail-closed.");
        }

        public static bool CanApproveSampleRecord(int sampleId, out string message)
        {
            message = "";

            if (HasOpenQualityEvent(sampleId))
            {
                string summary = GetOpenQualityEventSummary(sampleId);
                message = "This sample has an open Quality Event and cannot be approved. " + summary;
                return false;
            }

            if (!AreOosResultsResolvedForApproval(sampleId, out message))
                return false;

            return true;
        }

        public static bool CanIssueCertificateForSample(int sampleId, out string message)
        {
            message = "";

            if (HasOpenQualityEvent(sampleId))
            {
                string summary = GetOpenQualityEventSummary(sampleId);
                message = "This sample has an open Quality Event and COA issuance is blocked. " + summary;
                return false;
            }

            if (!AreOosResultsResolvedForApproval(sampleId, out message))
            {
                message = message + " COA issuance is blocked until QA disposition is completed.";
                return false;
            }

            return true;
        }


        public static int GetOpenQualityEventId(int sampleId)
        {
            if (!TableExists("QualityEvents"))
                return 0;

            string query = @"
                SELECT TOP 1 QualityEventID
                FROM dbo.QualityEvents
                WHERE SampleID = @sampleId
                  AND CurrentStatus NOT IN ('Closed', 'QA Closed', 'Cancelled', 'Rejected Closed')
                ORDER BY QualityEventID DESC";

            object result = ExecuteScalar(query, new[] { new SqlParameter("@sampleId", sampleId) });
            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        public static int GetLatestQualityEventId(int sampleId)
        {
            if (!TableExists("QualityEvents"))
                return 0;

            string query = @"
                SELECT TOP 1 QualityEventID
                FROM dbo.QualityEvents
                WHERE SampleID = @sampleId
                ORDER BY QualityEventID DESC";

            object result = ExecuteScalar(query, new[] { new SqlParameter("@sampleId", sampleId) });
            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        public static bool HasAnyQualityEvent(int sampleId)
        {
            if (!TableExists("QualityEvents"))
                throw new InvalidOperationException("Quality Event compliance table is unavailable. Controlled workflow verification cannot continue.");

            string query = @"
                SELECT COUNT(*)
                FROM dbo.QualityEvents
                WHERE SampleID = @sampleId";

            object result = ExecuteScalar(query, new[] { new SqlParameter("@sampleId", sampleId) });
            return result != null && result != DBNull.Value && Convert.ToInt32(result) > 0;
        }

        public static void AddQualityEventPrintHistory(int qualityEventId, string printedBy)
        {
            if (!TableExists("QualityEventPrintHistory"))
                return;

            string query = @"
                INSERT INTO dbo.QualityEventPrintHistory
                (
                    QualityEventID,
                    PrintedBy,
                    PrintedDate
                )
                VALUES
                (
                    @qualityEventId,
                    @printedBy,
                    GETDATE()
                )";

            ExecuteNonQuery(query, new[]
            {
                new SqlParameter("@qualityEventId", qualityEventId),
                new SqlParameter("@printedBy", string.IsNullOrWhiteSpace(printedBy) ? (object)DBNull.Value : printedBy)
            });
        }

        public static DataTable GetQualityEventHeader(int qualityEventId)
        {
            string query = @"
                SELECT TOP 1
                    QualityEventID,
                    EventNumber,
                    EventType,
                    Severity,
                    SampleID,
                    SampleNumber,
                    CurrentStatus,
                    DetectedBy,
                    DetectedDate,
                    DetectionSource,
                    InitialDescription,
                    ImmediateAction,
                    RootCauseCategory,
                    RootCauseDetails,
                    ImpactAssessment,
                    CAPARequired,
                    QAConclusion,
                    FinalDisposition,
                    ClosedBy,
                    ClosedDate,
                    CreatedDate,
                    ModifiedBy,
                    ModifiedDate
                FROM dbo.QualityEvents
                WHERE QualityEventID = @qualityEventId";

            return ExecuteQuery(query, new[] { new SqlParameter("@qualityEventId", qualityEventId) });
        }

        public static DataTable GetQualityEventAffectedResults(int qualityEventId)
        {
            string query = @"
                SELECT
                    AffectedResultID,
                    QualityEventID,
                    SampleTestID,
                    TestID,
                    TestName,
                    ResultValue,
                    SpecificationLimit,
                    Unit,
                    FailureType,
                    CreatedDate
                FROM dbo.QualityEventAffectedResults
                WHERE QualityEventID = @qualityEventId
                ORDER BY AffectedResultID";

            return ExecuteQuery(query, new[] { new SqlParameter("@qualityEventId", qualityEventId) });
        }

        public static DataTable GetQualityEventActions(int qualityEventId)
        {
            string query = @"
                SELECT
                    QualityEventActionID,
                    QualityEventID,
                    ActionType,
                    ActionDescription,
                    PerformedBy,
                    PerformedDate,
                    ElectronicSignatureID
                FROM dbo.QualityEventActions
                WHERE QualityEventID = @qualityEventId
                ORDER BY QualityEventActionID";

            return ExecuteQuery(query, new[] { new SqlParameter("@qualityEventId", qualityEventId) });
        }

        public static void SaveQualityEventInvestigation(
            int qualityEventId,
            string severity,
            string initialDescription,
            string immediateAction,
            string rootCauseCategory,
            string rootCauseDetails,
            string impactAssessment,
            bool capaRequired,
            string qaConclusion,
            string finalDisposition,
            string currentStatus,
            string modifiedBy)
        {
            string query = @"
                UPDATE dbo.QualityEvents
                SET Severity = @severity,
                    InitialDescription = @initialDescription,
                    ImmediateAction = @immediateAction,
                    RootCauseCategory = @rootCauseCategory,
                    RootCauseDetails = @rootCauseDetails,
                    ImpactAssessment = @impactAssessment,
                    CAPARequired = @capaRequired,
                    QAConclusion = @qaConclusion,
                    FinalDisposition = @finalDisposition,
                    CurrentStatus = @currentStatus,
                    ModifiedBy = @modifiedBy,
                    ModifiedDate = GETDATE()
                WHERE QualityEventID = @qualityEventId";

            ExecuteNonQuery(query, new[]
            {
                new SqlParameter("@qualityEventId", qualityEventId),
                new SqlParameter("@severity", string.IsNullOrWhiteSpace(severity) ? "Major" : severity),
                new SqlParameter("@initialDescription", string.IsNullOrWhiteSpace(initialDescription) ? (object)DBNull.Value : initialDescription),
                new SqlParameter("@immediateAction", string.IsNullOrWhiteSpace(immediateAction) ? (object)DBNull.Value : immediateAction),
                new SqlParameter("@rootCauseCategory", string.IsNullOrWhiteSpace(rootCauseCategory) ? (object)DBNull.Value : rootCauseCategory),
                new SqlParameter("@rootCauseDetails", string.IsNullOrWhiteSpace(rootCauseDetails) ? (object)DBNull.Value : rootCauseDetails),
                new SqlParameter("@impactAssessment", string.IsNullOrWhiteSpace(impactAssessment) ? (object)DBNull.Value : impactAssessment),
                new SqlParameter("@capaRequired", capaRequired),
                new SqlParameter("@qaConclusion", string.IsNullOrWhiteSpace(qaConclusion) ? (object)DBNull.Value : qaConclusion),
                new SqlParameter("@finalDisposition", string.IsNullOrWhiteSpace(finalDisposition) ? (object)DBNull.Value : finalDisposition),
                new SqlParameter("@currentStatus", string.IsNullOrWhiteSpace(currentStatus) ? "Open" : currentStatus),
                new SqlParameter("@modifiedBy", string.IsNullOrWhiteSpace(modifiedBy) ? (object)DBNull.Value : modifiedBy)
            });
        }

        public static void SaveQualityEventInvestigation(
            SqlConnection connection,
            SqlTransaction transaction,
            int qualityEventId,
            string severity,
            string initialDescription,
            string immediateAction,
            string rootCauseCategory,
            string rootCauseDetails,
            string impactAssessment,
            bool capaRequired,
            string qaConclusion,
            string finalDisposition,
            string currentStatus,
            string modifiedBy)
        {
            int affected = ExecuteNonQueryWithTransaction(@"
UPDATE dbo.QualityEvents
SET Severity = @severity,
    InitialDescription = @initialDescription,
    ImmediateAction = @immediateAction,
    RootCauseCategory = @rootCauseCategory,
    RootCauseDetails = @rootCauseDetails,
    ImpactAssessment = @impactAssessment,
    CAPARequired = @capaRequired,
    QAConclusion = @qaConclusion,
    FinalDisposition = @finalDisposition,
    CurrentStatus = @currentStatus,
    ModifiedBy = @modifiedBy,
    ModifiedDate = GETDATE()
WHERE QualityEventID = @qualityEventId
  AND ISNULL(CurrentStatus,N'Open') NOT IN(N'Closed',N'QA Closed',N'Cancelled',N'Rejected Closed');",
                new[]
                {
                    new SqlParameter("@qualityEventId", qualityEventId),
                    new SqlParameter("@severity", string.IsNullOrWhiteSpace(severity) ? "Major" : severity),
                    new SqlParameter("@initialDescription", string.IsNullOrWhiteSpace(initialDescription) ? (object)DBNull.Value : initialDescription),
                    new SqlParameter("@immediateAction", string.IsNullOrWhiteSpace(immediateAction) ? (object)DBNull.Value : immediateAction),
                    new SqlParameter("@rootCauseCategory", string.IsNullOrWhiteSpace(rootCauseCategory) ? (object)DBNull.Value : rootCauseCategory),
                    new SqlParameter("@rootCauseDetails", string.IsNullOrWhiteSpace(rootCauseDetails) ? (object)DBNull.Value : rootCauseDetails),
                    new SqlParameter("@impactAssessment", string.IsNullOrWhiteSpace(impactAssessment) ? (object)DBNull.Value : impactAssessment),
                    new SqlParameter("@capaRequired", capaRequired),
                    new SqlParameter("@qaConclusion", string.IsNullOrWhiteSpace(qaConclusion) ? (object)DBNull.Value : qaConclusion),
                    new SqlParameter("@finalDisposition", string.IsNullOrWhiteSpace(finalDisposition) ? (object)DBNull.Value : finalDisposition),
                    new SqlParameter("@currentStatus", string.IsNullOrWhiteSpace(currentStatus) ? "Open" : currentStatus),
                    new SqlParameter("@modifiedBy", string.IsNullOrWhiteSpace(modifiedBy) ? (object)DBNull.Value : modifiedBy)
                }, connection, transaction);

            if (affected != 1)
                throw new DBConcurrencyException("The Quality Event was changed or removed by another user. Reload and retry.");
        }

        public static void AddQualityEventAction(int qualityEventId, string actionType, string actionDescription, string performedBy, int? electronicSignatureId = null)
        {
            string effectivePerformedBy = ResolveAuthenticatedSigner(performedBy);

            string query = @"
                INSERT INTO dbo.QualityEventActions
                (
                    QualityEventID,
                    ActionType,
                    ActionDescription,
                    PerformedBy,
                    PerformedDate,
                    ElectronicSignatureID
                )
                VALUES
                (
                    @qualityEventId,
                    @actionType,
                    @actionDescription,
                    @performedBy,
                    GETDATE(),
                    @electronicSignatureId
                )";

            ExecuteNonQuery(query, new[]
            {
                new SqlParameter("@qualityEventId", qualityEventId),
                new SqlParameter("@actionType", string.IsNullOrWhiteSpace(actionType) ? "Investigation Update" : actionType),
                new SqlParameter("@actionDescription", string.IsNullOrWhiteSpace(actionDescription) ? (object)DBNull.Value : actionDescription),
                new SqlParameter("@performedBy", effectivePerformedBy),
                new SqlParameter("@electronicSignatureId", electronicSignatureId.HasValue ? (object)electronicSignatureId.Value : DBNull.Value)
            });
        }

        public static void SubmitQualityEventToQA(int qualityEventId, string submittedBy)
        {
            ExecuteInTransaction((conn, tx) =>
            {
                EnsureQualityEventManagementAuthorizationInTransaction(
                    conn, tx, submittedBy, "submit a Quality Event to QA review");

                int affected = ExecuteNonQueryWithTransaction(@"
                    UPDATE dbo.QualityEvents
                    SET CurrentStatus = 'QA Review',
                        ModifiedBy = @submittedBy,
                        ModifiedDate = GETDATE()
                    WHERE QualityEventID = @qualityEventId
                      AND CurrentStatus NOT IN ('QA Review','Closed','QA Closed','Cancelled','Rejected Closed')",
                    new[]
                    {
                        new SqlParameter("@qualityEventId", qualityEventId),
                        new SqlParameter("@submittedBy", string.IsNullOrWhiteSpace(submittedBy) ? (object)DBNull.Value : submittedBy)
                    }, conn, tx);
                if (affected != 1)
                    throw new DBConcurrencyException("The Quality Event is already in QA Review or was changed by another user.");

                ExecuteNonQueryWithTransaction(@"
                    INSERT INTO dbo.QualityEventActions
                    (
                        QualityEventID,
                        ActionType,
                        ActionDescription,
                        PerformedBy,
                        PerformedDate
                    )
                    VALUES
                    (
                        @qualityEventId,
                        'Submit to QA',
                        'Investigation submitted to QA review.',
                        @submittedBy,
                        GETDATE()
                    )",
                    new[]
                    {
                        new SqlParameter("@qualityEventId", qualityEventId),
                        new SqlParameter("@submittedBy", string.IsNullOrWhiteSpace(submittedBy) ? (object)DBNull.Value : submittedBy)
                    }, conn, tx);

                AddAuditTrailAdvanced(
                    conn,
                    tx,
                    "QualityEvents",
                    qualityEventId,
                    "Submit Quality Event to QA",
                    "Open",
                    "QA Review",
                    "Investigation submitted to QA review",
                    submittedBy,
                    "CurrentStatus",
                    null,
                    null,
                    "Quality Event");
            });
        }

        public static void CloseQualityEvent(
            int qualityEventId,
            int sampleId,
            string qaConclusion,
            string finalDisposition,
            string closedBy,
            string signatureMeaning,
            string signatureReason,
            string userRole)
        {
            ExecuteInTransaction((conn, tx) =>
            {
                string authorizedRole = EnsureQaClosureAuthorizationInTransaction(
                    conn, tx, closedBy, "close a Quality Event");
                userRole = authorizedRole;

                int? linkedSampleId = null;
                int? linkedEmEventId = null;
                string sourceRecordNumber = "";
                string eventNumber = "";
                string detectionSource = "";

                using (SqlCommand contextCommand = new SqlCommand(@"
                    SELECT TOP 1
                        QualityEventID,
                        SampleID,
                        SampleNumber,
                        EventNumber,
                        DetectionSource
                    FROM dbo.QualityEvents WITH (UPDLOCK, HOLDLOCK)
                    WHERE QualityEventID = @qualityEventId;", conn, tx))
                {
                    contextCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    contextCommand.Parameters.Add("@qualityEventId", SqlDbType.Int).Value = qualityEventId;

                    using (SqlDataReader reader = contextCommand.ExecuteReader())
                    {
                        if (!reader.Read())
                            throw new InvalidOperationException("The Quality Event record was not found.");

                        if (reader["SampleID"] != DBNull.Value)
                            linkedSampleId = Convert.ToInt32(reader["SampleID"], CultureInfo.InvariantCulture);

                        sourceRecordNumber = reader["SampleNumber"] == DBNull.Value
                            ? ""
                            : reader["SampleNumber"].ToString()?.Trim() ?? "";
                        eventNumber = reader["EventNumber"] == DBNull.Value
                            ? ""
                            : reader["EventNumber"].ToString()?.Trim() ?? "";
                        detectionSource = reader["DetectionSource"] == DBNull.Value
                            ? ""
                            : reader["DetectionSource"].ToString()?.Trim() ?? "";
                    }
                }

                if (linkedSampleId.HasValue && linkedSampleId.Value > 0)
                {
                    using (SqlCommand validateSampleCommand = new SqlCommand(@"
                        SELECT COUNT(1)
                        FROM dbo.Samples
                        WHERE SampleID = @sampleId;", conn, tx))
                    {
                        validateSampleCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        validateSampleCommand.Parameters.Add("@sampleId", SqlDbType.Int).Value = linkedSampleId.Value;
                        int sampleCount = Convert.ToInt32(validateSampleCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                        if (sampleCount != 1)
                        {
                            throw new InvalidOperationException(
                                "The Quality Event references a laboratory sample that no longer exists. " +
                                "Closure was blocked to preserve referential integrity.");
                        }
                    }
                }
                else if (detectionSource.Equals("Environmental Monitoring", StringComparison.OrdinalIgnoreCase) ||
                         sourceRecordNumber.StartsWith("EM-", StringComparison.OrdinalIgnoreCase))
                {
                    using (SqlCommand resolveEmCommand = new SqlCommand(@"
                        IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NULL
                            THROW 51330, 'Required table dbo.EM_Events is missing.', 1;

                        SELECT TOP 1 Id
                        FROM dbo.EM_Events
                        WHERE EventNo = @eventNo
                        ORDER BY Id DESC;", conn, tx))
                    {
                        resolveEmCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        resolveEmCommand.Parameters.Add("@eventNo", SqlDbType.NVarChar, 50).Value = sourceRecordNumber;
                        object emId = resolveEmCommand.ExecuteScalar();
                        if (emId == null || emId == DBNull.Value)
                        {
                            throw new InvalidOperationException(
                                "The Environmental Monitoring event linked to this Quality Event could not be found. " +
                                "Closure was blocked to avoid writing a signature against the wrong record.");
                        }

                        linkedEmEventId = Convert.ToInt32(emId, CultureInfo.InvariantCulture);
                    }
                }
                else if (sampleId > 0)
                {
                    // Compatibility fallback for legacy Water events opened by SampleID before
                    // the QualityEvents.SampleID column was populated consistently.
                    using (SqlCommand validateLegacySampleCommand = new SqlCommand(@"
                        SELECT COUNT(1)
                        FROM dbo.Samples
                        WHERE SampleID = @sampleId;", conn, tx))
                    {
                        validateLegacySampleCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        validateLegacySampleCommand.Parameters.Add("@sampleId", SqlDbType.Int).Value = sampleId;
                        int sampleCount = Convert.ToInt32(validateLegacySampleCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                        if (sampleCount == 1)
                            linkedSampleId = sampleId;
                    }
                }

                if (!linkedSampleId.HasValue && !linkedEmEventId.HasValue)
                {
                    throw new InvalidOperationException(
                        "The Quality Event source record could not be resolved to either dbo.Samples or dbo.EM_Events. " +
                        "Closure was blocked to preserve electronic-signature traceability.");
                }

                int affectedQualityEvents = ExecuteNonQueryWithTransaction(@"
                    UPDATE dbo.QualityEvents
                    SET CurrentStatus = 'Closed',
                        QAConclusion = @qaConclusion,
                        FinalDisposition = @finalDisposition,
                        ClosedBy = @closedBy,
                        ClosedDate = GETDATE(),
                        ModifiedBy = @closedBy,
                        ModifiedDate = GETDATE()
                    WHERE QualityEventID = @qualityEventId
                      AND CurrentStatus = 'QA Review'",
                    new[]
                    {
                        new SqlParameter("@qualityEventId", qualityEventId),
                        new SqlParameter("@qaConclusion", string.IsNullOrWhiteSpace(qaConclusion) ? (object)DBNull.Value : qaConclusion),
                        new SqlParameter("@finalDisposition", string.IsNullOrWhiteSpace(finalDisposition) ? (object)DBNull.Value : finalDisposition),
                        new SqlParameter("@closedBy", string.IsNullOrWhiteSpace(closedBy) ? (object)DBNull.Value : closedBy)
                    }, conn, tx);
                if (affectedQualityEvents != 1)
                    throw new DBConcurrencyException("The Quality Event was already changed or closed. Reload and retry.");

                ExecuteNonQueryWithTransaction(@"
                    INSERT INTO dbo.QualityEventActions
                    (
                        QualityEventID,
                        ActionType,
                        ActionDescription,
                        PerformedBy,
                        PerformedDate
                    )
                    VALUES
                    (
                        @qualityEventId,
                        'QA Closure',
                        'Quality Event closed by QA. Final disposition: ' + ISNULL(@finalDisposition, ''),
                        @closedBy,
                        GETDATE()
                    )",
                    new[]
                    {
                        new SqlParameter("@qualityEventId", qualityEventId),
                        new SqlParameter("@finalDisposition", string.IsNullOrWhiteSpace(finalDisposition) ? (object)DBNull.Value : finalDisposition),
                        new SqlParameter("@closedBy", string.IsNullOrWhiteSpace(closedBy) ? (object)DBNull.Value : closedBy)
                    }, conn, tx);

                if (linkedSampleId.HasValue)
                {
                    ExecuteNonQueryWithTransaction(@"
                        DECLARE @Ids TABLE(SignatureID INT NOT NULL);
                        INSERT dbo.ElectronicSignatures(SampleID,ActionType,ActionReason,SignedBy,SignedAt,MeaningOfSignature,UserRole)
                        OUTPUT INSERTED.SignatureID INTO @Ids(SignatureID)
                        VALUES(@sampleId,'Quality Event Closure',@signatureReason,@closedBy,GETDATE(),@signatureMeaning,@userRole);
                        INSERT dbo.QualityEventSignatures(QualityEventID,SignatureID,SignedBy,UserRole,MeaningOfSignature,SignedAt)
                        SELECT @qualityEventId,SignatureID,@closedBy,@userRole,@signatureMeaning,GETDATE() FROM @Ids;",
                        new[]
                        {
                            new SqlParameter("@qualityEventId", qualityEventId),
                            new SqlParameter("@sampleId", linkedSampleId.Value),
                            new SqlParameter("@signatureReason", string.IsNullOrWhiteSpace(signatureReason) ? (object)DBNull.Value : signatureReason),
                            new SqlParameter("@closedBy", closedBy),
                            new SqlParameter("@signatureMeaning", signatureMeaning),
                            new SqlParameter("@userRole", userRole)
                        }, conn, tx);
                }
                else
                {
                    string emSignatureReason = string.IsNullOrWhiteSpace(signatureReason)
                        ? "Quality Event " + eventNumber + " closure"
                        : "Quality Event " + eventNumber + " closure: " + signatureReason.Trim();

                    ExecuteNonQueryWithTransaction(@"
                        IF OBJECT_ID(N'dbo.EM_EventSignatures', N'U') IS NULL
                            THROW 51331, 'Required table dbo.EM_EventSignatures is missing.', 1;

                        INSERT INTO dbo.EM_EventSignatures
                        (
                            EventID,
                            EventNo,
                            ActionType,
                            ActionReason,
                            SignedBy,
                            UserRole,
                            MeaningOfSignature,
                            SignedAt
                        )
                        VALUES
                        (
                            @eventId,
                            @eventNo,
                            'Quality Event Closure',
                            @signatureReason,
                            @closedBy,
                            @userRole,
                            @signatureMeaning,
                            SYSDATETIME()
                        );",
                        new[]
                        {
                            new SqlParameter("@eventId", linkedEmEventId.Value),
                            new SqlParameter("@eventNo", string.IsNullOrWhiteSpace(sourceRecordNumber) ? (object)DBNull.Value : sourceRecordNumber),
                            new SqlParameter("@signatureReason", emSignatureReason),
                            new SqlParameter("@closedBy", closedBy),
                            new SqlParameter("@userRole", userRole),
                            new SqlParameter("@signatureMeaning", signatureMeaning)
                        }, conn, tx);
                }

                AddAuditTrailAdvanced(
                    conn,
                    tx,
                    "QualityEvents",
                    qualityEventId,
                    "Close Quality Event",
                    "QA Review",
                    "Closed",
                    signatureReason,
                    closedBy,
                    "CurrentStatus",
                    null,
                    null,
                    linkedEmEventId.HasValue ? "Quality Event / Environmental Monitoring" : "Quality Event");

                if (linkedSampleId.HasValue)
                {
                    string releaseStatus = "Released After Investigation";
                    if (string.Equals(finalDisposition, "Reject", StringComparison.OrdinalIgnoreCase))
                        releaseStatus = "Rejected";

                    ExecuteNonQueryWithTransaction(@"
                        UPDATE dbo.Samples
                        SET Status = @releaseStatus,
                            ModifiedBy = @closedBy,
                            ModifiedDate = GETDATE()
                        WHERE SampleID = @sampleId",
                        new[]
                        {
                            new SqlParameter("@sampleId", linkedSampleId.Value),
                            new SqlParameter("@releaseStatus", releaseStatus),
                            new SqlParameter("@closedBy", string.IsNullOrWhiteSpace(closedBy) ? (object)DBNull.Value : closedBy)
                        }, conn, tx);
                }
            });
        }

        public static void EnsureEnvironmentalMonitoringChecklistQuestions()
        {
            if (!TableExists("QualityEventChecklistQuestions"))
                return;

            EnsureQualityEventChecklistV12Columns();

            var questions = new (string Section, string Question, string Keyword, int SortOrder)[]
            {
                // Phase I laboratory / sampling review. These controls are intentionally explicit so
                // the report cannot claim that laboratory error was excluded when the underlying
                // review areas were never documented.
                ("Phase I - Laboratory Review", "Was analyst training and authorization verified for the EM sampling/testing activity?", "All", 7900),
                ("Phase I - Laboratory Review", "Was an analyst/sampler interview documented and did it identify no relevant abnormality or error?", "All", 7910),
                ("Phase I - Laboratory Review", "Were calculation, transcription, and result-entry checks independently reviewed with no attributable error?", "All", 7920),
                ("Phase I - Laboratory Review", "Was the current approved method/SOP verified and followed for sampling, incubation, reading, and calculation?", "All", 7930),
                ("Phase I - Laboratory Review", "Were acceptance criteria, calculations, and alert/action limit interpretations independently verified?", "All", 7940),
                ("Phase I - Laboratory Review", "Were sample/plate identity and chain of custody fully traceable from collection through reading?", "All", 7950),
                ("Phase I - Laboratory Review", "Were sampling, transport, holding time, and handling practices reviewed with no attributable error?", "All", 7960),
                ("Phase I - Laboratory Review", "Were raw data and audit trail reviewed with no unexplained deletion, modification, repetition, or backdating?", "All", 7970),
                ("Phase I - Laboratory Review", "Were applicable equipment/instrument status, calibration, incubator status, and controls reviewed and acceptable?", "All", 7980),

                ("EM Sampling", "Was the EM sampling location/point correct according to the approved EM plan?", "All", 8010),
                ("EM Sampling", "Was sampling performed during the documented activity condition?", "All", 8020),
                ("EM Sampling", "Were sampling date, time, duration or sampled volume, and point/plate identity fully traceable?", "All", 8030),
                ("EM Media", "Was media lot number recorded, within expiry, and released for use?", "All", 8040),
                ("EM Media", "Was Growth Promotion Test status acceptable for the used media lot?", "All", 8050),
                ("EM Traceability", "Were plate/sample labels, event number, area, grade, and monitoring method traceable without discrepancy?", "All", 8060),
                ("EM Handling", "Were transport, holding time, and handling conditions acceptable before incubation or reading?", "All", 8070),
                ("EM Incubation", "Were incubation time and temperature within the approved procedure?", "All", 8080),
                ("EM Controls", "Were negative controls and other applicable controls acceptable?", "All", 8090),
                ("EM Result Review", "Was colony count/result interpretation independently verified against approved alert/action limits?", "All", 8100),
                ("EM Identification", "For an action-level excursion or significant recovery, was organism identification completed or formally initiated as required by procedure?", "All", 8110),
                ("EM Identification", "Was the identified organism or morphology reviewed against historical flora, objectionable-organism risk, and likely contamination source?", "All", 8120),
                ("Cleaning / Disinfection", "Were cleaning and disinfection records for the relevant area and period reviewed and found acceptable?", "All", 8130),
                ("Cleaning / Disinfection", "Were disinfectant preparation/concentration, rotation, contact time, and sanitization execution reviewed for the relevant period?", "All", 8140),
                ("HVAC / Facility", "Were relevant pressure differentials, temperature/humidity, HVAC alarms or excursions, and facility conditions reviewed?", "All", 8150),
                ("Area / Personnel", "Were doors, personnel movement, cleaning/sanitization, and area activity normal during sampling?", "All", 8160),
                ("Personnel", "Where personnel could contribute, were training, gowning qualification, aseptic behavior, and personnel monitoring history reviewed?", "All", 8170),
                ("Trend Review", "Were adjacent/related monitoring points and previous/subsequent EM results reviewed for the same area and grade?", "All", 8180),
                ("Trend Review", "Was recurrence or an adverse microbiological trend assessed using the available historical EM data?", "All", 8190),
                ("Impact Assessment", "Was potential impact on exposed product, material, batch, process, and area state of control assessed and documented?", "All", 8200),
                ("Related Records", "Were related deviations, maintenance, cleaning events, alarms, previous Quality Events, and CAPA records reviewed?", "All", 8210),
                ("Follow-up Monitoring", "Was the follow-up monitoring/resampling strategy scientifically justified, with locations, timing, acceptance criteria, and interpretation defined?", "All", 8220),
                ("CAPA", "Was the CAPA requirement scientifically justified based on root cause, recurrence risk, and impact assessment?", "All", 8230),
                ("QA Disposition", "Was QA disposition documented before final approval/report issuance?", "All", 8240),
                ("QA Disposition", "Was final QA disposition scientifically justified with the area status, product/material impact, and follow-up requirements clearly documented?", "All", 8245),
                ("Settle Plate", "Was settle plate exposure time within the approved procedure?", "settle", 8250),
                ("Settle Plate", "Was plate handling performed aseptically and protected from accidental contamination?", "settle", 8260),
                ("Active Air Sampling", "Was air sampler ID recorded and calibration status valid at the time of sampling?", "active air", 8270),
                ("Active Air Sampling", "Was the sampled air volume correct and used correctly in CFU/m3 calculation?", "active air", 8280),
                ("Contact Plate", "Were contact-plate site, contact area/time, pressure, and neutralizer suitability appropriate for the sampled surface?", "contact plate", 8290),
                ("Surface Swab", "Were swab location/area, swab kit and diluent lots, recovery volume, and recovery technique documented and acceptable?", "surface swab", 8300),
                ("Personnel Monitoring", "Were personnel identity, gowning stage, sampled location, sampling time, and relation to the activity fully documented?", "personnel", 8310),

                // Explicit fishbone coverage. Comments/Evidence records the basis for concluding
                // whether each causal family contributed or was reasonably excluded.
                ("Root Cause / Fishbone", "Was Personnel / Training assessed as a potential cause, with evidence documented?", "All", 8320),
                ("Root Cause / Fishbone", "Was Method / Procedure assessed as a potential cause, with evidence documented?", "All", 8330),
                ("Root Cause / Fishbone", "Was Equipment / Instrument assessed as a potential cause, with evidence documented?", "All", 8340),
                ("Root Cause / Fishbone", "Was Material / Media assessed as a potential cause, with evidence documented?", "All", 8350),
                ("Root Cause / Fishbone", "Was Environment / Facility assessed as a potential cause, with evidence documented?", "All", 8360),
                ("Root Cause / Fishbone", "Was Measurement / Data assessed as a potential cause, with evidence documented?", "All", 8370)
            };

            // Do not use a simple row-count shortcut here. Historical deployments can
            // contain the expected number of EM questions while still missing one or more
            // controlled sections (for example EM Media). Synchronize every controlled
            // definition by exact question text so existing QuestionID values and answers
            // are preserved while missing questions are inserted.
            foreach (var question in questions)
            {
                ExecuteNonQuery(@"
UPDATE dbo.QualityEventChecklistQuestions
SET SectionName = @SectionName,
    AppliesToEventType = N'All',
    AppliesToSampleType = N'All',
    AppliesToTestCategory = N'Environmental Monitoring',
    AppliesToTestNameKeyword = @Keyword,
    AnswerType = N'YesNoNA',
    IsRequired = 1,
    SortOrder = @SortOrder,
    IsActive = 1,
    ExpectedAnswer = N'Yes',
    QuestionLogic = N'PositiveCheck'
WHERE QuestionText = @QuestionText
   OR (ISNULL(AppliesToTestCategory, N'') = N'Environmental Monitoring' AND SortOrder = @SortOrder);

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO dbo.QualityEventChecklistQuestions
        (SectionName, QuestionText, AppliesToEventType, AppliesToSampleType,
         AppliesToTestCategory, AppliesToTestNameKeyword, AnswerType, IsRequired,
         SortOrder, IsActive, ExpectedAnswer, QuestionLogic)
    VALUES
        (@SectionName, @QuestionText, N'All', N'All', N'Environmental Monitoring',
         @Keyword, N'YesNoNA', 1, @SortOrder, 1, N'Yes', N'PositiveCheck');
END;",
                    new[]
                    {
                        new SqlParameter("@SectionName", SqlDbType.NVarChar, 200) { Value = question.Section },
                        new SqlParameter("@QuestionText", SqlDbType.NVarChar, -1) { Value = question.Question },
                        new SqlParameter("@Keyword", SqlDbType.NVarChar, 200) { Value = question.Keyword },
                        new SqlParameter("@SortOrder", SqlDbType.Int) { Value = question.SortOrder }
                    });
            }
        }

        public static DataTable GetQualityEventChecklist(int qualityEventId)
        {
            DataTable empty = new DataTable();
            empty.Columns.Add("QuestionID", typeof(int));
            empty.Columns.Add("SectionName", typeof(string));
            empty.Columns.Add("QuestionText", typeof(string));
            empty.Columns.Add("InvestigationProfile", typeof(string));
            empty.Columns.Add("QuestionLogic", typeof(string));
            empty.Columns.Add("ExpectedAnswer", typeof(string));
            empty.Columns.Add("AnswerType", typeof(string));
            empty.Columns.Add("IsRequired", typeof(bool));
            empty.Columns.Add("SortOrder", typeof(int));
            empty.Columns.Add("AnswerValue", typeof(string));
            empty.Columns.Add("Comments", typeof(string));

            if (!TableExists("QualityEventChecklistQuestions"))
                return empty;

            EnsureQualityEventChecklistV12Columns();

            string query = @"
                ;WITH EventContext AS
                (
                    SELECT
                        qe.QualityEventID,
                        qe.EventType,
                        ISNULL(qe.SourceModule,'') AS SourceModule,
                        qe.SampleNumber,
                        qe.DetectionSource,
                        COALESCE(NULLIF(prm.SampleCategory,''),s.SampleType,'') AS SampleType,
                        ISNULL(s.SamplingMethod, '') AS SamplingMethod,
                        ISNULL((
                            SELECT TOP 1 qar.TestName
                            FROM dbo.QualityEventAffectedResults qar
                            WHERE qar.QualityEventID = qe.QualityEventID
                            ORDER BY qar.AffectedResultID
                        ), '') AS TestName,
                        ISNULL((
                            SELECT TOP 1 t.TestCategory
                            FROM dbo.QualityEventAffectedResults qar
                            LEFT JOIN dbo.Tests t ON t.TestID = qar.TestID
                            WHERE qar.QualityEventID = qe.QualityEventID
                            ORDER BY qar.AffectedResultID
                        ), '') AS TestCategory
                    FROM dbo.QualityEvents qe
                    LEFT JOIN dbo.Samples s
                        ON s.SampleID = qe.SampleID
                    LEFT JOIN dbo.PRM_Samples prm
                        ON qe.SourceModule=N'PRM' AND prm.SampleID=qe.SourceRecordID
                    WHERE qe.QualityEventID = @qualityEventId
                ),
                ProfiledContext AS
                (
                    SELECT
                        *,
                        CASE
                            WHEN TestName LIKE '%conductivity%' THEN 'Conductivity'
                            WHEN TestName LIKE '%pH%' THEN 'pH'
                            WHEN TestName LIKE '%active air%' OR TestName LIKE '%air sampler%' OR TestName LIKE '%air sampling%' OR SamplingMethod LIKE '%active air%' OR DetectionSource LIKE '%active air%' THEN 'active air'
                            WHEN TestName LIKE '%settle%' OR SamplingMethod LIKE '%settle%' OR DetectionSource LIKE '%settle%' THEN 'settle'
                            WHEN TestName LIKE '%contact plate%' OR SamplingMethod LIKE '%contact plate%' OR DetectionSource LIKE '%contact plate%' THEN 'contact plate'
                            WHEN TestName LIKE '%surface swab%' OR TestName LIKE '%swab%' OR SamplingMethod LIKE '%surface swab%' OR SamplingMethod LIKE '%swab%' OR DetectionSource LIKE '%swab%' THEN 'surface swab'
                            WHEN TestName LIKE '%personnel%' OR TestName LIKE '%glove%' OR SamplingMethod LIKE '%personnel%' OR DetectionSource LIKE '%personnel%' THEN 'personnel'
                            ELSE ''
                        END AS SpecificKeyword,

                        CASE
                            WHEN SourceModule=N'PRM' THEN 'PRM Microbiology'
                            WHEN TestName LIKE '%E. coli%' OR TestName LIKE '%coli%' OR TestName LIKE '%salmonella%' OR TestName LIKE '%pseudomonas%' OR TestName LIKE '%aeruginosa%' OR TestName LIKE '%staphylococcus%' OR TestName LIKE '%aureus%' OR TestName LIKE '%candida%' OR TestName LIKE '%bile%' OR TestName LIKE '%organism%' OR TestName LIKE '%clostridia%' THEN 'Specified Organisms'
                            WHEN TestName LIKE '%TAMC%' OR TestName LIKE '%TYMC%' OR TestName LIKE '%bioburden%' OR TestName LIKE '%count%' OR TestName LIKE '%CFU%' OR TestName LIKE '%microbial%' THEN 'Water Microbiological Count'
                            WHEN TestName LIKE '%conductivity%' OR TestName LIKE '%pH%' OR TestName LIKE '%TOC%' OR TestName LIKE '%oxidizable%' OR TestName LIKE '%hardness%' OR TestName LIKE '%chlorine%' OR TestName LIKE '%appearance%' OR TestName LIKE '%chloride%' OR TestName LIKE '%sulphate%' OR TestName LIKE '%sulfate%' OR TestName LIKE '%ammonia%' OR TestName LIKE '%nitrate%' OR TestName LIKE '%heavy metal%' OR TestName LIKE '%calcium%' OR TestName LIKE '%magnesium%' THEN 'Water Physical/Chemical'
                            WHEN SampleType LIKE '%environment%' OR SampleNumber LIKE 'EM-%' OR DetectionSource LIKE '%environment%' OR TestName LIKE '%settle%' OR TestName LIKE '%active air%' OR TestName LIKE '%swab%' OR TestName LIKE '%contact%' OR TestName LIKE '%personnel%' THEN 'Environmental Monitoring'
                            ELSE 'General Microbiology'
                        END AS InvestigationProfile
                    FROM EventContext
                )
                , RankedChecklist AS
                (
                    SELECT
                        q.QuestionID,
                        q.SectionName,
                        q.QuestionText,
                        pc.InvestigationProfile,
                        ISNULL(q.QuestionLogic, 'PositiveCheck') AS QuestionLogic,
                        ISNULL(q.ExpectedAnswer, 'Yes') AS ExpectedAnswer,
                        ISNULL(q.AnswerType, 'YesNoNA') AS AnswerType,
                        ISNULL(q.IsRequired, 1) AS IsRequired,
                        ISNULL(q.SortOrder, 1000) AS SortOrder,
                        ISNULL(a.AnswerValue, '') AS AnswerValue,
                        ISNULL(a.Comments, '') AS Comments,
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY
                                CASE
                                    WHEN pc.InvestigationProfile = 'Environmental Monitoring'
                                         AND ISNULL(q.SortOrder, 0) BETWEEN 7900 AND 8370
                                    THEN N'EM|' + CONVERT(nvarchar(20), q.SortOrder)
                                    ELSE LOWER(LTRIM(RTRIM(ISNULL(q.SectionName, N'')))) + N'|' +
                                         LOWER(LTRIM(RTRIM(ISNULL(q.QuestionText, N''))))
                                END
                            ORDER BY
                                CASE WHEN NULLIF(LTRIM(RTRIM(ISNULL(a.AnswerValue, N''))), N'') IS NOT NULL THEN 0 ELSE 1 END,
                                CASE WHEN a.AnsweredDate IS NULL THEN 1 ELSE 0 END,
                                a.AnsweredDate DESC,
                                q.QuestionID
                        ) AS DuplicateRank
                    FROM dbo.QualityEventChecklistQuestions q
                CROSS JOIN ProfiledContext pc
                LEFT JOIN dbo.QualityEventChecklistAnswers a
                    ON a.QualityEventID = pc.QualityEventID
                   AND a.QuestionID = q.QuestionID
                WHERE ISNULL(q.IsActive, 1) = 1
                  AND (q.AppliesToEventType IS NULL OR q.AppliesToEventType = '' OR q.AppliesToEventType = 'All' OR q.AppliesToEventType = pc.EventType)
                  AND (q.AppliesToSampleType IS NULL OR q.AppliesToSampleType = '' OR q.AppliesToSampleType = 'All' OR pc.SampleType LIKE '%' + q.AppliesToSampleType + '%')
                  AND
                  (
                      /* Conductivity and pH: fixed test-specific questions only.
                         Do not add All/All/All/All General questions.
                         Do not add Water Physical/Chemical / All questions. */
                      (
                          pc.InvestigationProfile = 'Water Physical/Chemical'
                          AND ISNULL(pc.SpecificKeyword, '') IN ('Conductivity', 'pH')
                          AND ISNULL(q.AppliesToTestCategory, '') = 'Water Physical/Chemical'
                          AND ISNULL(q.AppliesToTestNameKeyword, '') = pc.SpecificKeyword
                      )

                      OR

                      /* Other Physical/Chemical tests: fixed category questions only. */
                      (
                          pc.InvestigationProfile = 'Water Physical/Chemical'
                          AND ISNULL(pc.SpecificKeyword, '') = ''
                          AND ISNULL(q.AppliesToTestCategory, '') = 'Water Physical/Chemical'
                          AND (q.AppliesToTestNameKeyword IS NULL OR q.AppliesToTestNameKeyword = '' OR q.AppliesToTestNameKeyword = 'All')
                      )

                      OR

                      /* TAMC / TYMC / Micro Count: fixed Micro Count questions only. */
                      (
                          pc.InvestigationProfile = 'Water Microbiological Count'
                          AND ISNULL(q.AppliesToTestCategory, '') = 'Water Microbiological Count'
                          AND (q.AppliesToTestNameKeyword IS NULL OR q.AppliesToTestNameKeyword = '' OR q.AppliesToTestNameKeyword = 'All')
                      )

                      OR

                      /* Specified organisms: fixed Specified Organisms questions only. */
                      (
                          pc.InvestigationProfile = 'Specified Organisms'
                          AND ISNULL(q.AppliesToTestCategory, '') = 'Specified Organisms'
                          AND (q.AppliesToTestNameKeyword IS NULL OR q.AppliesToTestNameKeyword = '' OR q.AppliesToTestNameKeyword = 'All')
                      )

                      OR

                      /* EM investigation: always include the common EM controls, then add
                         method-specific questions when the affected method is known. */
                      (
                          pc.InvestigationProfile = 'Environmental Monitoring'
                          AND ISNULL(q.AppliesToTestCategory, '') = 'Environmental Monitoring'
                          AND
                          (
                              q.AppliesToTestNameKeyword IS NULL
                              OR q.AppliesToTestNameKeyword = ''
                              OR q.AppliesToTestNameKeyword = 'All'
                              OR ISNULL(q.AppliesToTestNameKeyword, '') = ISNULL(pc.SpecificKeyword, '')
                              OR
                              (
                                  q.AppliesToTestNameKeyword = 'active air'
                                  AND EXISTS
                                  (
                                      SELECT 1
                                      FROM dbo.QualityEventAffectedResults emr
                                      WHERE emr.QualityEventID = pc.QualityEventID
                                        AND (emr.TestName LIKE '%active air%' OR emr.TestName LIKE '%air sampling%')
                                  )
                              )
                              OR
                              (
                                  q.AppliesToTestNameKeyword = 'settle'
                                  AND EXISTS
                                  (
                                      SELECT 1
                                      FROM dbo.QualityEventAffectedResults emr
                                      WHERE emr.QualityEventID = pc.QualityEventID
                                        AND emr.TestName LIKE '%settle%'
                                  )
                              )
                              OR
                              (
                                  q.AppliesToTestNameKeyword = 'contact plate'
                                  AND EXISTS
                                  (
                                      SELECT 1
                                      FROM dbo.QualityEventAffectedResults emr
                                      WHERE emr.QualityEventID = pc.QualityEventID
                                        AND emr.TestName LIKE '%contact plate%'
                                  )
                              )
                              OR
                              (
                                  q.AppliesToTestNameKeyword = 'surface swab'
                                  AND EXISTS
                                  (
                                      SELECT 1
                                      FROM dbo.QualityEventAffectedResults emr
                                      WHERE emr.QualityEventID = pc.QualityEventID
                                        AND (emr.TestName LIKE '%surface swab%' OR emr.TestName LIKE '%swab%')
                                  )
                              )
                              OR
                              (
                                  q.AppliesToTestNameKeyword = 'personnel'
                                  AND EXISTS
                                  (
                                      SELECT 1
                                      FROM dbo.QualityEventAffectedResults emr
                                      WHERE emr.QualityEventID = pc.QualityEventID
                                        AND (emr.TestName LIKE '%personnel%' OR emr.TestName LIKE '%glove%')
                                  )
                              )
                          )
                      )

                      OR

                      /* PRM events use the controlled PRM checklist plus category-specific questions. */
                      (
                          pc.InvestigationProfile = 'PRM Microbiology'
                          AND ISNULL(q.AppliesToTestCategory,'') = 'PRM Microbiology'
                          AND (ISNULL(q.AppliesToSampleType,'') IN('','All') OR q.AppliesToSampleType=pc.SampleType)
                          AND ISNULL(q.AppliesToTestNameKeyword,'All') IN('','All')
                      )

                      OR

                      /* Fallback only for unknown events.
                         This does not apply to Conductivity, pH, Micro Count, Specified Organisms, Active Air, or Settle Plate. */
                      (
                          pc.InvestigationProfile = 'General Microbiology'
                          AND ISNULL(q.AppliesToTestCategory, '') = 'All'
                          AND ISNULL(q.AppliesToTestNameKeyword, '') = 'All'
                      )
                  )
                )
                SELECT
                    QuestionID,
                    SectionName,
                    QuestionText,
                    InvestigationProfile,
                    QuestionLogic,
                    ExpectedAnswer,
                    AnswerType,
                    IsRequired,
                    SortOrder,
                    AnswerValue,
                    Comments
                FROM RankedChecklist
                WHERE DuplicateRank = 1
                ORDER BY SortOrder, QuestionID";

            try
            {
                return ExecuteQuery(query, new[]
                {
                    new SqlParameter("@qualityEventId", qualityEventId)
                });
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error($"Unable to load quality-event checklist for QualityEventId={qualityEventId}.", ex);
                return empty;
            }
        }

        private static void EnsureQualityEventChecklistV12Columns()
        {
            if (!TableExists("QualityEventChecklistQuestions"))
                return;

            if (ColumnExists("QualityEventChecklistQuestions", "ExpectedAnswer") == false)
                throw new InvalidOperationException("Required column dbo.QualityEventChecklistQuestions.ExpectedAnswer is missing. Apply the controlled database migration.");

            if (ColumnExists("QualityEventChecklistQuestions", "QuestionLogic") == false)
                throw new InvalidOperationException("Required column dbo.QualityEventChecklistQuestions.QuestionLogic is missing. Apply the controlled database migration.");
        }

        public static void SaveQualityEventChecklistAnswers(int qualityEventId, DataTable checklistTable, string answeredBy)
        {
            if (qualityEventId <= 0 || checklistTable == null || !TableExists("QualityEventChecklistAnswers"))
                return;

            foreach (DataRow row in checklistTable.Rows)
            {
                if (row == null)
                    continue;

                if (row.RowState == DataRowState.Deleted)
                    continue;

                if (!row.Table.Columns.Contains("QuestionID"))
                    continue;

                if (row["QuestionID"] == null || row["QuestionID"] == DBNull.Value)
                    continue;

                int questionId;

                try
                {
                    questionId = Convert.ToInt32(row["QuestionID"]);
                }
                catch
                {
                    continue;
                }

                if (questionId <= 0)
                    continue;

                string answerValue = row.Table.Columns.Contains("AnswerValue") ? row.GetSafeString("AnswerValue") : "";
                string comments = row.Table.Columns.Contains("Comments") ? row.GetSafeString("Comments") : "";

                string query = @"
                    IF EXISTS (
                        SELECT 1
                        FROM dbo.QualityEventChecklistAnswers
                        WHERE QualityEventID = @qualityEventId
                          AND QuestionID = @questionId
                    )
                    BEGIN
                        UPDATE dbo.QualityEventChecklistAnswers
                        SET AnswerValue = @answerValue,
                            Comments = @comments,
                            AnsweredBy = @answeredBy,
                            AnsweredDate = GETDATE()
                        WHERE QualityEventID = @qualityEventId
                          AND QuestionID = @questionId;
                    END
                    ELSE
                    BEGIN
                        INSERT INTO dbo.QualityEventChecklistAnswers
                        (
                            QualityEventID,
                            QuestionID,
                            AnswerValue,
                            Comments,
                            AnsweredBy,
                            AnsweredDate
                        )
                        VALUES
                        (
                            @qualityEventId,
                            @questionId,
                            @answerValue,
                            @comments,
                            @answeredBy,
                            GETDATE()
                        );
                    END";

                ExecuteNonQuery(query, new[]
                {
                    new SqlParameter("@qualityEventId", qualityEventId),
                    new SqlParameter("@questionId", questionId),
                    new SqlParameter("@answerValue", string.IsNullOrWhiteSpace(answerValue) ? (object)DBNull.Value : answerValue.Trim()),
                    new SqlParameter("@comments", string.IsNullOrWhiteSpace(comments) ? (object)DBNull.Value : comments.Trim()),
                    new SqlParameter("@answeredBy", string.IsNullOrWhiteSpace(answeredBy) ? (object)DBNull.Value : answeredBy.Trim())
                });
            }
        }

        public static string GetMissingRequiredQualityEventChecklist(int qualityEventId)
        {
            DataTable dt = GetQualityEventChecklist(qualityEventId);

            foreach (DataRow row in dt.Rows)
            {
                bool isRequired = row.Table.Columns.Contains("IsRequired") &&
                                  row["IsRequired"] != DBNull.Value &&
                                  Convert.ToBoolean(row["IsRequired"]);

                if (!isRequired)
                    continue;

                string answer = row.Table.Columns.Contains("AnswerValue") ? row.GetSafeString("AnswerValue") : "";
                if (string.IsNullOrWhiteSpace(answer))
                    return row.GetSafeString("SectionName") + ": " + row.GetSafeString("QuestionText");

                string expected = row.Table.Columns.Contains("ExpectedAnswer") ? row.GetSafeString("ExpectedAnswer") : "";
                string comments = row.Table.Columns.Contains("Comments") ? row.GetSafeString("Comments") : "";

                if (AnswerRequiresInvestigationComment(answer, expected) && string.IsNullOrWhiteSpace(comments))
                {
                    return row.GetSafeString("SectionName") + ": " + row.GetSafeString("QuestionText") +
                           "\nComment/evidence is required because the answer is outside the expected response.";
                }
            }

            return "";
        }

        public static bool AnswerRequiresInvestigationComment(string answerValue, string expectedAnswer)
        {
            string answer = (answerValue ?? "").Trim();
            string expected = (expectedAnswer ?? "").Trim();

            if (string.IsNullOrWhiteSpace(answer) || string.IsNullOrWhiteSpace(expected))
                return false;

            if (answer.Equals("NA", StringComparison.OrdinalIgnoreCase) ||
                answer.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
                answer.Equals("Not Applicable", StringComparison.OrdinalIgnoreCase))
                return false;

            return !answer.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        // Authentication and password verification are intentionally centralized in AuthService/UserRepository.
        // DatabaseHelper does not expose a second credential-validation path.

    }
}
