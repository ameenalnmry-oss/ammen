using System;
using PharmaLIMS.TrustedSigning;
namespace PharmaLIMS.Services;
// Never a pipe input. Only the host builds these from the owner-locked snapshot.
public sealed record WaterWorkflowMutationPlan(int Version, string Operation, int SampleId,
    string ExpectedStatus, string ExpectedSnapshot, string Status, DateTime? AnalysisStartedAt,
    WaterWorkflowTimes? Times, bool MarkAnalysisCompleted, WaterWorkflowRowPlan[] Rows);
public sealed record WaterWorkflowRowPlan(int SampleTestId, bool WriteResult, string? ResultValue,
    string? Remarks, string ResultStatus, string? DeviationType, bool WriteEvidence,
    int? EquipmentId, int[] SupportingEquipmentIds, string? TestKitCode, string? TestKitLot,
    DateTime? TestKitExpiry, string RawResultSnapshot, WaterWorkflowExecutionPlan? Execution,
    string ResultRowVersion);
public sealed record WaterWorkflowExecutionPlan(string ProcedureReference, string MethodGuidanceSnapshot,
    string? SampleTemperatureC, string VerificationReference, bool VerificationConfirmed, string ExecutionRemarks);
