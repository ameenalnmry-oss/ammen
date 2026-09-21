namespace PharmaLIMS.Infrastructure
{
    /// <summary>
    /// Authoritative database-boundary contract for compliance records that must remain immutable
    /// after insertion. System Preflight and the SQL integration suite intentionally consume the
    /// same query so a missing table or decoy/irrelevant trigger cannot satisfy the release gate. Structural checks are complemented by behavioral SQL integration tests that prove UPDATE/DELETE are rejected.
    /// </summary>
    public static class ComplianceRecordProtectionContract
    {
        public const string QuerySql = @"
DECLARE @Protected TABLE
(
    TableName sysname NOT NULL PRIMARY KEY,
    TriggerName sysname NOT NULL,
    BodyToken nvarchar(200) NOT NULL
);

INSERT INTO @Protected(TableName, TriggerName, BodyToken)
VALUES
    (N'AuditTrail', N'TRG_AuditTrail_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'CertificateDocumentSnapshots', N'TRG_CertificateDocumentSnapshots_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'CertificateLifecycleAudit', N'TRG_CertificateLifecycleAudit_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'CertificatePrintHistory', N'TRG_CertificatePrintHistory_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'CultureMediaPrintHistory', N'TRG_CultureMediaPrintHistory_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'CultureMediaSignatures', N'TRG_CultureMediaSignatures_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'ElectronicSignatures', N'TRG_ElectronicSignatures_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'UserAdministrationSignatures', N'TRG_UserAdministrationSignatures_AppendOnly', N'user administration signature evidence is append-only and cannot be updated or deleted'),
    (N'EM_EventSignatures', N'TRG_EM_EventSignatures_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'EM_PlanSignatures', N'TRG_EM_PlanSignatures_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'EM_ScheduleSignatures', N'TRG_EM_ScheduleSignatures_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'PRM_CertificateSnapshots', N'TRG_PRM_CertificateSnapshots_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'PRM_CertificateHistory', N'TRG_PRM_CertificateHistory_AppendOnly_20260921', N'PRM certificate lifecycle history is append-only and cannot be updated or deleted'),
    (N'PRM_ElectronicSignatures', N'TRG_PRM_ElectronicSignatures_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'PRM_SpecificationSignatures', N'TRG_PRM_SpecificationSignatures_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'QualityEventPrintHistory', N'TRG_QualityEventPrintHistory_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'QualityEventSignatures', N'TRG_QualityEventSignatures_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'Water_PlanSignatures', N'TRG_Water_PlanSignatures_AppendOnly', N'append-only and cannot be updated or deleted'),
    (N'QualityEventInvestigationEvidenceHistory', N'TR_QEInvestigationEvidenceHistory_AppendOnly_20260828_001', N'structured-evidence history is append-only'),
    (N'EM_SchedulePointSnapshots', N'TRG_EM_SchedulePointSnapshots_AppendOnly_20260828', N'Approved EM schedule point snapshots are immutable'),
    (N'EM_LimitSnapshotReconciliations', N'TRG_EM_LimitSnapshotReconciliations_AppendOnly_20260828', N'historical limit reconciliations are append-only'),
    (N'PRM_TimingMigrationHistory', N'TR_PRM_TimingMigrationHistory_AppendOnly_20260906', N'timing migration history is append-only'),
    (N'PRM_TimingMigrationTestEvidence', N'TR_PRM_TimingMigrationTestEvidence_AppendOnly_20260906', N'timing migration test evidence is append-only'),
    (N'PRM_SpecificationTimingReapprovalHistory', N'TR_PRM_SpecTimingReapprovalHistory_AppendOnly_20260906', N'specification timing reapproval history is append-only'),
    (N'MediaQualificationRequirementSnapshots', N'TR_MediaQualificationRequirementSnapshots_AppendOnly_20260906', N'qualification requirement snapshots are append-only'),
    (N'PRM_TimingGovernanceMigrationState', N'TR_PRM_TimingGovernanceMigrationState_AppendOnly_20260906', N'timing governance migration state is append-only'),
    (N'PRM_TimingQELegacyLinkCorrections', N'TR_PRM_TimingQELegacyLinkCorrections_AppendOnly_20260906', N'Quality Event legacy-link correction history is append-only'),
    (N'WaterTestProfileSignatures', N'TRG_WaterTestProfileSignatures_AppendOnly', N'signature evidence is append-only'),
    (N'LegacyCertificateEvidenceReconciliations', N'TRG_LegacyCertificateEvidenceReconciliations_AppendOnly_20260908', N'reconciliation evidence is append-only');

SELECT
    protected.TableName,
    protected.TriggerName,
    CASE WHEN tableRow.object_id IS NOT NULL THEN 1 ELSE 0 END AS TableExists,
    CASE WHEN triggerRow.object_id IS NOT NULL AND triggerRow.is_disabled = 0 THEN 1 ELSE 0 END AS HasExpectedEnabledTrigger,
    CASE WHEN EXISTS
    (
        SELECT 1
        FROM sys.trigger_events ev
        WHERE ev.object_id = triggerRow.object_id
          AND ev.type_desc = N'UPDATE'
    ) THEN 1 ELSE 0 END AS ProtectsUpdate,
    CASE WHEN EXISTS
    (
        SELECT 1
        FROM sys.trigger_events ev
        WHERE ev.object_id = triggerRow.object_id
          AND ev.type_desc = N'DELETE'
    ) THEN 1 ELSE 0 END AS ProtectsDelete,
    CASE WHEN LOWER(ISNULL(module.definition, N'')) LIKE N'%throw%'
               AND LOWER(ISNULL(module.definition, N'')) LIKE N'%' + LOWER(protected.BodyToken) + N'%'
         THEN 1 ELSE 0 END AS HasCanonicalAppendOnlyBody
FROM @Protected protected
LEFT JOIN sys.tables tableRow
  ON tableRow.schema_id = SCHEMA_ID(N'dbo')
 AND tableRow.name = protected.TableName
LEFT JOIN sys.triggers triggerRow
  ON triggerRow.parent_id = tableRow.object_id
 AND triggerRow.name = protected.TriggerName
LEFT JOIN sys.sql_modules module
  ON module.object_id = triggerRow.object_id
ORDER BY protected.TableName;";
    }
}
