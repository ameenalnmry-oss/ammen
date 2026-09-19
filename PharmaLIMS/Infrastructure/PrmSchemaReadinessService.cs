using System;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace PharmaLIMS.Infrastructure
{
    public sealed class PrmSchemaReadinessResult
    {
        private PrmSchemaReadinessResult(bool isReady, string message)
        {
            IsReady = isReady;
            Message = message;
        }

        public bool IsReady { get; }
        public string Message { get; }

        public static PrmSchemaReadinessResult Ready(string message) => new(true, message);
        public static PrmSchemaReadinessResult Blocked(string message) => new(false, message);
    }

    /// <summary>
    /// Read-only PRM schema readiness checks. Operational screens never execute DDL
    /// or database migrations. Development schema changes are performed only from
    /// the explicit, signed Database Maintenance action on the main dashboard.
    /// Production remains fail-closed and requires the approved deployment process.
    /// </summary>
    public static class PrmSchemaReadinessService
    {
        private const int ReadinessCommandTimeoutSeconds = 15;
        private static readonly DatabaseConnection ReadinessDatabase = new DatabaseConnection();

        private enum ReadinessProbeState
        {
            Ready,
            NotReady,
            Busy,
            Failed
        }

        private sealed class ReadinessProbeResult
        {
            public ReadinessProbeState State { get; init; }
            public string Details { get; init; } = string.Empty;
        }

        private const string RegistrationReadinessSql = @"
SELECT CASE WHEN
    (SELECT COUNT(1) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'dbo')
       AND name IN(N'PRM_NumberSequences',N'PRM_Samples',N'PRM_SampleTests',N'PRM_Reports',N'PRM_ReportHistory',N'PRM_SpecificationTests'))=6
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ItemCode') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ProductionStage') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'MinimumElapsedHours') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'SpecificationVersionNo') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'StabilityChamberNo') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'StabilityProtocolNo') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests',N'SourceSpecificationTestID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests',N'SpecificationVersionNo') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests',N'SpecificationItemCode') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests',N'SpecificationProductionStage') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests',N'MinimumElapsedHours') IS NOT NULL
THEN 1 ELSE 0 END;";

        private const string ResultsReadinessSql = @"
SELECT CASE WHEN
    (SELECT COUNT(1) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'dbo') AND name IN
    (
        N'PRM_Samples',N'PRM_SampleTests',N'PRM_SpecificationTests',
        N'PRM_Certificates',N'PRM_CertificateHistory',N'PRM_ElectronicSignatures'
    ))=6
    AND COL_LENGTH(N'dbo.PRM_Samples',N'SpecificationVersionNo') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'AnalysisStartedDate') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'AnalysisCompletedDate') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciliationStatus') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciledBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciledAt') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'StabilityChamberNo') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'StabilityProtocolNo') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests',N'SourceSpecificationTestID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests',N'SpecificationVersionNo') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests',N'MinimumElapsedHours') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ItemCode') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ProductionStage') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'MinimumElapsedHours') IS NOT NULL
THEN 1 ELSE 0 END;";

        private const string QualityEventCompatibilityReadySql = @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.PRM_NumberSequences',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.QualityEvents',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.QualityEventAffectedResults',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.QualityEventActions',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.QualityEventChecklistAnswers',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.QualityEventPrintHistory',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.TR_PRM_QEEvidenceReconciliation_Immutable_20260827_002',N'TR') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_NumberSequences',N'LastUpdated') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'EventNumber') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'EventType') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'Severity') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'SampleID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'SampleNumber') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'SourceModule') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'SourceRecordID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'CurrentStatus') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'DetectedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'DetectedDate') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'DetectionSource') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'InitialDescription') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'ImmediateAction') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'RootCauseCategory') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'RootCauseDetails') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'ImpactAssessment') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'CAPARequired') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'QAConclusion') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'FinalDisposition') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'ClosedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'ClosedDate') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'CreatedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'CreatedDate') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'ModifiedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'ModifiedDate') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'AffectedResultID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'QualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SampleTestID') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'SampleTestID'
          AND system_type_id=TYPE_ID(N'int')
          AND is_nullable=1
          AND is_computed=0
    )
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'TestID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'TestName') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'ResultValue') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'ResultValue'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND (max_length=-1 OR max_length>=400)
          AND is_computed=0
    )
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SpecificationLimit') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SpecificationNumericLimit') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'EvidenceSchemaVersion') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'LegacyQualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReplacementQualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'SampleID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReconciledBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReconciliationReason') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ElectronicSignatureID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReconciliationSchemaVersion') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'SpecificationLimit'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND (max_length=-1 OR max_length>=1000)
          AND is_computed=0
    )
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'Unit') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'FailureType') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'FailureType'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND (max_length=-1 OR max_length>=240)
          AND is_computed=0
    )
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'CreatedDate') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SourceModule') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'SourceModule'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND is_computed=0
    )
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SourceResultID') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'SourceResultID'
          AND system_type_id=TYPE_ID(N'int')
          AND is_nullable=1
          AND is_computed=0
    )
    AND COL_LENGTH(N'dbo.QualityEventActions',N'QualityEventActionID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventActions',N'QualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventActions',N'ActionType') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventActions',N'ActionDescription') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventActions',N'PerformedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventActions',N'PerformedDate') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventActions',N'ElectronicSignatureID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'AnswerID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'QualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'QuestionID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'AnswerValue') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'Comments') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'AnsweredBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'AnsweredDate') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventPrintHistory',N'QualityEventPrintID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventPrintHistory',N'QualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventPrintHistory',N'PrintedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventPrintHistory',N'PrintedDate') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'AffectedResultID'
          AND system_type_id=TYPE_ID(N'int')
          AND is_nullable=0
          AND is_computed=0
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions')
          AND name=N'QualityEventActionID'
          AND system_type_id=TYPE_ID(N'int')
          AND is_nullable=0
          AND is_computed=0
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
          AND name=N'AnswerID'
          AND system_type_id IN(TYPE_ID(N'int'),TYPE_ID(N'bigint'))
          AND is_nullable=0
          AND is_computed=0
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
          AND name=N'QualityEventPrintID'
          AND system_type_id IN(TYPE_ID(N'int'),TYPE_ID(N'bigint'))
          AND is_nullable=0
          AND is_computed=0
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'QualityEventID'
          AND system_type_id=TYPE_ID(N'int')
          AND is_nullable=0
          AND is_computed=0
    )
    AND (
        EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
              AND name=N'QualityEventID' AND is_identity=1
        )
        OR EXISTS
        (
            SELECT 1
            FROM sys.default_constraints d
            INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
            WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEvents')
              AND c.name=N'QualityEventID'
              AND OBJECT_DEFINITION(d.object_id) LIKE N'%NEXT VALUE FOR%'
        )
    )
    AND (
        EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
              AND name=N'AffectedResultID' AND is_identity=1
        )
        OR EXISTS
        (
            SELECT 1
            FROM sys.default_constraints d
            INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
            WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
              AND c.name=N'AffectedResultID'
              AND OBJECT_DEFINITION(d.object_id) LIKE N'%NEXT VALUE FOR%'
        )
    )
    AND (
        EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions')
              AND name=N'QualityEventActionID' AND is_identity=1
        )
        OR EXISTS
        (
            SELECT 1
            FROM sys.default_constraints d
            INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
            WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventActions')
              AND c.name=N'QualityEventActionID'
              AND OBJECT_DEFINITION(d.object_id) LIKE N'%NEXT VALUE FOR%'
        )
    )
    AND (
        EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
              AND name=N'AnswerID' AND is_identity=1
        )
        OR EXISTS
        (
            SELECT 1
            FROM sys.default_constraints d
            INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
            WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
              AND c.name=N'AnswerID'
              AND OBJECT_DEFINITION(d.object_id) LIKE N'%NEXT VALUE FOR%'
        )
    )
    AND (
        EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
              AND name=N'QualityEventPrintID' AND is_identity=1
        )
        OR EXISTS
        (
            SELECT 1
            FROM sys.default_constraints d
            INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
            WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
              AND c.name=N'QualityEventPrintID'
              AND OBJECT_DEFINITION(d.object_id) LIKE N'%NEXT VALUE FOR%'
        )
    )
THEN 1 ELSE 0 END;";


        private const string ChecklistCompleteSql = @"
SELECT CASE WHEN OBJECT_ID(N'dbo.QualityEventChecklistQuestions',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'SectionName') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'QuestionText') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'AppliesToEventType') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'AppliesToSampleType') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'AppliesToTestCategory') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'AppliesToTestNameKeyword') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'AnswerType') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'IsRequired') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'ExpectedAnswer') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'QuestionLogic') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'SortOrder') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'IsActive') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistQuestions')
          AND name=N'QuestionID'
          AND system_type_id=TYPE_ID(N'int')
          AND is_nullable=0
    )
    AND (
        EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistQuestions')
              AND name=N'QuestionID' AND is_identity=1
        )
        OR EXISTS
        (
            SELECT 1
            FROM sys.default_constraints d
            INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
            WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventChecklistQuestions')
              AND c.name=N'QuestionID'
              AND OBJECT_DEFINITION(d.object_id) LIKE N'%NEXT VALUE FOR%'
        )
    )
THEN 1 ELSE 0 END;";

        public static Task<PrmSchemaReadinessResult> EnsureRegistrationReadyAsync() =>
            EnsureReadyAsync(
                "PRM registration",
                RegistrationReadinessSql,
                requiresQualityEventCompatibility: false);

        public static Task<PrmSchemaReadinessResult> EnsureResultsReadyAsync() =>
            EnsureReadyAsync(
                "PRM results",
                ResultsReadinessSql,
                requiresQualityEventCompatibility: false);

        public static Task<PrmSchemaReadinessResult> EnsureQualityEventReadyAsync() =>
            EnsureReadyAsync(
                "PRM Quality Event",
                QualityEventCompatibilityReadySql,
                requiresQualityEventCompatibility: true);

        private static async Task<PrmSchemaReadinessResult> EnsureReadyAsync(
            string moduleName,
            string readinessSql,
            bool requiresQualityEventCompatibility)
        {
            ReadinessProbeResult moduleProbe = await ProbeAsync(readinessSql).ConfigureAwait(false);

            if (moduleProbe.State == ReadinessProbeState.Busy)
            {
                string busyMessage =
                    "The PRM readiness probe could not obtain a timely read because SQL Server is busy or a schema lock is active. " +
                    "No schema change or workflow record change was attempted. Finish Database Maintenance or the blocking database operation, then reopen this module.";
                ApplicationLogger.Warning($"{moduleName} readiness deferred because the database is busy. {moduleProbe.Details}");
                return PrmSchemaReadinessResult.Blocked(busyMessage);
            }

            if (moduleProbe.State == ReadinessProbeState.Failed)
            {
                ApplicationLogger.Error($"{moduleName} read-only schema readiness probe failed without changing the database. {moduleProbe.Details}");
                return PrmSchemaReadinessResult.Blocked(
                    "The PRM database readiness check could not be completed. No schema or workflow record was changed. " +
                    moduleProbe.Details);
            }

            if (moduleProbe.State == ReadinessProbeState.Ready && requiresQualityEventCompatibility)
            {
                ReadinessProbeResult checklistProbe = await ProbeAsync(ChecklistCompleteSql).ConfigureAwait(false);
                if (checklistProbe.State == ReadinessProbeState.Busy)
                {
                    ApplicationLogger.Warning($"{moduleName} checklist readiness deferred because the database is busy. {checklistProbe.Details}");
                    return PrmSchemaReadinessResult.Blocked(
                        "The PRM Quality Event readiness check could not finish because SQL Server is busy or a schema lock is active. " +
                        "No database change was attempted. Finish the blocking database operation and reopen the investigation.");
                }

                if (checklistProbe.State == ReadinessProbeState.Failed)
                {
                    ApplicationLogger.Error($"{moduleName} checklist readiness probe failed without changing the database. {checklistProbe.Details}");
                    return PrmSchemaReadinessResult.Blocked(
                        "The PRM Quality Event checklist readiness check failed. No database change was attempted. " +
                        checklistProbe.Details);
                }

                if (checklistProbe.State != ReadinessProbeState.Ready)
                    moduleProbe = new ReadinessProbeResult { State = ReadinessProbeState.NotReady };
            }

            if (moduleProbe.State == ReadinessProbeState.Ready)
                return PrmSchemaReadinessResult.Ready("The controlled PRM database schema is ready.");

            string message;
            if (AppConfig.IsDevelopment)
            {
                message =
                    "The PRM database schema required by this workflow is incomplete. " +
                    "No schema change or workflow data change was attempted from this screen. " +
                    "Close the PRM workflow window, run the signed Database Maintenance action from the main dashboard once, " +
                    "confirm System Preflight passes, then reopen the module.";
            }
            else
            {
                message =
                    "The controlled PRM database schema is incomplete. No database change was attempted from this workflow. " +
                    "Apply the approved database migration manifest through the controlled deployment process before reopening this module.";
            }

            ApplicationLogger.Warning($"{moduleName} blocked by read-only schema readiness. {message}");
            return PrmSchemaReadinessResult.Blocked(message);
        }

        private static async Task<ReadinessProbeResult> ProbeAsync(string readinessSql)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ReadinessCommandTimeoutSeconds + 2));
            try
            {
                DataTable result = await ReadinessDatabase.ExecuteQueryAsync(
                    readinessSql,
                    parameters: null,
                    commandTimeoutSeconds: ReadinessCommandTimeoutSeconds,
                    cancellationToken: timeout.Token).ConfigureAwait(false);

                bool ready = result.Rows.Count > 0 &&
                             result.Columns.Count > 0 &&
                             result.Rows[0][0] != DBNull.Value &&
                             Convert.ToInt32(result.Rows[0][0], CultureInfo.InvariantCulture) == 1;

                return new ReadinessProbeResult
                {
                    State = ready ? ReadinessProbeState.Ready : ReadinessProbeState.NotReady
                };
            }
            catch (SqlException ex) when (ex.Number == -2 || ex.Number == 1222)
            {
                return new ReadinessProbeResult
                {
                    State = ReadinessProbeState.Busy,
                    Details = $"SQL Server did not complete the read-only readiness probe within {ReadinessCommandTimeoutSeconds} seconds (SQL {ex.Number})."
                };
            }
            catch (OperationCanceledException)
            {
                return new ReadinessProbeResult
                {
                    State = ReadinessProbeState.Busy,
                    Details = $"The read-only readiness probe exceeded its {ReadinessCommandTimeoutSeconds + 2}-second execution window."
                };
            }
            catch (Exception ex)
            {
                return new ReadinessProbeResult
                {
                    State = ReadinessProbeState.Failed,
                    Details = UserFacingError.SafeMessage(ex, "PRM schema readiness")
                };
            }
        }
    }
}
