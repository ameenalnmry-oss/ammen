using Microsoft.Data.SqlClient;
using System.Data;

namespace PharmaLIMS.Interfaces;

public enum CultureMediaQuery
{
    LoadSelectedReleaseReportSummary,
    LoadSelectedReleaseReportTests,
    LoadOrganismsForReport,
    LoadMediaLotReceiptForPrint,
    LoadReleaseReportHeaderForPrint,
    LoadReleaseReportTestsForPrint,
    LoadReleasedMediaLabelData,
    LoadPreparationForPrint,
    LoadPreparedMediaLabelData,
    ResolveMediaLotBySelectedLotId,
    ResolveMediaLotByMediaId,
    ValidateMediaLotForPreparation,
    ValidatePreparationSourceLotForRelease,
    LoadPreparationReleaseGateRecord,
    LoadReceipts,
    LoadReleaseReports,
    LoadPreparations,
    LoadMediaForFilters,
    LoadStoredMediaForPreparation,
    LoadMediaLotsForRelease,
    LoadGptHistory,
    LoadDashboardSummary,
    LoadMediaLotForStockReconciliation,
    LoadQualificationTimingRequirements,
    LoadQualificationWorkflowRecord,
    LoadQualificationTests,
    LoadMediaLotQualificationValidation,
    LoadApplicableQualificationRequirements,
}

public enum CultureMediaScalar
{
    PreparationPreparedBy,
    HasSignedPreparationControls,
    HasVisualCheckSignature,
    HasSterilityReviewSignature,
    PreparationReleaseStatus,
    LoadSopField,
    CultureMediaSopSchemaReady,
    FindMediaMasterId,
    MediaLotReceiptStatus,
    HasActiveQualificationForLot,
}

public enum CultureMediaCommand
{
    InsertReleaseOrganism,
    SaveSopField,
    RecordCultureMediaPrint,
    AddCultureMediaSignature,
}

public interface ICultureMediaRepository
{
    DataTable Load(CultureMediaQuery operation, params SqlParameter[] parameters);
    object? ReadScalar(CultureMediaScalar operation, params SqlParameter[] parameters);
    int RunCommand(CultureMediaCommand operation, params SqlParameter[] parameters);
    string GenerateNumber(string sequenceName, string prefix);
    int FindExistingMediaLotId(int mediaId, string lotNumber);
}
