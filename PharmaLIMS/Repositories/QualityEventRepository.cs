using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Interfaces;
using PharmaLIMS.Models;
using System.Data;

namespace PharmaLIMS.Repositories
{
    public class QualityEventRepository : IRepository<QualityEvent, int>
    {
        private readonly DatabaseConnection _connection;

        public QualityEventRepository(DatabaseConnection connection)
        {
            _connection = connection;
        }

        public async Task<QualityEvent?> GetByIdAsync(int id)
        {
            const string query = @"SELECT QualityEventID, EventNumber, EventType, Severity, SampleID, SampleNumber,
                    CurrentStatus, DetectedBy, DetectedDate, DetectionSource,
                    InitialDescription, ImmediateAction, RootCauseCategory, RootCauseDetails,
                    ImpactAssessment, CAPARequired, QAConclusion, FinalDisposition,
                    ClosedBy, ClosedDate, CreatedDate, ModifiedBy, ModifiedDate FROM QualityEvents WHERE QualityEventID = @QualityEventId";
            var parameters = new[] { new SqlParameter("@QualityEventId", id) };
            var dt = await _connection.ExecuteQueryAsync(query, parameters);

            if (dt.Rows.Count == 0)
                return null;

            var qualityEvent = MapToQualityEvent(dt.Rows[0]);
            qualityEvent.AffectedResults = await GetAffectedResultsByEventIdAsync(id);
            qualityEvent.Actions = await GetActionsByEventIdAsync(id);

            return qualityEvent;
        }

        public async Task<QualityEvent?> GetOpenQualityEventBySampleIdAsync(int sampleId)
        {
            const string query = @"
                SELECT TOP 1 QualityEventID, EventNumber, EventType, Severity, SampleID, SampleNumber,
                    CurrentStatus, DetectedBy, DetectedDate, DetectionSource,
                    InitialDescription, ImmediateAction, RootCauseCategory, RootCauseDetails,
                    ImpactAssessment, CAPARequired, QAConclusion, FinalDisposition,
                    ClosedBy, ClosedDate, CreatedDate, ModifiedBy, ModifiedDate
                FROM QualityEvents
                WHERE SampleID = @SampleId
                  AND CurrentStatus NOT IN ('Closed', 'QA Closed', 'Cancelled', 'Rejected Closed')
                ORDER BY QualityEventID DESC";

            var parameters = new[] { new SqlParameter("@SampleId", sampleId) };
            var dt = await _connection.ExecuteQueryAsync(query, parameters);

            if (dt.Rows.Count == 0)
                return null;

            var qualityEvent = MapToQualityEvent(dt.Rows[0]);
            qualityEvent.AffectedResults = await GetAffectedResultsByEventIdAsync(qualityEvent.QualityEventId);
            qualityEvent.Actions = await GetActionsByEventIdAsync(qualityEvent.QualityEventId);

            return qualityEvent;
        }

        public async Task<List<QualityEventAffectedResult>> GetAffectedResultsByEventIdAsync(int qualityEventId)
        {
            const string query = @"SELECT AffectedResultID, QualityEventID, SampleTestID, SourceModule, SourceResultID, TestID, TestName, ResultValue, SpecificationLimit, Unit, FailureType, CreatedDate FROM QualityEventAffectedResults WHERE QualityEventID = @QualityEventId ORDER BY AffectedResultID";
            var parameters = new[] { new SqlParameter("@QualityEventId", qualityEventId) };
            var dt = await _connection.ExecuteQueryAsync(query, parameters);
            var results = new List<QualityEventAffectedResult>();

            foreach (DataRow row in dt.Rows)
            {
                results.Add(new QualityEventAffectedResult
                {
                    AffectedResultId = row.GetSafeInt("AffectedResultID"),
                    QualityEventId = row.GetSafeInt("QualityEventID"),
                    SampleTestId = row["SampleTestID"] != DBNull.Value ? (int?)Convert.ToInt32(row["SampleTestID"]) : null,
                    SourceModule = row.GetSafeString("SourceModule"),
                    SourceResultId = row["SourceResultID"] != DBNull.Value ? (int?)Convert.ToInt32(row["SourceResultID"]) : null,
                    TestId = row["TestID"] != DBNull.Value ? (int?)Convert.ToInt32(row["TestID"]) : null,
                    TestName = row.GetSafeString("TestName"),
                    ResultValue = row.GetSafeString("ResultValue"),
                    SpecificationLimit = row.GetSafeString("SpecificationLimit"),
                    Unit = row.GetSafeString("Unit"),
                    FailureType = row.GetSafeString("FailureType"),
                    CreatedDate = row.GetSafeDateTime("CreatedDate") ?? DateTime.Now
                });
            }

            return results;
        }

        public async Task<List<QualityEventAction>> GetActionsByEventIdAsync(int qualityEventId)
        {
            const string query = @"SELECT QualityEventActionID, QualityEventID, ActionType, ActionDescription, PerformedBy, PerformedDate, ElectronicSignatureID FROM QualityEventActions WHERE QualityEventID = @QualityEventId ORDER BY QualityEventActionID";
            var parameters = new[] { new SqlParameter("@QualityEventId", qualityEventId) };
            var dt = await _connection.ExecuteQueryAsync(query, parameters);
            var actions = new List<QualityEventAction>();

            foreach (DataRow row in dt.Rows)
            {
                actions.Add(new QualityEventAction
                {
                    QualityEventActionId = row.GetSafeInt("QualityEventActionID"),
                    QualityEventId = row.GetSafeInt("QualityEventID"),
                    ActionType = row.GetSafeString("ActionType"),
                    ActionDescription = row.GetSafeString("ActionDescription"),
                    PerformedBy = row.GetSafeString("PerformedBy"),
                    PerformedDate = row.GetSafeDateTime("PerformedDate") ?? DateTime.Now,
                    ElectronicSignatureId = row["ElectronicSignatureID"] != DBNull.Value ? (int?)Convert.ToInt32(row["ElectronicSignatureID"]) : null
                });
            }

            return actions;
        }

        public async Task<int> AddAffectedResultAsync(QualityEventAffectedResult result)
        {
            const string query = @"
                INSERT INTO QualityEventAffectedResults (
                    QualityEventID, SampleTestID, SourceModule, SourceResultID, TestID, TestName,
                    ResultValue, SpecificationLimit, Unit, FailureType, CreatedDate
                )
                OUTPUT INSERTED.AffectedResultID
                VALUES (
                    @QualityEventId, @SampleTestId, @SourceModule, @SourceResultId, @TestId, @TestName,
                    @ResultValue, @SpecificationLimit, @Unit, @FailureType, GETDATE()
                );";

            var parameters = new[]
            {
                new SqlParameter("@QualityEventId", SqlDbType.Int) { Value = result.QualityEventId },
                new SqlParameter("@SampleTestId", SqlDbType.Int) { Value = result.SampleTestId ?? (object)DBNull.Value },
                new SqlParameter("@SourceModule", SqlDbType.NVarChar, 80) { Value = result.SourceModule ?? (object)DBNull.Value },
                new SqlParameter("@SourceResultId", SqlDbType.Int) { Value = result.SourceResultId ?? (object)DBNull.Value },
                new SqlParameter("@TestId", SqlDbType.Int) { Value = result.TestId ?? (object)DBNull.Value },
                new SqlParameter("@TestName", SqlDbType.NVarChar, 200) { Value = result.TestName ?? (object)DBNull.Value },
                new SqlParameter("@ResultValue", SqlDbType.NVarChar, 200) { Value = result.ResultValue ?? (object)DBNull.Value },
                new SqlParameter("@SpecificationLimit", SqlDbType.NVarChar, 500) { Value = result.SpecificationLimit ?? (object)DBNull.Value },
                new SqlParameter("@Unit", SqlDbType.NVarChar, 50) { Value = result.Unit ?? (object)DBNull.Value },
                new SqlParameter("@FailureType", SqlDbType.NVarChar, 120) { Value = result.FailureType ?? (object)DBNull.Value }
            };

            return await _connection.ExecuteScalarAsync<int>(query, parameters);
        }

        public async Task<int> AddActionAsync(QualityEventAction action)
        {
            const string query = @"
                INSERT INTO QualityEventActions (
                    QualityEventID, ActionType, ActionDescription,
                    PerformedBy, PerformedDate, ElectronicSignatureID
                )
                OUTPUT INSERTED.QualityEventActionID
                VALUES (
                    @QualityEventId, @ActionType, @ActionDescription,
                    @PerformedBy, GETDATE(), @ElectronicSignatureId
                );";

            var parameters = new[]
            {
                new SqlParameter("@QualityEventId", action.QualityEventId),
                new SqlParameter("@ActionType", action.ActionType ?? (object)DBNull.Value),
                new SqlParameter("@ActionDescription", action.ActionDescription ?? (object)DBNull.Value),
                new SqlParameter("@PerformedBy", action.PerformedBy ?? (object)DBNull.Value),
                new SqlParameter("@ElectronicSignatureId", action.ElectronicSignatureId ?? (object)DBNull.Value)
            };

            return await _connection.ExecuteScalarAsync<int>(query, parameters);
        }

        public async Task<IEnumerable<QualityEvent>> GetAllAsync()
        {
            const string query = @"SELECT QualityEventID, EventNumber, EventType, Severity, SampleID, SampleNumber,
                    CurrentStatus, DetectedBy, DetectedDate, DetectionSource,
                    InitialDescription, ImmediateAction, RootCauseCategory, RootCauseDetails,
                    ImpactAssessment, CAPARequired, QAConclusion, FinalDisposition,
                    ClosedBy, ClosedDate, CreatedDate, ModifiedBy, ModifiedDate FROM QualityEvents ORDER BY QualityEventID DESC";
            var dt = await _connection.ExecuteQueryAsync(query);
            var events = new List<QualityEvent>();

            foreach (DataRow row in dt.Rows)
                events.Add(MapToQualityEvent(row));

            return events;
        }

        public async Task<int> AddAsync(QualityEvent entity)
        {
            const string query = @"
                INSERT INTO QualityEvents (
                    EventNumber, EventType, Severity, SampleID, SampleNumber,
                    CurrentStatus, DetectedBy, DetectedDate, DetectionSource,
                    InitialDescription, ImmediateAction, CAPARequired,
                    CreatedDate, ModifiedBy, ModifiedDate
                )
                OUTPUT INSERTED.QualityEventID
                VALUES (
                    @EventNumber, @EventType, @Severity, @SampleId, @SampleNumber,
                    @CurrentStatus, @DetectedBy, @DetectedDate, @DetectionSource,
                    @InitialDescription, @ImmediateAction, @CAPARequired,
                    GETDATE(), @ModifiedBy, GETDATE()
                );";

            var parameters = BuildQualityEventParameters(entity);
            return await _connection.ExecuteScalarAsync<int>(query, parameters);
        }

        public async Task<int> UpdateAsync(QualityEvent entity)
        {
            const string query = @"
                UPDATE QualityEvents SET
                    Severity = @Severity,
                    CurrentStatus = @CurrentStatus,
                    InitialDescription = @InitialDescription,
                    ImmediateAction = @ImmediateAction,
                    RootCauseCategory = @RootCauseCategory,
                    RootCauseDetails = @RootCauseDetails,
                    ImpactAssessment = @ImpactAssessment,
                    CAPARequired = @CAPARequired,
                    QAConclusion = @QAConclusion,
                    FinalDisposition = @FinalDisposition,
                    ModifiedBy = @ModifiedBy,
                    ModifiedDate = GETDATE()
                WHERE QualityEventID = @QualityEventId";

            var parameters = BuildQualityEventParameters(entity);
            return await _connection.ExecuteNonQueryAsync(query, parameters);
        }

        public Task<int> DeleteAsync(int id)
        {
            throw new InvalidOperationException(
                "Controlled Quality Event records cannot be physically deleted. Use the approved cancellation or closure workflow so history remains traceable.");
        }

        private SqlParameter[] BuildQualityEventParameters(QualityEvent entity)
        {
            return new[]
            {
                new SqlParameter("@QualityEventId", entity.QualityEventId),
                new SqlParameter("@EventNumber", entity.EventNumber),
                new SqlParameter("@EventType", entity.EventType),
                new SqlParameter("@Severity", entity.Severity ?? (object)DBNull.Value),
                new SqlParameter("@SampleId", entity.SampleId ?? (object)DBNull.Value),
                new SqlParameter("@SampleNumber", entity.SampleNumber ?? (object)DBNull.Value),
                new SqlParameter("@CurrentStatus", entity.CurrentStatus),
                new SqlParameter("@DetectedBy", entity.DetectedBy ?? (object)DBNull.Value),
                new SqlParameter("@DetectedDate", entity.DetectedDate ?? (object)DBNull.Value),
                new SqlParameter("@DetectionSource", entity.DetectionSource ?? (object)DBNull.Value),
                new SqlParameter("@InitialDescription", entity.InitialDescription ?? (object)DBNull.Value),
                new SqlParameter("@ImmediateAction", entity.ImmediateAction ?? (object)DBNull.Value),
                new SqlParameter("@RootCauseCategory", entity.RootCauseCategory ?? (object)DBNull.Value),
                new SqlParameter("@RootCauseDetails", entity.RootCauseDetails ?? (object)DBNull.Value),
                new SqlParameter("@ImpactAssessment", entity.ImpactAssessment ?? (object)DBNull.Value),
                new SqlParameter("@CAPARequired", entity.CAPARequired),
                new SqlParameter("@QAConclusion", entity.QAConclusion ?? (object)DBNull.Value),
                new SqlParameter("@FinalDisposition", entity.FinalDisposition ?? (object)DBNull.Value),
                new SqlParameter("@ModifiedBy", entity.ModifiedBy ?? (object)DBNull.Value)
            };
        }

        private QualityEvent MapToQualityEvent(DataRow row)
        {
            return new QualityEvent
            {
                QualityEventId = row.GetSafeInt("QualityEventID"),
                EventNumber = row.GetSafeString("EventNumber"),
                EventType = row.GetSafeString("EventType"),
                Severity = row.GetSafeString("Severity"),
                SampleId = row["SampleID"] != DBNull.Value ? (int?)Convert.ToInt32(row["SampleID"]) : null,
                SampleNumber = row.GetSafeString("SampleNumber"),
                CurrentStatus = row.GetSafeString("CurrentStatus"),
                DetectedBy = row.GetSafeString("DetectedBy"),
                DetectedDate = row.GetSafeDateTime("DetectedDate"),
                DetectionSource = row.GetSafeString("DetectionSource"),
                InitialDescription = row.GetSafeString("InitialDescription"),
                ImmediateAction = row.GetSafeString("ImmediateAction"),
                RootCauseCategory = row.GetSafeString("RootCauseCategory"),
                RootCauseDetails = row.GetSafeString("RootCauseDetails"),
                ImpactAssessment = row.GetSafeString("ImpactAssessment"),
                CAPARequired = row.Table.Columns.Contains("CAPARequired") &&
                               row["CAPARequired"] != DBNull.Value &&
                               Convert.ToBoolean(row["CAPARequired"]),
                QAConclusion = row.GetSafeString("QAConclusion"),
                FinalDisposition = row.GetSafeString("FinalDisposition"),
                ClosedBy = row.GetSafeString("ClosedBy"),
                ClosedDate = row.GetSafeDateTime("ClosedDate"),
                CreatedDate = row.GetSafeDateTime("CreatedDate") ?? DateTime.Now,
                ModifiedBy = row.GetSafeString("ModifiedBy"),
                ModifiedDate = row.GetSafeDateTime("ModifiedDate")
            };
        }
    }
}