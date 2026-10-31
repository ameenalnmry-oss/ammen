using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System.Data;
using System.Globalization;
// Exact site backend method bodies. BoundaryBase supplies explicit test adapters
// for authentication, planning, status and auditing; no claim about those controls.
internal static partial class SiteEmWorkflowCases
{
    private sealed class Frozen : BoundaryBase
    {
        internal const string ReviewEMEventSource = "public static void ReviewEMEvent(\n            int eventId,\n            string eventNo,\n            string reviewedBy,\n            string meaningOfSignature,\n            string reason)\n        {\n            EnsureEMWorkflowObjects();\n            string effectiveReviewedBy = ResolveAuthenticatedSigner(reviewedBy);\n            string currentStatus = GetEMWorkflowStatus(eventId);\n\n            if (!currentStatus.Equals(\"Under Review\", StringComparison.OrdinalIgnoreCase))\n                throw new InvalidOperationException(\"Only EM events under review can be reviewed.\");\n\n            ExecuteInTransaction((conn, tx) =>\n            {\n                string signerRole = EnsureUserPermissionInTransaction(\n                    conn, tx, effectiveReviewedBy, \"CanReviewResults\", \"review EM results\");\n                EnsureEmSourcePlanInTransaction(conn, tx, eventId, AppConfig.IsProduction);\n\n                string lockedStatus = GetLockedEMWorkflowStatusInTransaction(conn, tx, eventId);\n                if (!lockedStatus.Equals(\"Under Review\", StringComparison.OrdinalIgnoreCase))\n                {\n                    throw new InvalidOperationException(\n                        \"The EM event status changed while the electronic signature was being completed. \" +\n                        \"Review was cancelled. Current status: \" + lockedStatus + \".\");\n                }\n\n                bool adminOverride = AppConfig.DevelopmentAdminFullPermissions && IsAdministrativeRole(signerRole);\n                if (!adminOverride &&\n                    HasEmSignatureInTransaction(\n                        conn,\n                        tx,\n                        eventId,\n                        effectiveReviewedBy,\n                        \"EM Result Entry\",\n                        \"EM Submit for Review\"))\n                {\n                    throw new InvalidOperationException(\n                        \"The user who entered or submitted these EM results cannot perform the independent review.\");\n                }\n\n                int updated = ExecuteNonQueryWithTransaction(@\"\n                    UPDATE dbo.EM_Events\n                    SET WorkflowStatus = 'Reviewed',\n                        ReviewedBy = @reviewedBy,\n                        ReviewedDate = GETDATE()\n                    WHERE Id = @eventId\n                      AND ISNULL(NULLIF(LTRIM(RTRIM(WorkflowStatus)), N''),\n                                 ISNULL(NULLIF(LTRIM(RTRIM(FinalResult)), N''), N'Pending')) = N'Under Review'\",\n                    new[]\n                    {\n                        new SqlParameter(\"@eventId\", eventId),\n                        new SqlParameter(\"@reviewedBy\", effectiveReviewedBy)\n                    }, conn, tx);\n\n                if (updated != 1)\n                {\n                    throw new InvalidOperationException(\n                        \"The EM event changed during review. No workflow change was committed.\");\n                }\n\n                ExecuteNonQueryWithTransaction(@\"\n                    INSERT INTO dbo.EM_EventSignatures\n                    (\n                        EventID,\n                        EventNo,\n                        ActionType,\n                        ActionReason,\n                        SignedBy,\n                        UserRole,\n                        MeaningOfSignature,\n                        SignedAt\n                    )\n                    VALUES\n                    (\n                        @eventId,\n                        @eventNo,\n                        'EM Review',\n                        @reason,\n                        @signedBy,\n                        @userRole,\n                        @meaning,\n                        GETDATE()\n                    )\",\n                    new[]\n                    {\n                        new SqlParameter(\"@eventId\", eventId),\n                        new SqlParameter(\"@eventNo\", string.IsNullOrWhiteSpace(eventNo) ? (object)DBNull.Value : eventNo),\n                        new SqlParameter(\"@reason\", string.IsNullOrWhiteSpace(reason) ? (object)DBNull.Value : reason),\n                        new SqlParameter(\"@signedBy\", effectiveReviewedBy),\n                        new SqlParameter(\"@userRole\", signerRole),\n                        new SqlParameter(\"@meaning\", string.IsNullOrWhiteSpace(meaningOfSignature) ? (object)DBNull.Value : meaningOfSignature)\n                    }, conn, tx);\n\n                AddAuditTrailAdvanced(\n                    conn, tx,\n                    \"EM_Events\", eventId, \"EM Review\", lockedStatus, \"Reviewed\",\n                    reason, effectiveReviewedBy,\n                    \"WorkflowStatus\", null, eventNo, \"Environmental Monitoring\");\n\n                currentStatus = lockedStatus;\n            });\n        }";
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
                EnsureEmSourcePlanInTransaction(conn, tx, eventId, AppConfig.IsProduction);

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
        internal const string ApproveEMEventSource = "public static void ApproveEMEvent(\n            int eventId,\n            string eventNo,\n            string approvedBy,\n            string meaningOfSignature,\n            string reason)\n        {\n            EnsureEMWorkflowObjects();\n            string effectiveApprovedBy = ResolveAuthenticatedSigner(approvedBy);\n            string currentStatus = GetEMWorkflowStatus(eventId);\n\n            if (!currentStatus.Equals(\"Reviewed\", StringComparison.OrdinalIgnoreCase))\n                throw new InvalidOperationException(\"Only reviewed EM events can be approved.\");\n\n            int linkedPlanId = 0;\n\n            ExecuteInTransaction((conn, tx) =>\n            {\n                string signerRole = EnsureQaApprovalAuthorizationInTransaction(\n                    conn, tx, effectiveApprovedBy, \"approve EM results\");\n                linkedPlanId = EnsureEmSourcePlanInTransaction(conn, tx, eventId, AppConfig.IsProduction);\n\n                string lockedStatus = GetLockedEMWorkflowStatusInTransaction(conn, tx, eventId);\n                if (!lockedStatus.Equals(\"Reviewed\", StringComparison.OrdinalIgnoreCase))\n                {\n                    throw new InvalidOperationException(\n                        \"The EM event status changed while the electronic signature was being completed. \" +\n                        \"Approval was cancelled. Current status: \" + lockedStatus + \".\");\n                }\n\n                bool adminOverride = AppConfig.DevelopmentAdminFullPermissions && IsAdministrativeRole(signerRole);\n                if (!adminOverride &&\n                    HasEmSignatureInTransaction(\n                        conn,\n                        tx,\n                        eventId,\n                        effectiveApprovedBy,\n                        \"EM Result Entry\",\n                        \"EM Submit for Review\",\n                        \"EM Review\"))\n                {\n                    throw new InvalidOperationException(\n                        \"The result entrant, submitter, or reviewer cannot perform QA approval for the same EM event.\");\n                }\n\n                EnsureEmApprovalQualityEventGateInTransaction(conn, tx, eventId, eventNo);\n\n                // Apply the exact frozen-evidence consistency gate used by\n                // EM report printing BEFORE approving any plate/event. Read\n                // under the existing signing transaction and row locks; an\n                // inconsistency rolls back the entire approval and signature.\n                // Historical approved records are not modified here.\n                DataTable approvalEvidence = ReadEmPlateSnapshotInTransaction(conn, tx, eventId);\n                EmReportEvidenceGuard.EnsureConsistent(approvalEvidence);\n\n                int updated = ExecuteNonQueryWithTransaction(@\"\n                    UPDATE dbo.EM_Events\n                    SET WorkflowStatus = 'Approved',\n                        ApprovedBy = @approvedBy,\n                        ApprovedDate = GETDATE()\n                    WHERE Id = @eventId\n                      AND ISNULL(NULLIF(LTRIM(RTRIM(WorkflowStatus)), N''),\n                                 ISNULL(NULLIF(LTRIM(RTRIM(FinalResult)), N''), N'Pending')) = N'Reviewed'\",\n                    new[]\n                    {\n                        new SqlParameter(\"@eventId\", eventId),\n                        new SqlParameter(\"@approvedBy\", effectiveApprovedBy)\n                    }, conn, tx);\n\n                if (updated != 1)\n                {\n                    throw new InvalidOperationException(\n                        \"The EM event changed during approval. No workflow change was committed.\");\n                }\n\n                ExecuteNonQueryWithTransaction(@\"\n                    INSERT INTO dbo.EM_EventSignatures\n                    (\n                        EventID,\n                        EventNo,\n                        ActionType,\n                        ActionReason,\n                        SignedBy,\n                        UserRole,\n                        MeaningOfSignature,\n                        SignedAt\n                    )\n                    VALUES\n                    (\n                        @eventId,\n                        @eventNo,\n                        'EM Approval',\n                        @reason,\n                        @signedBy,\n                        @userRole,\n                        @meaning,\n                        GETDATE()\n                    )\",\n                    new[]\n                    {\n                        new SqlParameter(\"@eventId\", eventId),\n                        new SqlParameter(\"@eventNo\", string.IsNullOrWhiteSpace(eventNo) ? (object)DBNull.Value : eventNo),\n                        new SqlParameter(\"@reason\", string.IsNullOrWhiteSpace(reason) ? (object)DBNull.Value : reason),\n                        new SqlParameter(\"@signedBy\", effectiveApprovedBy),\n                        new SqlParameter(\"@userRole\", signerRole),\n                        new SqlParameter(\"@meaning\", string.IsNullOrWhiteSpace(meaningOfSignature) ? (object)DBNull.Value : meaningOfSignature)\n                    }, conn, tx);\n\n                if (linkedPlanId > 0)\n                {\n                    string planStatusBefore;\n                    using (SqlCommand statusBeforeCommand = new SqlCommand(\n                        \"SELECT ISNULL(Status,N'') FROM dbo.EM_Plans WITH (UPDLOCK,HOLDLOCK) WHERE PlanID=@planId;\", conn, tx))\n                    {\n                        statusBeforeCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;\n                        statusBeforeCommand.Parameters.Add(\"@planId\", SqlDbType.Int).Value = linkedPlanId;\n                        object value = statusBeforeCommand.ExecuteScalar();\n                        planStatusBefore = value == null || value == DBNull.Value\n                            ? string.Empty\n                            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;\n                    }\n\n                    ExecuteNonQueryWithTransaction(@\"\nIF EXISTS (SELECT 1 FROM dbo.EM_Events WHERE PlanID=@planId)\n   AND NOT EXISTS\n   (\n       SELECT 1\n       FROM dbo.EM_Events\n       WHERE PlanID=@planId\n         AND ISNULL(WorkflowStatus,N'Pending')<>N'Approved'\n   )\n   AND EXISTS\n   (\n       SELECT 1 FROM dbo.EM_Plans\n       WHERE PlanID=@planId AND Status=N'Ready for Results'\n   )\nBEGIN\n    UPDATE dbo.EM_Plans\n    SET Status=N'Completed', CompletedBy=@approvedBy, CompletedAt=SYSDATETIME()\n    WHERE PlanID=@planId;\n\n    UPDATE dbo.EM_PlanSamples\n    SET Status=N'Completed'\n    WHERE PlanID=@planId AND Status IN (N'Ready for Results',N'Control Passed');\n\n    IF OBJECT_ID(N'dbo.EM_PlanSignatures',N'U') IS NOT NULL\n    BEGIN\n        INSERT dbo.EM_PlanSignatures\n        (PlanID,ActionType,ActionReason,SignedBy,UserRole,MeaningOfSignature,SignedAt)\n        VALUES\n        (@planId,N'Plan Completed',@reason,@approvedBy,@userRole,\n         N'All linked EM events reviewed and approved',SYSDATETIME());\n    END;\nEND;\",\n                        new[]\n                        {\n                            new SqlParameter(\"@planId\", linkedPlanId),\n                            new SqlParameter(\"@approvedBy\", effectiveApprovedBy),\n                            new SqlParameter(\"@reason\", reason ?? string.Empty),\n                            new SqlParameter(\"@userRole\", signerRole)\n                        }, conn, tx);\n\n                    string planStatusAfter;\n                    using (SqlCommand statusAfterCommand = new SqlCommand(\n                        \"SELECT ISNULL(Status,N'') FROM dbo.EM_Plans WHERE PlanID=@planId;\", conn, tx))\n                    {\n                        statusAfterCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;\n                        statusAfterCommand.Parameters.Add(\"@planId\", SqlDbType.Int).Value = linkedPlanId;\n                        object value = statusAfterCommand.ExecuteScalar();\n                        planStatusAfter = value == null || value == DBNull.Value\n                            ? string.Empty\n                            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;\n                    }\n\n                    if (!planStatusBefore.Equals(\"Completed\", StringComparison.OrdinalIgnoreCase) &&\n                        planStatusAfter.Equals(\"Completed\", StringComparison.OrdinalIgnoreCase))\n                    {\n                        AddAuditTrailAdvanced(\n                            conn, tx,\n                            \"EM_Plans\", linkedPlanId, \"EM Plan Completed\",\n                            planStatusBefore, \"Completed\",\n                            \"All linked EM events reviewed and approved. \" + reason,\n                            effectiveApprovedBy,\n                            \"Status\", null, null, \"Environmental Monitoring\");\n                    }\n                }\n\n                AddAuditTrailAdvanced(\n                    conn, tx,\n                    \"EM_Events\", eventId, \"EM Approval\", lockedStatus, \"Approved\",\n                    reason, effectiveApprovedBy,\n                    \"WorkflowStatus\", null, eventNo, \"Environmental Monitoring\");\n\n                currentStatus = lockedStatus;\n            });\n        }";
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
                string signerRole = EnsureQaApprovalAuthorizationInTransaction(
                    conn, tx, effectiveApprovedBy, "approve EM results");
                linkedPlanId = EnsureEmSourcePlanInTransaction(conn, tx, eventId, AppConfig.IsProduction);

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

                // Apply the exact frozen-evidence consistency gate used by
                // EM report printing BEFORE approving any plate/event. Read
                // under the existing signing transaction and row locks; an
                // inconsistency rolls back the entire approval and signature.
                // Historical approved records are not modified here.
                DataTable approvalEvidence = ReadEmPlateSnapshotInTransaction(conn, tx, eventId);
                EmReportEvidenceGuard.EnsureConsistent(approvalEvidence);

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
    private sealed class Corrected : BoundaryBase
    {
        internal const string ReviewEMEventSource = "public static void ReviewEMEvent(\n            int eventId,\n            string eventNo,\n            string reviewedBy,\n            string meaningOfSignature,\n            string reason, DataTable expectedEvidence)\n        {\n            EnsureEMWorkflowObjects();\n            string effectiveReviewedBy = ResolveAuthenticatedSigner(reviewedBy);\n            string currentStatus = GetEMWorkflowStatus(eventId);\n\n            if (!currentStatus.Equals(\"Under Review\", StringComparison.OrdinalIgnoreCase))\n                throw new InvalidOperationException(\"Only EM events under review can be reviewed.\");\n\n            ExecuteInTransaction((conn, tx) =>\n            {\n                string signerRole = EnsureUserPermissionInTransaction(\n                    conn, tx, effectiveReviewedBy, \"CanReviewResults\", \"review EM results\");\n                EnsureEmSourcePlanInTransaction(conn, tx, eventId, AppConfig.IsProduction);\n\n                string lockedStatus = GetLockedEMWorkflowStatusInTransaction(conn, tx, eventId);\n                if (!lockedStatus.Equals(\"Under Review\", StringComparison.OrdinalIgnoreCase))\n                {\n                    throw new InvalidOperationException(\n                        \"The EM event status changed while the electronic signature was being completed. \" +\n                        \"Review was cancelled. Current status: \" + lockedStatus + \".\");\n                }\n\n                bool adminOverride = AppConfig.DevelopmentAdminFullPermissions && IsAdministrativeRole(signerRole);\n                if (!adminOverride &&\n                    HasEmSignatureInTransaction(\n                        conn,\n                        tx,\n                        eventId,\n                        effectiveReviewedBy,\n                        \"EM Result Entry\",\n                        \"EM Submit for Review\"))\n                {\n                    throw new InvalidOperationException(\n                        \"The user who entered or submitted these EM results cannot perform the independent review.\");\n                }\n\n                ResultSnapshotGuard.EnsureMatches(expectedEvidence,\n                    ReadEmPlateSnapshotInTransaction(conn, tx, eventId), \"Id\", \"EventId\", eventId);\n\n                int updated = ExecuteNonQueryWithTransaction(@\"\n                    UPDATE dbo.EM_Events\n                    SET WorkflowStatus = 'Reviewed',\n                        ReviewedBy = @reviewedBy,\n                        ReviewedDate = GETDATE()\n                    WHERE Id = @eventId\n                      AND ISNULL(NULLIF(LTRIM(RTRIM(WorkflowStatus)), N''),\n                                 ISNULL(NULLIF(LTRIM(RTRIM(FinalResult)), N''), N'Pending')) = N'Under Review'\",\n                    new[]\n                    {\n                        new SqlParameter(\"@eventId\", eventId),\n                        new SqlParameter(\"@reviewedBy\", effectiveReviewedBy)\n                    }, conn, tx);\n\n                if (updated != 1)\n                {\n                    throw new InvalidOperationException(\n                        \"The EM event changed during review. No workflow change was committed.\");\n                }\n\n                ExecuteNonQueryWithTransaction(@\"\n                    INSERT INTO dbo.EM_EventSignatures\n                    (\n                        EventID,\n                        EventNo,\n                        ActionType,\n                        ActionReason,\n                        SignedBy,\n                        UserRole,\n                        MeaningOfSignature,\n                        SignedAt\n                    )\n                    VALUES\n                    (\n                        @eventId,\n                        @eventNo,\n                        'EM Review',\n                        @reason,\n                        @signedBy,\n                        @userRole,\n                        @meaning,\n                        GETDATE()\n                    )\",\n                    new[]\n                    {\n                        new SqlParameter(\"@eventId\", eventId),\n                        new SqlParameter(\"@eventNo\", string.IsNullOrWhiteSpace(eventNo) ? (object)DBNull.Value : eventNo),\n                        new SqlParameter(\"@reason\", string.IsNullOrWhiteSpace(reason) ? (object)DBNull.Value : reason),\n                        new SqlParameter(\"@signedBy\", effectiveReviewedBy),\n                        new SqlParameter(\"@userRole\", signerRole),\n                        new SqlParameter(\"@meaning\", string.IsNullOrWhiteSpace(meaningOfSignature) ? (object)DBNull.Value : meaningOfSignature)\n                    }, conn, tx);\n\n                AddAuditTrailAdvanced(\n                    conn, tx,\n                    \"EM_Events\", eventId, \"EM Review\", lockedStatus, \"Reviewed\",\n                    reason, effectiveReviewedBy,\n                    \"WorkflowStatus\", null, eventNo, \"Environmental Monitoring\");\n\n                currentStatus = lockedStatus;\n            });\n        }";
public static void ReviewEMEvent(
            int eventId,
            string eventNo,
            string reviewedBy,
            string meaningOfSignature,
            string reason, DataTable expectedEvidence)
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
                EnsureEmSourcePlanInTransaction(conn, tx, eventId, AppConfig.IsProduction);

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

                ResultSnapshotGuard.EnsureMatches(expectedEvidence,
                    ReadEmPlateSnapshotInTransaction(conn, tx, eventId), "Id", "EventId", eventId);

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
        internal const string ApproveEMEventSource = "public static void ApproveEMEvent(\n            int eventId,\n            string eventNo,\n            string approvedBy,\n            string meaningOfSignature,\n            string reason, DataTable expectedEvidence)\n        {\n            EnsureEMWorkflowObjects();\n            string effectiveApprovedBy = ResolveAuthenticatedSigner(approvedBy);\n            string currentStatus = GetEMWorkflowStatus(eventId);\n\n            if (!currentStatus.Equals(\"Reviewed\", StringComparison.OrdinalIgnoreCase))\n                throw new InvalidOperationException(\"Only reviewed EM events can be approved.\");\n\n            int linkedPlanId = 0;\n\n            ExecuteInTransaction((conn, tx) =>\n            {\n                string signerRole = EnsureQaApprovalAuthorizationInTransaction(\n                    conn, tx, effectiveApprovedBy, \"approve EM results\");\n                linkedPlanId = EnsureEmSourcePlanInTransaction(conn, tx, eventId, AppConfig.IsProduction);\n\n                string lockedStatus = GetLockedEMWorkflowStatusInTransaction(conn, tx, eventId);\n                if (!lockedStatus.Equals(\"Reviewed\", StringComparison.OrdinalIgnoreCase))\n                {\n                    throw new InvalidOperationException(\n                        \"The EM event status changed while the electronic signature was being completed. \" +\n                        \"Approval was cancelled. Current status: \" + lockedStatus + \".\");\n                }\n\n                bool adminOverride = AppConfig.DevelopmentAdminFullPermissions && IsAdministrativeRole(signerRole);\n                if (!adminOverride &&\n                    HasEmSignatureInTransaction(\n                        conn,\n                        tx,\n                        eventId,\n                        effectiveApprovedBy,\n                        \"EM Result Entry\",\n                        \"EM Submit for Review\",\n                        \"EM Review\"))\n                {\n                    throw new InvalidOperationException(\n                        \"The result entrant, submitter, or reviewer cannot perform QA approval for the same EM event.\");\n                }\n\n                EnsureEmApprovalQualityEventGateInTransaction(conn, tx, eventId, eventNo);\n\n                // Apply the exact frozen-evidence consistency gate used by\n                // EM report printing BEFORE approving any plate/event. Read\n                // under the existing signing transaction and row locks; an\n                // inconsistency rolls back the entire approval and signature.\n                // Historical approved records are not modified here.\n                DataTable approvalEvidence = ReadEmPlateSnapshotInTransaction(conn, tx, eventId);\n                ResultSnapshotGuard.EnsureMatches(expectedEvidence, approvalEvidence, \"Id\", \"EventId\", eventId);\n                EmReportEvidenceGuard.EnsureConsistent(approvalEvidence);\n\n                int updated = ExecuteNonQueryWithTransaction(@\"\n                    UPDATE dbo.EM_Events\n                    SET WorkflowStatus = 'Approved',\n                        ApprovedBy = @approvedBy,\n                        ApprovedDate = GETDATE()\n                    WHERE Id = @eventId\n                      AND ISNULL(NULLIF(LTRIM(RTRIM(WorkflowStatus)), N''),\n                                 ISNULL(NULLIF(LTRIM(RTRIM(FinalResult)), N''), N'Pending')) = N'Reviewed'\",\n                    new[]\n                    {\n                        new SqlParameter(\"@eventId\", eventId),\n                        new SqlParameter(\"@approvedBy\", effectiveApprovedBy)\n                    }, conn, tx);\n\n                if (updated != 1)\n                {\n                    throw new InvalidOperationException(\n                        \"The EM event changed during approval. No workflow change was committed.\");\n                }\n\n                ExecuteNonQueryWithTransaction(@\"\n                    INSERT INTO dbo.EM_EventSignatures\n                    (\n                        EventID,\n                        EventNo,\n                        ActionType,\n                        ActionReason,\n                        SignedBy,\n                        UserRole,\n                        MeaningOfSignature,\n                        SignedAt\n                    )\n                    VALUES\n                    (\n                        @eventId,\n                        @eventNo,\n                        'EM Approval',\n                        @reason,\n                        @signedBy,\n                        @userRole,\n                        @meaning,\n                        GETDATE()\n                    )\",\n                    new[]\n                    {\n                        new SqlParameter(\"@eventId\", eventId),\n                        new SqlParameter(\"@eventNo\", string.IsNullOrWhiteSpace(eventNo) ? (object)DBNull.Value : eventNo),\n                        new SqlParameter(\"@reason\", string.IsNullOrWhiteSpace(reason) ? (object)DBNull.Value : reason),\n                        new SqlParameter(\"@signedBy\", effectiveApprovedBy),\n                        new SqlParameter(\"@userRole\", signerRole),\n                        new SqlParameter(\"@meaning\", string.IsNullOrWhiteSpace(meaningOfSignature) ? (object)DBNull.Value : meaningOfSignature)\n                    }, conn, tx);\n\n                if (linkedPlanId > 0)\n                {\n                    string planStatusBefore;\n                    using (SqlCommand statusBeforeCommand = new SqlCommand(\n                        \"SELECT ISNULL(Status,N'') FROM dbo.EM_Plans WITH (UPDLOCK,HOLDLOCK) WHERE PlanID=@planId;\", conn, tx))\n                    {\n                        statusBeforeCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;\n                        statusBeforeCommand.Parameters.Add(\"@planId\", SqlDbType.Int).Value = linkedPlanId;\n                        object value = statusBeforeCommand.ExecuteScalar();\n                        planStatusBefore = value == null || value == DBNull.Value\n                            ? string.Empty\n                            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;\n                    }\n\n                    ExecuteNonQueryWithTransaction(@\"\nIF EXISTS (SELECT 1 FROM dbo.EM_Events WHERE PlanID=@planId)\n   AND NOT EXISTS\n   (\n       SELECT 1\n       FROM dbo.EM_Events\n       WHERE PlanID=@planId\n         AND ISNULL(WorkflowStatus,N'Pending')<>N'Approved'\n   )\n   AND EXISTS\n   (\n       SELECT 1 FROM dbo.EM_Plans\n       WHERE PlanID=@planId AND Status=N'Ready for Results'\n   )\nBEGIN\n    UPDATE dbo.EM_Plans\n    SET Status=N'Completed', CompletedBy=@approvedBy, CompletedAt=SYSDATETIME()\n    WHERE PlanID=@planId;\n\n    UPDATE dbo.EM_PlanSamples\n    SET Status=N'Completed'\n    WHERE PlanID=@planId AND Status IN (N'Ready for Results',N'Control Passed');\n\n    IF OBJECT_ID(N'dbo.EM_PlanSignatures',N'U') IS NOT NULL\n    BEGIN\n        INSERT dbo.EM_PlanSignatures\n        (PlanID,ActionType,ActionReason,SignedBy,UserRole,MeaningOfSignature,SignedAt)\n        VALUES\n        (@planId,N'Plan Completed',@reason,@approvedBy,@userRole,\n         N'All linked EM events reviewed and approved',SYSDATETIME());\n    END;\nEND;\",\n                        new[]\n                        {\n                            new SqlParameter(\"@planId\", linkedPlanId),\n                            new SqlParameter(\"@approvedBy\", effectiveApprovedBy),\n                            new SqlParameter(\"@reason\", reason ?? string.Empty),\n                            new SqlParameter(\"@userRole\", signerRole)\n                        }, conn, tx);\n\n                    string planStatusAfter;\n                    using (SqlCommand statusAfterCommand = new SqlCommand(\n                        \"SELECT ISNULL(Status,N'') FROM dbo.EM_Plans WHERE PlanID=@planId;\", conn, tx))\n                    {\n                        statusAfterCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;\n                        statusAfterCommand.Parameters.Add(\"@planId\", SqlDbType.Int).Value = linkedPlanId;\n                        object value = statusAfterCommand.ExecuteScalar();\n                        planStatusAfter = value == null || value == DBNull.Value\n                            ? string.Empty\n                            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;\n                    }\n\n                    if (!planStatusBefore.Equals(\"Completed\", StringComparison.OrdinalIgnoreCase) &&\n                        planStatusAfter.Equals(\"Completed\", StringComparison.OrdinalIgnoreCase))\n                    {\n                        AddAuditTrailAdvanced(\n                            conn, tx,\n                            \"EM_Plans\", linkedPlanId, \"EM Plan Completed\",\n                            planStatusBefore, \"Completed\",\n                            \"All linked EM events reviewed and approved. \" + reason,\n                            effectiveApprovedBy,\n                            \"Status\", null, null, \"Environmental Monitoring\");\n                    }\n                }\n\n                AddAuditTrailAdvanced(\n                    conn, tx,\n                    \"EM_Events\", eventId, \"EM Approval\", lockedStatus, \"Approved\",\n                    reason, effectiveApprovedBy,\n                    \"WorkflowStatus\", null, eventNo, \"Environmental Monitoring\");\n\n                currentStatus = lockedStatus;\n            });\n        }";
public static void ApproveEMEvent(
            int eventId,
            string eventNo,
            string approvedBy,
            string meaningOfSignature,
            string reason, DataTable expectedEvidence)
        {
            EnsureEMWorkflowObjects();
            string effectiveApprovedBy = ResolveAuthenticatedSigner(approvedBy);
            string currentStatus = GetEMWorkflowStatus(eventId);

            if (!currentStatus.Equals("Reviewed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only reviewed EM events can be approved.");

            int linkedPlanId = 0;

            ExecuteInTransaction((conn, tx) =>
            {
                string signerRole = EnsureQaApprovalAuthorizationInTransaction(
                    conn, tx, effectiveApprovedBy, "approve EM results");
                linkedPlanId = EnsureEmSourcePlanInTransaction(conn, tx, eventId, AppConfig.IsProduction);

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

                // Apply the exact frozen-evidence consistency gate used by
                // EM report printing BEFORE approving any plate/event. Read
                // under the existing signing transaction and row locks; an
                // inconsistency rolls back the entire approval and signature.
                // Historical approved records are not modified here.
                DataTable approvalEvidence = ReadEmPlateSnapshotInTransaction(conn, tx, eventId);
                ResultSnapshotGuard.EnsureMatches(expectedEvidence, approvalEvidence, "Id", "EventId", eventId);
                EmReportEvidenceGuard.EnsureConsistent(approvalEvidence);

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
    private static class SiteSnapshot
    {
        private static class AppConfig { internal static int CommandTimeoutSeconds => 15; }
internal static string GetEmPlateSnapshotSql(bool forUpdate)
    {
        string hint=forUpdate?" WITH(UPDLOCK,HOLDLOCK)":"";
        return @"SELECT P.Id,P.EventId,P.Method,
CASE WHEN P.Method=N'Personnel Monitoring' AND NULLIF(LTRIM(RTRIM(E.EmployeeId)),N'') IS NULL AND ps.EmployeeID IS NOT NULL
THEN CONCAT(P.PlateCode,N' [',ps.EmployeeID,N' - ',ps.EmployeeName,N']') ELSE P.PlateCode END AS PlateCode,P.SequenceNo,P.TotalCount,P.ColoniesObserved,P.ResultCFU,P.Status,P.FungalCount,P.CorrectedCount,
P.ResultRowVersion,P.ResultCalculationVersion,E.ResultRowVersion AS EventRowVersion,E.WorkflowStatus,E.FinalResult,
EVID.AlertLimitSnapshot,EVID.ActionLimitSnapshot,EVID.ResultUnitSnapshot,EVID.AirVolumeLitersSnapshot,EVID.NativeSnapshotComplete,EVID.ReconciliationID,EVID.EvidenceSource,EVID.EvidenceComplete,
COALESCE(NULLIF(LTRIM(RTRIM(E.GradeSnapshot)),N''),A.Grade) AS Grade,
(SELECT u.EquipmentID FROM dbo.LabEquipmentUsage u"+hint+@" WHERE u.Module=N'EM' AND u.ParentRecordID=P.EventId AND u.ResultRecordID=P.Id) AS EquipmentID,
(SELECT MAX(h.HistoryID) FROM dbo.LabEquipmentUsageHistory h"+hint+@" WHERE h.Module=N'EM' AND h.ParentRecordID=P.EventId AND h.ResultRecordID=P.Id) AS EquipmentHistoryID
FROM dbo.EM_EventPlates P"+hint+@" JOIN dbo.EM_Events E"+hint+@" ON E.Id=P.EventId JOIN dbo.EM_Areas A ON A.Id=E.AreaId LEFT JOIN dbo.EM_PlanSamples ps ON ps.PlanSampleID=P.PlanSampleID
"+EmLimitEvidenceSql.Joins(forUpdate)+@" WHERE P.EventId=@eventId ORDER BY P.Id;";
    }
internal static DataTable ReadEmPlateSnapshotInTransaction(SqlConnection c,SqlTransaction t,int eventId)
    {
        using var cmd = new SqlCommand(GetEmPlateSnapshotSql(true), c, t)
        {
            CommandTimeout = AppConfig.CommandTimeoutSeconds
        };
        cmd.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        // Multiple plates share the parent event's ResultRowVersion. Loading the
        // joined reader via DataTable.Load infers a false uniqueness constraint
        // on that repeated event value. The explicit snapshot guards validate
        // plate identity and concurrency; never infer a key from this join.
        using var adapter = new SqlDataAdapter(cmd)
        {
            MissingSchemaAction = MissingSchemaAction.Add
        };
        var table = new DataTable();
        adapter.Fill(table);
        return table;
    }
    }
    private const string UiCaptureSource = "private DataTable CaptureEmWorkflowEvidence(int eventId)\n        {\n            CommitEmResultGridEdits();\n            DataTable original = _loadedPlateSnapshot?.Copy()\n                ?? throw new InvalidOperationException(\"Reload the EM event before signing its review or approval.\");\n            var visible = plateItems.Select(item => (item.PlateId, item.TotalCount, item.Remarks)).ToArray();\n            var equipment = plateItems.ToDictionary(item => item.PlateId, item => item.EquipmentID);\n            EnsureEmVisibleEvidenceSaved(original, original, eventId, visible, equipment);\n            return original;\n        }";
    private sealed class UiCaptureAdapter
    {

        internal DataTable? _loadedPlateSnapshot;
        internal List<VisiblePlate> plateItems=new();
        internal bool InvalidGrid;
        private void CommitEmResultGridEdits(){if(InvalidGrid)throw new InvalidOperationException("Invalid grid cell.");}
        private static void EnsureEmVisibleEvidenceSaved(DataTable loaded,DataTable locked,int id,IEnumerable<(int PlateId,int? Count,string Remarks)> visible,IReadOnlyDictionary<int,int?> equipment)
        {EmQualityEventEvidenceGuard.EnsureSaved(loaded,locked,id,visible);EmQualityEventEvidenceGuard.EnsureEquipmentSaved(locked,equipment);}
        internal DataTable Capture(int id)=>CaptureEmWorkflowEvidence(id);
private DataTable CaptureEmWorkflowEvidence(int eventId)
        {
            CommitEmResultGridEdits();
            DataTable original = _loadedPlateSnapshot?.Copy()
                ?? throw new InvalidOperationException("Reload the EM event before signing its review or approval.");
            var visible = plateItems.Select(item => (item.PlateId, item.TotalCount, item.Remarks)).ToArray();
            var equipment = plateItems.ToDictionary(item => item.PlateId, item => item.EquipmentID);
            EnsureEmVisibleEvidenceSaved(original, original, eventId, visible, equipment);
            return original;
        }
    }
}
