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
    // Environmental Monitoring workflow state, signatures, quality gates, review, and approval.
    public static partial class DatabaseHelper
    {
        private static void EnsureEMWorkflowColumns()
        {
            if (!TableExists("EM_Events"))
                throw new InvalidOperationException("Required table dbo.EM_Events is missing. Apply the controlled database migration.");

            string[] requiredColumns =
            {
                "WorkflowStatus", "ResultsEnteredBy", "ResultsEnteredDate", "SubmittedBy", "SubmittedDate",
                "ReviewedBy", "ReviewedDate", "ApprovedBy", "ApprovedDate"
            };

            foreach (string column in requiredColumns)
            {
                if (!ColumnExists("EM_Events", column))
                    throw new InvalidOperationException("Required column dbo.EM_Events." + column + " is missing. Apply Database/Migrations/20260714_001_Harden_GMP_Workflows.sql.");
            }
        }

        private static void EnsureEMEventSignaturesTable()
        {
            if (TableExists("EM_EventSignatures"))
                return;

            throw new InvalidOperationException("Required table dbo.EM_EventSignatures is missing. Apply Database/Migrations/20260714_001_Harden_GMP_Workflows.sql.");
        }

        public static void EnsureEMWorkflowObjects()
        {
            EnsureEMWorkflowColumns();
            EnsureEMEventSignaturesTable();
        }

        public static string GetEMWorkflowStatus(int eventId)
        {
            if (eventId <= 0 || !TableExists("EM_Events"))
                return "Pending";

            EnsureEMWorkflowColumns();

            string query = @"
                SELECT TOP 1
                    ISNULL(NULLIF(WorkflowStatus, ''), ISNULL(NULLIF(FinalResult, ''), 'Pending')) AS WorkflowStatus
                FROM dbo.EM_Events
                WHERE Id = @eventId";

            object result = ExecuteScalar(query, new[] { new SqlParameter("@eventId", eventId) });
            string status = result?.ToString() ?? "";

            return string.IsNullOrWhiteSpace(status) ? "Pending" : status.Trim();
        }

        private static string GetLockedEMWorkflowStatusInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int eventId)
        {
            using SqlCommand command = new SqlCommand(@"
SELECT TOP 1
       ISNULL(NULLIF(LTRIM(RTRIM(WorkflowStatus)), N''),
              ISNULL(NULLIF(LTRIM(RTRIM(FinalResult)), N''), N'Pending'))
FROM dbo.EM_Events WITH (UPDLOCK, HOLDLOCK)
WHERE Id = @eventId;", connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;

            object value = command.ExecuteScalar();
            if (value == null || value == DBNull.Value)
                throw new InvalidOperationException("The EM event no longer exists.");

            string status = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            return string.IsNullOrWhiteSpace(status) ? "Pending" : status.Trim();
        }

        private static bool HasEmSignatureInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int eventId,
            string signedBy,
            params string[] actionTypes)
        {
            if (eventId <= 0 || string.IsNullOrWhiteSpace(signedBy) || actionTypes == null || actionTypes.Length == 0)
                return false;

            var actionParameters = new List<string>();
            using SqlCommand command = new SqlCommand();
            command.Connection = connection;
            command.Transaction = transaction;
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            command.Parameters.Add("@signedBy", SqlDbType.NVarChar, 120).Value = signedBy.Trim();

            for (int i = 0; i < actionTypes.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(actionTypes[i]))
                    continue;

                string parameterName = "@action" + i.ToString(CultureInfo.InvariantCulture);
                actionParameters.Add(parameterName);
                command.Parameters.Add(parameterName, SqlDbType.NVarChar, 80).Value = actionTypes[i].Trim();
            }

            if (actionParameters.Count == 0)
                return false;

            command.CommandText = @"
SELECT COUNT(1)
FROM dbo.EM_EventSignatures WITH (UPDLOCK, HOLDLOCK)
WHERE EventID = @eventId
  AND UPPER(LTRIM(RTRIM(SignedBy))) = UPPER(LTRIM(RTRIM(@signedBy)))
  AND ActionType IN (" + string.Join(",", actionParameters) + ");";

            int count = Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
            return count > 0;
        }

        private static void EnsureEmApprovalQualityEventGateInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int eventId,
            string eventNo)
        {
            using (SqlCommand oosCommand = new SqlCommand(@"
SELECT CASE WHEN
    EXISTS
    (
        SELECT 1
        FROM dbo.EM_EventPlates WITH (UPDLOCK, HOLDLOCK)
        WHERE EventId = @eventId
          AND UPPER(LTRIM(RTRIM(ISNULL(Status,N'')))) IN (N'OOS',N'ACTION',N'FAIL')
    )
    OR EXISTS
    (
        SELECT 1
        FROM dbo.EM_Events WITH (UPDLOCK, HOLDLOCK)
        WHERE Id = @eventId
          AND UPPER(LTRIM(RTRIM(ISNULL(FinalResult,N'')))) IN (N'OOS',N'ACTION',N'FAIL')
    )
THEN 1 ELSE 0 END;", connection, transaction))
            {
                oosCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                oosCommand.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
                bool hasOos = Convert.ToInt32(oosCommand.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) == 1;
                if (!hasOos)
                    return;
            }

            using SqlCommand qualityEventCommand = new SqlCommand(@"
IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
    THROW 51441, 'Required table dbo.QualityEvents is missing. EM approval was blocked.', 1;

IF COL_LENGTH(N'dbo.QualityEvents', N'SourceRecordID') IS NULL
   OR COL_LENGTH(N'dbo.QualityEvents', N'CurrentStatus') IS NULL
   OR COL_LENGTH(N'dbo.QualityEvents', N'FinalDisposition') IS NULL
   OR COL_LENGTH(N'dbo.QualityEvents', N'QAConclusion') IS NULL
   OR COL_LENGTH(N'dbo.QualityEvents', N'ClosedBy') IS NULL
   OR COL_LENGTH(N'dbo.QualityEvents', N'ClosedDate') IS NULL
    THROW 51442, 'Quality Event schema is incomplete. EM approval was blocked.', 1;

SELECT TOP (1)
       ISNULL(CurrentStatus,N''),
       ISNULL(FinalDisposition,N''),
       ISNULL(QAConclusion,N''),
       ISNULL(ClosedBy,N''),
       ClosedDate
FROM dbo.QualityEvents WITH (UPDLOCK, HOLDLOCK)
WHERE
    (
        SourceRecordID = @eventId
        OR
        (
            NULLIF(LTRIM(RTRIM(@eventNo)),N'') IS NOT NULL
            AND UPPER(LTRIM(RTRIM(ISNULL(SampleNumber,N'')))) = UPPER(LTRIM(RTRIM(@eventNo)))
        )
    )
    AND
    (
        UPPER(LTRIM(RTRIM(ISNULL(DetectionSource,N'')))) = N'ENVIRONMENTAL MONITORING'
        OR UPPER(LTRIM(RTRIM(ISNULL(SourceModule,N'')))) IN (N'ENVIRONMENTAL MONITORING',N'EM')
        OR UPPER(LTRIM(RTRIM(ISNULL(SampleNumber,N'')))) = UPPER(LTRIM(RTRIM(@eventNo)))
    )
ORDER BY QualityEventID DESC;", connection, transaction);
            qualityEventCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            qualityEventCommand.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            qualityEventCommand.Parameters.Add("@eventNo", SqlDbType.NVarChar, 80).Value = string.IsNullOrWhiteSpace(eventNo) ? (object)DBNull.Value : eventNo.Trim();

            using SqlDataReader reader = qualityEventCommand.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException(
                    "This EM event contains ACTION/OOS results but no linked Quality Event was found. Approval is blocked.");
            }

            string status = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
            string disposition = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
            string qaConclusion = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
            string closedBy = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();
            bool hasClosedDate = !reader.IsDBNull(4);

            // Only an explicitly closed Quality Event can release an ACTION/OOS EM event.
            // "Approved" is an investigation workflow state, not a terminal closure. Cancelled/
            // rejected events are terminal for dashboards but do not constitute positive QA release.
            bool closed = status.Equals("Closed", StringComparison.OrdinalIgnoreCase) ||
                          status.Equals("QA Closed", StringComparison.OrdinalIgnoreCase);
            bool qaReleased = disposition.Equals("Approved", StringComparison.OrdinalIgnoreCase) ||
                              disposition.Equals("Area Released after Corrective Action", StringComparison.OrdinalIgnoreCase) ||
                              disposition.Equals("Accept with Justification", StringComparison.OrdinalIgnoreCase);

            if (!closed || !qaReleased || string.IsNullOrWhiteSpace(qaConclusion) || string.IsNullOrWhiteSpace(closedBy) || !hasClosedDate)
            {
                throw new InvalidOperationException(
                    "This EM event contains ACTION/OOS results. Approval requires a linked, closed Quality Event with QA conclusion, explicit release disposition, closer identity, and closure date.");
            }
        }

        private static (int Total, int Entered) GetEMPlateCompletionCountsInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int eventId)
        {
            using SqlCommand command = new SqlCommand(@"
SELECT COUNT(1) AS TotalCount,
       SUM(CASE WHEN TotalCount IS NOT NULL THEN 1 ELSE 0 END) AS EnteredCount
FROM dbo.EM_EventPlates WITH (UPDLOCK, HOLDLOCK)
WHERE EventId = @eventId;", connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;

            using SqlDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return (0, 0);

            int total = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            int entered = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            return (total, entered);
        }

        public static int GetEMTotalPlateCount(int eventId)
        {
            if (eventId <= 0 || !TableExists("EM_EventPlates"))
                return 0;

            object result = ExecuteScalar(
                "SELECT COUNT(1) FROM dbo.EM_EventPlates WHERE EventId = @eventId",
                new[] { new SqlParameter("@eventId", eventId) });

            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        public static int GetEMEnteredPlateCount(int eventId)
        {
            if (eventId <= 0 || !TableExists("EM_EventPlates"))
                return 0;

            object result = ExecuteScalar(@"
                SELECT COUNT(1)
                FROM dbo.EM_EventPlates
                WHERE EventId = @eventId
                  AND TotalCount IS NOT NULL",
                new[] { new SqlParameter("@eventId", eventId) });

            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        public static bool CanPrintEMResultReport(int eventId, string username, out string message)
        {
            message = "";

            string effectiveUsername;
            try
            {
                effectiveUsername = ResolveAuthenticatedSigner(username);
            }
            catch (Exception ex)
            {
                message = Infrastructure.UserFacingError.SafeMessage(ex, "EM report print authorization");
                return false;
            }

            if (eventId <= 0)
            {
                message = "No EM event is loaded.";
                return false;
            }

            string status = GetEMWorkflowStatus(eventId);

            if (!status.Equals("Approved", StringComparison.OrdinalIgnoreCase))
            {
                message = "EM Result Report can be printed only after Review and Approval. Current status: " + status;
                return false;
            }

            int total = GetEMTotalPlateCount(eventId);
            int entered = GetEMEnteredPlateCount(eventId);

            if (total <= 0)
            {
                message = "No EM plates were found for this event.";
                return false;
            }

            if (entered != total)
            {
                message = "All EM plate results must be entered before printing the approved report. Entered: " + entered + " of " + total + ".";
                return false;
            }

            try
            {
                // Re-validate the OOS / Quality Event release gate at print time. Approval may
                // have occurred earlier, but a final controlled report must not be printed if
                // the linked investigation has since been reopened or its closure evidence is
                // no longer complete. The same fail-closed gate used by approval is reused here.
                ExecuteInTransaction((connection, transaction) =>
                {
                    EnsureUserPermissionInTransaction(
                        connection,
                        transaction,
                        effectiveUsername,
                        "CanAccessReports",
                        "print approved EM Result Report");

                    string eventNo;
                    using (SqlCommand eventCommand = new SqlCommand(@"
SELECT EventNo
FROM dbo.EM_Events WITH (UPDLOCK, HOLDLOCK)
WHERE Id = @eventId;", connection, transaction))
                    {
                        eventCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        eventCommand.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
                        object value = eventCommand.ExecuteScalar();
                        if (value == null || value == DBNull.Value)
                            throw new InvalidOperationException("The EM event no longer exists. Report printing is blocked.");
                        eventNo = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    }

                    EnsureEmApprovalQualityEventGateInTransaction(connection, transaction, eventId, eventNo);
                });
            }
            catch (Exception ex)
            {
                message = Infrastructure.UserFacingError.SafeMessage(ex, "EM report print validation");
                return false;
            }

            return true;
        }

        public static DataTable GetEMEventSignatures(int eventId)
        {
            EnsureEMEventSignaturesTable();

            string query = @"
                SELECT
                    S.SignatureID,
                    S.EventID,
                    S.EventNo,
                    S.ActionType,
                    S.ActionReason,
                    S.SignedBy,
                    COALESCE(NULLIF(U.FullName,N''),S.SignedBy) AS SignerDisplayName,
                    S.UserRole,
                    S.MeaningOfSignature,
                    S.SignedAt
                FROM dbo.EM_EventSignatures S
                LEFT JOIN dbo.Users U ON U.Username=S.SignedBy
                WHERE S.EventID = @eventId
                ORDER BY S.SignatureID";

            return ExecuteQuery(query, new[] { new SqlParameter("@eventId", eventId) });
        }

        public static int AddEMEventSignature(
            int eventId,
            string eventNo,
            string actionType,
            string signedBy,
            string meaningOfSignature,
            string reason = null)
        {
            ValidateSignatureMetadata(eventId, actionType, meaningOfSignature);
            EnsureEMEventSignaturesTable();

            string effectiveSignedBy = ResolveAuthenticatedSigner(signedBy);

            string query = @"
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
                    @actionType,
                    @reason,
                    @signedBy,
                    @userRole,
                    @meaning,
                    GETDATE()
                )";

            SqlParameter[] pars =
            {
                new SqlParameter("@eventId", eventId),
                NVarCharParameter("@eventNo", eventNo, 50),
                NVarCharParameter("@actionType", actionType, 100),
                NVarCharMaxParameter("@reason", reason),
                NVarCharParameter("@signedBy", effectiveSignedBy, 100),
                NVarCharParameter("@userRole", GetUserRole(effectiveSignedBy), 100),
                NVarCharParameter("@meaning", meaningOfSignature, 255)
            };

            return ExecuteNonQuery(query, pars);
        }

        public static void MarkEMResultsEntered(
            int eventId,
            string eventNo,
            string signedBy,
            string meaningOfSignature,
            string reason)
        {
            EnsureEMWorkflowObjects();
            string effectiveSignedBy = ResolveAuthenticatedSigner(signedBy);

            ExecuteInTransaction((conn, tx) =>
            {
                string signerRole = EnsureUserPermissionInTransaction(
                    conn, tx, effectiveSignedBy, "CanEnterResults", "record EM results");

                int updated = ExecuteNonQueryWithTransaction(@"
                    UPDATE dbo.EM_Events
                    SET WorkflowStatus = 'Results Entered'
                    WHERE Id = @eventId
                      AND ISNULL(WorkflowStatus, N'Pending') NOT IN
                          (N'Under Review', N'Reviewed', N'Approved', N'Completed', N'Closed', N'Cancelled')",
                    new[]
                    {
                        new SqlParameter("@eventId", eventId)
                    }, conn, tx);

                if (updated != 1)
                    throw new InvalidOperationException("The EM event is no longer editable. Result-entry workflow was rolled back.");

                ExecuteNonQueryWithTransaction(@"
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
                        'EM Result Entry',
                        @reason,
                        @signedBy,
                        @userRole,
                        @meaning,
                        GETDATE()
                    )",
                    new[]
                    {
                        new SqlParameter("@eventId", eventId),
                        new SqlParameter("@eventNo", string.IsNullOrWhiteSpace(eventNo) ? (object)DBNull.Value : eventNo),
                        new SqlParameter("@reason", string.IsNullOrWhiteSpace(reason) ? (object)DBNull.Value : reason),
                        new SqlParameter("@signedBy", effectiveSignedBy),
                        new SqlParameter("@userRole", signerRole),
                        new SqlParameter("@meaning", string.IsNullOrWhiteSpace(meaningOfSignature) ? (object)DBNull.Value : meaningOfSignature)
                    }, conn, tx);

                AddAuditTrailAdvanced(
                    conn, tx,
                    "EM_Events", eventId, "EM Result Entry", "", "Results Entered",
                    reason, effectiveSignedBy,
                    "WorkflowStatus", null, eventNo, "Environmental Monitoring");
            });
        }

        public static void MarkEMEventUnderInvestigation(
            int eventId,
            string eventNo,
            string modifiedBy,
            string reason)
        {
            EnsureEMWorkflowObjects();
            string effectiveModifiedBy = ResolveAuthenticatedSigner(modifiedBy);

            ExecuteInTransaction((conn, tx) =>
            {
                string signerRole = EnsureActiveUserInTransaction(
                    conn, tx, effectiveModifiedBy, "place an EM event under investigation");

                int updated = ExecuteNonQueryWithTransaction(@"
                    UPDATE dbo.EM_Events
                    SET
                        WorkflowStatus = 'Under Investigation',
                        FinalResult = CASE
                            WHEN ISNULL(NULLIF(FinalResult, ''), '') IN ('', 'Pending', 'Results Entered', 'Partially Entered') THEN 'OOS'
                            ELSE FinalResult
                        END
                    WHERE Id = @eventId
                      AND ISNULL(WorkflowStatus, '') <> 'Approved'",
                    new[]
                    {
                        new SqlParameter("@eventId", eventId)
                    }, conn, tx);

                if (updated != 1)
                    throw new InvalidOperationException("The EM event can no longer be placed under investigation. No signature was recorded.");

                ExecuteNonQueryWithTransaction(@"
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
                        'EM Investigation Required',
                        @reason,
                        @signedBy,
                        @userRole,
                        'ACTION/OOS result requires Quality Event investigation before approval',
                        GETDATE()
                    )",
                    new[]
                    {
                        new SqlParameter("@eventId", eventId),
                        new SqlParameter("@eventNo", string.IsNullOrWhiteSpace(eventNo) ? (object)DBNull.Value : eventNo),
                        new SqlParameter("@reason", string.IsNullOrWhiteSpace(reason) ? (object)DBNull.Value : reason),
                        new SqlParameter("@signedBy", effectiveModifiedBy),
                        new SqlParameter("@userRole", signerRole)
                    }, conn, tx);

                AddAuditTrailAdvanced(
                    conn, tx,
                    "EM_Events", eventId, "EM Investigation Required", "", "Under Investigation",
                    reason, effectiveModifiedBy,
                    "WorkflowStatus", null, eventNo, "Environmental Monitoring");
            });
        }

        public static void SubmitEMEventForReview(
            int eventId,
            string eventNo,
            string submittedBy,
            string meaningOfSignature,
            string reason)
        {
            EnsureEMWorkflowObjects();
            string effectiveSubmittedBy = ResolveAuthenticatedSigner(submittedBy);
            string currentStatus = GetEMWorkflowStatus(eventId);

            if (!currentStatus.Equals("Results Entered", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only complete EM events with entered results can be submitted for review.");
            }

            int total = GetEMTotalPlateCount(eventId);
            int entered = GetEMEnteredPlateCount(eventId);

            if (total <= 0 || entered != total)
            {
                throw new InvalidOperationException("All EM plate results must be entered before submitting for review. Entered: " + entered + " of " + total + ".");
            }

            ExecuteInTransaction((conn, tx) =>
            {
                string signerRole = EnsureUserPermissionInTransaction(
                    conn, tx, effectiveSubmittedBy, "CanSubmitForReview", "submit EM results for review");

                string lockedStatus = GetLockedEMWorkflowStatusInTransaction(conn, tx, eventId);
                if (!lockedStatus.Equals("Results Entered", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The EM event status changed while the electronic signature was being completed. " +
                        "Submission was cancelled. Current status: " + lockedStatus + ".");
                }

                (int currentTotal, int currentEntered) = GetEMPlateCompletionCountsInTransaction(conn, tx, eventId);
                if (currentTotal <= 0 || currentEntered != currentTotal)
                {
                    throw new InvalidOperationException(
                        "EM plate completion changed while the electronic signature was being completed. " +
                        "Submission was cancelled. Entered: " + currentEntered + " of " + currentTotal + ".");
                }

                int updated = ExecuteNonQueryWithTransaction(@"
                    UPDATE dbo.EM_Events
                    SET WorkflowStatus = 'Under Review',
                        SubmittedBy = @submittedBy,
                        SubmittedDate = GETDATE()
                    WHERE Id = @eventId
                      AND ISNULL(NULLIF(LTRIM(RTRIM(WorkflowStatus)), N''),
                                 ISNULL(NULLIF(LTRIM(RTRIM(FinalResult)), N''), N'Pending')) = N'Results Entered'",
                    new[]
                    {
                        new SqlParameter("@eventId", eventId),
                        new SqlParameter("@submittedBy", effectiveSubmittedBy)
                    }, conn, tx);

                if (updated != 1)
                {
                    throw new InvalidOperationException(
                        "The EM event changed during submission. No workflow change was committed.");
                }

                ExecuteNonQueryWithTransaction(@"
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
                        'EM Submit for Review',
                        @reason,
                        @signedBy,
                        @userRole,
                        @meaning,
                        GETDATE()
                    )",
                    new[]
                    {
                        new SqlParameter("@eventId", eventId),
                        new SqlParameter("@eventNo", string.IsNullOrWhiteSpace(eventNo) ? (object)DBNull.Value : eventNo),
                        new SqlParameter("@reason", string.IsNullOrWhiteSpace(reason) ? (object)DBNull.Value : reason),
                        new SqlParameter("@signedBy", effectiveSubmittedBy),
                        new SqlParameter("@userRole", signerRole),
                        new SqlParameter("@meaning", string.IsNullOrWhiteSpace(meaningOfSignature) ? (object)DBNull.Value : meaningOfSignature)
                    }, conn, tx);

                AddAuditTrailAdvanced(
                    conn, tx,
                    "EM_Events", eventId, "EM Submit for Review", lockedStatus, "Under Review",
                    reason, effectiveSubmittedBy,
                    "WorkflowStatus", null, eventNo, "Environmental Monitoring");

                currentStatus = lockedStatus;
            });
        }

        public static void ReviewEMEvent(
            int eventId,
            string eventNo,
            string reviewedBy,
            string meaningOfSignature,
            string reason)
        {
            EnsureEMWorkflowObjects();
            string effectiveReviewedBy = ResolveAuthenticatedSigner(reviewedBy);
            string currentStatus = GetEMWorkflowStatus(eventId);

            if (!currentStatus.Equals("Under Review", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only EM events under review can be reviewed.");

            ExecuteInTransaction((conn, tx) =>
            {
                string signerRole = EnsureUserPermissionInTransaction(
                    conn, tx, effectiveReviewedBy, "CanReviewResults", "review EM results");

                string lockedStatus = GetLockedEMWorkflowStatusInTransaction(conn, tx, eventId);
                if (!lockedStatus.Equals("Under Review", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The EM event status changed while the electronic signature was being completed. " +
                        "Review was cancelled. Current status: " + lockedStatus + ".");
                }

                bool adminOverride = AppConfig.DevelopmentAdminFullPermissions && IsAdministrativeRole(signerRole);
                if (!adminOverride &&
                    HasEmSignatureInTransaction(
                        conn,
                        tx,
                        eventId,
                        effectiveReviewedBy,
                        "EM Result Entry",
                        "EM Submit for Review"))
                {
                    throw new InvalidOperationException(
                        "The user who entered or submitted these EM results cannot perform the independent review.");
                }

                int updated = ExecuteNonQueryWithTransaction(@"
                    UPDATE dbo.EM_Events
                    SET WorkflowStatus = 'Reviewed',
                        ReviewedBy = @reviewedBy,
                        ReviewedDate = GETDATE()
                    WHERE Id = @eventId
                      AND ISNULL(NULLIF(LTRIM(RTRIM(WorkflowStatus)), N''),
                                 ISNULL(NULLIF(LTRIM(RTRIM(FinalResult)), N''), N'Pending')) = N'Under Review'",
                    new[]
                    {
                        new SqlParameter("@eventId", eventId),
                        new SqlParameter("@reviewedBy", effectiveReviewedBy)
                    }, conn, tx);

                if (updated != 1)
                {
                    throw new InvalidOperationException(
                        "The EM event changed during review. No workflow change was committed.");
                }

                ExecuteNonQueryWithTransaction(@"
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
                        'EM Review',
                        @reason,
                        @signedBy,
                        @userRole,
                        @meaning,
                        GETDATE()
                    )",
                    new[]
                    {
                        new SqlParameter("@eventId", eventId),
                        new SqlParameter("@eventNo", string.IsNullOrWhiteSpace(eventNo) ? (object)DBNull.Value : eventNo),
                        new SqlParameter("@reason", string.IsNullOrWhiteSpace(reason) ? (object)DBNull.Value : reason),
                        new SqlParameter("@signedBy", effectiveReviewedBy),
                        new SqlParameter("@userRole", signerRole),
                        new SqlParameter("@meaning", string.IsNullOrWhiteSpace(meaningOfSignature) ? (object)DBNull.Value : meaningOfSignature)
                    }, conn, tx);

                AddAuditTrailAdvanced(
                    conn, tx,
                    "EM_Events", eventId, "EM Review", lockedStatus, "Reviewed",
                    reason, effectiveReviewedBy,
                    "WorkflowStatus", null, eventNo, "Environmental Monitoring");

                currentStatus = lockedStatus;
            });
        }

        public static void ApproveEMEvent(
            int eventId,
            string eventNo,
            string approvedBy,
            string meaningOfSignature,
            string reason)
        {
            EnsureEMWorkflowObjects();
            string effectiveApprovedBy = ResolveAuthenticatedSigner(approvedBy);
            string currentStatus = GetEMWorkflowStatus(eventId);

            if (!currentStatus.Equals("Reviewed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only reviewed EM events can be approved.");

            int linkedPlanId = 0;

            ExecuteInTransaction((conn, tx) =>
            {
                string signerRole = EnsureUserPermissionInTransaction(
                    conn, tx, effectiveApprovedBy, "CanApproveResults", "approve EM results");

                string lockedStatus = GetLockedEMWorkflowStatusInTransaction(conn, tx, eventId);
                if (!lockedStatus.Equals("Reviewed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The EM event status changed while the electronic signature was being completed. " +
                        "Approval was cancelled. Current status: " + lockedStatus + ".");
                }

                bool adminOverride = AppConfig.DevelopmentAdminFullPermissions && IsAdministrativeRole(signerRole);
                if (!adminOverride &&
                    HasEmSignatureInTransaction(
                        conn,
                        tx,
                        eventId,
                        effectiveApprovedBy,
                        "EM Result Entry",
                        "EM Submit for Review",
                        "EM Review"))
                {
                    throw new InvalidOperationException(
                        "The result entrant, submitter, or reviewer cannot perform QA approval for the same EM event.");
                }

                EnsureEmApprovalQualityEventGateInTransaction(conn, tx, eventId, eventNo);

                using (SqlCommand planCommand = new SqlCommand(
                    "SELECT ISNULL(PlanID, 0) FROM dbo.EM_Events WHERE Id = @eventId", conn, tx))
                {
                    planCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    planCommand.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
                    object linkedPlanValue = planCommand.ExecuteScalar();
                    if (linkedPlanValue != null && linkedPlanValue != DBNull.Value)
                        linkedPlanId = Convert.ToInt32(linkedPlanValue, CultureInfo.InvariantCulture);
                }

                int updated = ExecuteNonQueryWithTransaction(@"
                    UPDATE dbo.EM_Events
                    SET WorkflowStatus = 'Approved',
                        ApprovedBy = @approvedBy,
                        ApprovedDate = GETDATE()
                    WHERE Id = @eventId
                      AND ISNULL(NULLIF(LTRIM(RTRIM(WorkflowStatus)), N''),
                                 ISNULL(NULLIF(LTRIM(RTRIM(FinalResult)), N''), N'Pending')) = N'Reviewed'",
                    new[]
                    {
                        new SqlParameter("@eventId", eventId),
                        new SqlParameter("@approvedBy", effectiveApprovedBy)
                    }, conn, tx);

                if (updated != 1)
                {
                    throw new InvalidOperationException(
                        "The EM event changed during approval. No workflow change was committed.");
                }

                ExecuteNonQueryWithTransaction(@"
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
                        'EM Approval',
                        @reason,
                        @signedBy,
                        @userRole,
                        @meaning,
                        GETDATE()
                    )",
                    new[]
                    {
                        new SqlParameter("@eventId", eventId),
                        new SqlParameter("@eventNo", string.IsNullOrWhiteSpace(eventNo) ? (object)DBNull.Value : eventNo),
                        new SqlParameter("@reason", string.IsNullOrWhiteSpace(reason) ? (object)DBNull.Value : reason),
                        new SqlParameter("@signedBy", effectiveApprovedBy),
                        new SqlParameter("@userRole", signerRole),
                        new SqlParameter("@meaning", string.IsNullOrWhiteSpace(meaningOfSignature) ? (object)DBNull.Value : meaningOfSignature)
                    }, conn, tx);

                if (linkedPlanId > 0)
                {
                    string planStatusBefore;
                    using (SqlCommand statusBeforeCommand = new SqlCommand(
                        "SELECT ISNULL(Status,N'') FROM dbo.EM_Plans WITH (UPDLOCK,HOLDLOCK) WHERE PlanID=@planId;", conn, tx))
                    {
                        statusBeforeCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        statusBeforeCommand.Parameters.Add("@planId", SqlDbType.Int).Value = linkedPlanId;
                        object value = statusBeforeCommand.ExecuteScalar();
                        planStatusBefore = value == null || value == DBNull.Value
                            ? string.Empty
                            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    }

                    ExecuteNonQueryWithTransaction(@"
IF EXISTS (SELECT 1 FROM dbo.EM_Events WHERE PlanID=@planId)
   AND NOT EXISTS
   (
       SELECT 1
       FROM dbo.EM_Events
       WHERE PlanID=@planId
         AND ISNULL(WorkflowStatus,N'Pending')<>N'Approved'
   )
   AND EXISTS
   (
       SELECT 1 FROM dbo.EM_Plans
       WHERE PlanID=@planId AND Status=N'Ready for Results'
   )
BEGIN
    UPDATE dbo.EM_Plans
    SET Status=N'Completed', CompletedBy=@approvedBy, CompletedAt=SYSDATETIME()
    WHERE PlanID=@planId;

    UPDATE dbo.EM_PlanSamples
    SET Status=N'Completed'
    WHERE PlanID=@planId AND Status IN (N'Ready for Results',N'Control Passed');

    IF OBJECT_ID(N'dbo.EM_PlanSignatures',N'U') IS NOT NULL
    BEGIN
        INSERT dbo.EM_PlanSignatures
        (PlanID,ActionType,ActionReason,SignedBy,UserRole,MeaningOfSignature,SignedAt)
        VALUES
        (@planId,N'Plan Completed',@reason,@approvedBy,@userRole,
         N'All linked EM events reviewed and approved',SYSDATETIME());
    END;
END;",
                        new[]
                        {
                            new SqlParameter("@planId", linkedPlanId),
                            new SqlParameter("@approvedBy", effectiveApprovedBy),
                            new SqlParameter("@reason", reason ?? string.Empty),
                            new SqlParameter("@userRole", signerRole)
                        }, conn, tx);

                    string planStatusAfter;
                    using (SqlCommand statusAfterCommand = new SqlCommand(
                        "SELECT ISNULL(Status,N'') FROM dbo.EM_Plans WHERE PlanID=@planId;", conn, tx))
                    {
                        statusAfterCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        statusAfterCommand.Parameters.Add("@planId", SqlDbType.Int).Value = linkedPlanId;
                        object value = statusAfterCommand.ExecuteScalar();
                        planStatusAfter = value == null || value == DBNull.Value
                            ? string.Empty
                            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    }

                    if (!planStatusBefore.Equals("Completed", StringComparison.OrdinalIgnoreCase) &&
                        planStatusAfter.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                    {
                        AddAuditTrailAdvanced(
                            conn, tx,
                            "EM_Plans", linkedPlanId, "EM Plan Completed",
                            planStatusBefore, "Completed",
                            "All linked EM events reviewed and approved. " + reason,
                            effectiveApprovedBy,
                            "Status", null, null, "Environmental Monitoring");
                    }
                }

                AddAuditTrailAdvanced(
                    conn, tx,
                    "EM_Events", eventId, "EM Approval", lockedStatus, "Approved",
                    reason, effectiveApprovedBy,
                    "WorkflowStatus", null, eventNo, "Environmental Monitoring");

                currentStatus = lockedStatus;
            });
        }

    }
}
