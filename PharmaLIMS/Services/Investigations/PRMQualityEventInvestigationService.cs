#nullable disable

using Microsoft.Data.SqlClient;
using System;
using System.Data;

namespace PharmaLIMS.Services.Investigations
{
    public static class PRMQualityEventInvestigationService
    {
        public static DataTable GetPRMQualityEventHeader(int qualityEventId)
        {
            DataTable eventTable = DatabaseHelper.ExecuteQuery(@"
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
    ModifiedDate,
    SourceModule,
    SourceRecordID
FROM dbo.QualityEvents
WHERE QualityEventID = @QualityEventID
  AND UPPER(LTRIM(RTRIM(ISNULL(SourceModule, N'')))) = N'PRM';",
                new[]
                {
                    new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId }
                });

            DataTable result = CreateHeaderSchema();

            if (eventTable == null || eventTable.Rows.Count == 0)
                return result;

            DataRow qe = eventTable.Rows[0];

            int sourceRecordId = GetSafeInt(qe, "SourceRecordID");
            int sampleId = GetSafeInt(qe, "SampleID");
            string sampleNumber = GetSafeString(qe, "SampleNumber");

            DataTable sampleTable = GetPRMSampleFast(sourceRecordId, sampleId, sampleNumber);
            DataRow ps = sampleTable != null && sampleTable.Rows.Count > 0 ? sampleTable.Rows[0] : null;

            DataRow row = result.NewRow();

            CopyIfExists(qe, row, "QualityEventID");
            CopyIfExists(qe, row, "EventNumber");
            CopyIfExists(qe, row, "EventType");
            CopyIfExists(qe, row, "Severity");
            CopyIfExists(qe, row, "SampleID");
            CopyIfExists(qe, row, "SampleNumber");
            CopyIfExists(qe, row, "CurrentStatus");
            CopyIfExists(qe, row, "DetectedBy");
            CopyIfExists(qe, row, "DetectedDate");
            CopyIfExists(qe, row, "DetectionSource");
            CopyIfExists(qe, row, "InitialDescription");
            CopyIfExists(qe, row, "ImmediateAction");
            CopyIfExists(qe, row, "RootCauseCategory");
            CopyIfExists(qe, row, "RootCauseDetails");
            CopyIfExists(qe, row, "ImpactAssessment");
            CopyIfExists(qe, row, "CAPARequired");
            CopyIfExists(qe, row, "QAConclusion");
            CopyIfExists(qe, row, "FinalDisposition");
            CopyIfExists(qe, row, "ClosedBy");
            CopyIfExists(qe, row, "ClosedDate");
            CopyIfExists(qe, row, "CreatedDate");
            CopyIfExists(qe, row, "ModifiedBy");
            CopyIfExists(qe, row, "ModifiedDate");
            CopyIfExists(qe, row, "SourceModule");
            CopyIfExists(qe, row, "SourceRecordID");

            if (ps != null)
            {
                if (row["SampleID"] == DBNull.Value || Convert.ToInt32(row["SampleID"]) <= 0)
                    row["SampleID"] = GetSafeInt(ps, "SampleID");

                if (string.IsNullOrWhiteSpace(Convert.ToString(row["SampleNumber"])))
                    row["SampleNumber"] = GetSafeString(ps, "SampleNumber");

                CopyIfExists(ps, row, "SampleCategory");
                CopyIfExists(ps, row, "SamplePurpose");
                CopyIfExists(ps, row, "MaterialCode");
                CopyIfExists(ps, row, "MaterialName");
                CopyIfExists(ps, row, "MaterialType");
                CopyIfExists(ps, row, "Manufacturer");
                CopyIfExists(ps, row, "Supplier");
                CopyIfExists(ps, row, "ManufacturerLotNo");
                CopyIfExists(ps, row, "SupplierLotNo");
                CopyIfExists(ps, row, "GRNNo");
                CopyIfExists(ps, row, "ProductCode");
                CopyIfExists(ps, row, "ProductName");
                CopyIfExists(ps, row, "BatchNo");
                CopyIfExists(ps, row, "DosageForm");
                CopyIfExists(ps, row, "ProductionStage");
                CopyIfExists(ps, row, "SampleSource");
                CopyIfExists(ps, row, "SampledFrom");
                CopyIfExists(ps, row, "MachineLineNo");
                CopyIfExists(ps, row, "SpecificationNo");
                CopyIfExists(ps, row, "TestsRequired");
                CopyIfExists(ps, row, "StorageCondition");
                CopyIfExists(ps, row, "SampleStatus");
                CopyIfExists(ps, row, "ResultInterpretation");
                CopyIfExists(ps, row, "ReportStatus");
            }

            result.Rows.Add(row);
            return result;
        }

        private static DataTable GetPRMSampleFast(int sourceRecordId, int sampleId, string sampleNumber)
        {
            if (sourceRecordId > 0)
            {
                DataTable table = DatabaseHelper.ExecuteQuery(@"
SELECT TOP 1 *
FROM dbo.PRM_Samples
WHERE SampleID = @SampleID;",
                    new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = sourceRecordId } });

                if (table.Rows.Count > 0)
                    return table;
            }

            if (sampleId > 0 && sampleId != sourceRecordId)
            {
                DataTable table = DatabaseHelper.ExecuteQuery(@"
SELECT TOP 1 *
FROM dbo.PRM_Samples
WHERE SampleID = @SampleID;",
                    new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = sampleId } });

                if (table.Rows.Count > 0)
                    return table;
            }

            if (!string.IsNullOrWhiteSpace(sampleNumber))
            {
                return DatabaseHelper.ExecuteQuery(@"
SELECT TOP 1 *
FROM dbo.PRM_Samples
WHERE SampleNumber = @SampleNumber;",
                    new[] { new SqlParameter("@SampleNumber", SqlDbType.NVarChar, 80) { Value = sampleNumber } });
            }

            return new DataTable();
        }

        private static DataTable CreateHeaderSchema()
        {
            DataTable table = new DataTable();

            table.Columns.Add("QualityEventID", typeof(int));
            table.Columns.Add("EventNumber", typeof(string));
            table.Columns.Add("EventType", typeof(string));
            table.Columns.Add("Severity", typeof(string));
            table.Columns.Add("SampleID", typeof(int));
            table.Columns.Add("SampleNumber", typeof(string));
            table.Columns.Add("CurrentStatus", typeof(string));
            table.Columns.Add("DetectedBy", typeof(string));
            table.Columns.Add("DetectedDate", typeof(DateTime));
            table.Columns.Add("DetectionSource", typeof(string));
            table.Columns.Add("InitialDescription", typeof(string));
            table.Columns.Add("ImmediateAction", typeof(string));
            table.Columns.Add("RootCauseCategory", typeof(string));
            table.Columns.Add("RootCauseDetails", typeof(string));
            table.Columns.Add("ImpactAssessment", typeof(string));
            table.Columns.Add("CAPARequired", typeof(bool));
            table.Columns.Add("QAConclusion", typeof(string));
            table.Columns.Add("FinalDisposition", typeof(string));
            table.Columns.Add("ClosedBy", typeof(string));
            table.Columns.Add("ClosedDate", typeof(DateTime));
            table.Columns.Add("CreatedDate", typeof(DateTime));
            table.Columns.Add("ModifiedBy", typeof(string));
            table.Columns.Add("ModifiedDate", typeof(DateTime));
            table.Columns.Add("SourceModule", typeof(string));
            table.Columns.Add("SourceRecordID", typeof(int));

            table.Columns.Add("SampleCategory", typeof(string));
            table.Columns.Add("SamplePurpose", typeof(string));
            table.Columns.Add("MaterialCode", typeof(string));
            table.Columns.Add("MaterialName", typeof(string));
            table.Columns.Add("MaterialType", typeof(string));
            table.Columns.Add("Manufacturer", typeof(string));
            table.Columns.Add("Supplier", typeof(string));
            table.Columns.Add("ManufacturerLotNo", typeof(string));
            table.Columns.Add("SupplierLotNo", typeof(string));
            table.Columns.Add("GRNNo", typeof(string));
            table.Columns.Add("ProductCode", typeof(string));
            table.Columns.Add("ProductName", typeof(string));
            table.Columns.Add("BatchNo", typeof(string));
            table.Columns.Add("DosageForm", typeof(string));
            table.Columns.Add("ProductionStage", typeof(string));
            table.Columns.Add("SampleSource", typeof(string));
            table.Columns.Add("SampledFrom", typeof(string));
            table.Columns.Add("MachineLineNo", typeof(string));
            table.Columns.Add("SpecificationNo", typeof(string));
            table.Columns.Add("TestsRequired", typeof(string));
            table.Columns.Add("StorageCondition", typeof(string));
            table.Columns.Add("SampleStatus", typeof(string));
            table.Columns.Add("ResultInterpretation", typeof(string));
            table.Columns.Add("ReportStatus", typeof(string));

            return table;
        }

        private static void CopyIfExists(DataRow source, DataRow target, string columnName)
        {
            if (source == null || target == null)
                return;

            if (!source.Table.Columns.Contains(columnName) || !target.Table.Columns.Contains(columnName))
                return;

            object value = source[columnName];
            if (value == null)
                value = DBNull.Value;

            target[columnName] = value;
        }

        public static string GetPRMSampleCategory(int qualityEventId)
        {
            DataTable header = GetPRMQualityEventHeader(qualityEventId);
            if (header == null || header.Rows.Count == 0)
                return "";

            return GetPRMSampleCategory(header.Rows[0]);
        }

        public static string GetPRMSampleCategory(DataRow row)
        {
            if (row == null)
                return "";

            string combined =
                GetSafeString(row, "SampleNumber") + " " +
                GetSafeString(row, "SampleCategory") + " " +
                GetSafeString(row, "SamplePurpose") + " " +
                GetSafeString(row, "ProductName") + " " +
                GetSafeString(row, "ProductCode") + " " +
                GetSafeString(row, "MaterialName") + " " +
                GetSafeString(row, "MaterialCode") + " " +
                GetSafeString(row, "MaterialType") + " " +
                GetSafeString(row, "ProductionStage") + " " +
                GetSafeString(row, "DetectionSource") + " " +
                GetSafeString(row, "EventType");

            return combined.Trim();
        }

        public static string ResolvePRMChecklistCategory(string categoryText)
        {
            string text = (categoryText ?? "").Trim().ToLowerInvariant();

            // Priority matters:
            // FP and Finished Product must be resolved before "material",
            // otherwise text that includes MaterialName may be wrongly classified as Raw Material.
            if (text.StartsWith("fp-") ||
                text.Contains(" finished product") ||
                text.Contains("finished product") ||
                text.Contains(" finish product") ||
                text.Contains(" finished ") ||
                text.Contains(" fp-") ||
                text.Contains(" product "))
                return "PRM Finished Product";

            if (text.StartsWith("st-") ||
                text.Contains(" stability") ||
                text.Contains("stable") ||
                text.Contains("time point") ||
                text.Contains("pull point"))
                return "PRM Stability";

            if (text.Contains("production / in-process") ||
                text.Contains("production / in process") ||
                text.Contains("in-process") ||
                text.Contains("in process") ||
                text.Contains("inprocess") ||
                text.Contains("ipc") ||
                text.Contains("production") ||
                text.Contains("stage") ||
                text.Contains("machine") ||
                text.Contains("line"))
                return "PRM Production";

            if (text.StartsWith("rm-") ||
                text.Contains(" raw material") ||
                text.Contains("raw material") ||
                text.Contains(" grn") ||
                text.Contains(" supplier") ||
                text.Contains("manufacturer lot") ||
                text.Contains("supplier lot"))
                return "PRM Raw Material";

            return "PRM General";
        }

        public static DataTable GetPRMQualityEventChecklist(int qualityEventId)
        {
            string categoryText = GetPRMSampleCategory(qualityEventId);
            return GetPRMQualityEventChecklist(qualityEventId, categoryText);
        }

        public static DataTable GetPRMQualityEventChecklist(int qualityEventId, string categoryText)
        {
            string checklistCategory = ResolvePRMChecklistCategory(categoryText);

            DataTable table = DatabaseHelper.ExecuteQuery(@"
SELECT
    q.QuestionID,
    q.SectionName,
    q.QuestionText,
    q.AppliesToEventType,
    q.AppliesToSampleType,
    q.AppliesToTestCategory,
    q.AppliesToTestNameKeyword,
    q.AnswerType,
    q.IsRequired,
    q.ExpectedAnswer,
    q.QuestionLogic,
    ISNULL(a.AnswerValue, '') AS AnswerValue,
    ISNULL(a.Comments, '') AS Comments,
    a.AnsweredBy,
    a.AnsweredDate
FROM dbo.QualityEventChecklistQuestions q
LEFT JOIN dbo.QualityEventChecklistAnswers a
    ON a.QuestionID = q.QuestionID
   AND a.QualityEventID = @QualityEventID
WHERE q.IsActive = 1
  AND EXISTS
  (
      SELECT 1
      FROM dbo.QualityEvents qualityEvent
      WHERE qualityEvent.QualityEventID = @QualityEventID
        AND UPPER(LTRIM(RTRIM(ISNULL(qualityEvent.SourceModule, N'')))) = N'PRM'
  )
  AND
  (
        (q.AppliesToSampleType = 'ALL' AND q.AppliesToTestCategory = 'General Phase I')
     OR (q.AppliesToSampleType = 'PRM' AND q.AppliesToTestCategory = 'PRM General')
     OR (q.AppliesToSampleType = 'PRM' AND q.AppliesToTestCategory = @ChecklistCategory)
  )
ORDER BY
    CASE
        WHEN q.AppliesToTestCategory = 'General Phase I' THEN 0
        WHEN q.AppliesToTestCategory = 'PRM General' THEN 1
        ELSE 2
    END,
    q.SortOrder,
    q.QuestionID;",
                new[]
                {
                    new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                    new SqlParameter("@ChecklistCategory", SqlDbType.NVarChar, 120) { Value = checklistCategory }
                });

            EnsureChecklistSchema(table);
            return table;
        }

        public static void SavePRMQualityEventChecklistAnswers(int qualityEventId, DataTable checklistTable, string currentUser)
        {
            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
                SavePRMQualityEventChecklistAnswers(
                    connection,
                    transaction,
                    qualityEventId,
                    checklistTable,
                    currentUser));
        }

        public static void SavePRMQualityEventChecklistAnswers(
            SqlConnection connection,
            SqlTransaction transaction,
            int qualityEventId,
            DataTable checklistTable,
            string currentUser)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (qualityEventId <= 0 || checklistTable == null)
                return;

            using (SqlCommand eventGuard = new SqlCommand(@"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.QualityEvents WITH (UPDLOCK, HOLDLOCK)
    WHERE QualityEventID = @QualityEventID
      AND UPPER(LTRIM(RTRIM(ISNULL(SourceModule, N'')))) = N'PRM'
) THEN 1 ELSE 0 END;", connection, transaction))
            {
                eventGuard.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                eventGuard.Parameters.Add("@QualityEventID", SqlDbType.Int).Value = qualityEventId;
                if (Convert.ToInt32(eventGuard.ExecuteScalar()) != 1)
                    throw new InvalidOperationException("The selected Quality Event is not a PRM investigation.");
            }

            foreach (DataRow row in checklistTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                int questionId = GetSafeInt(row, "QuestionID");
                if (questionId <= 0)
                    continue;

                string answerValue = GetSafeString(row, "AnswerValue");
                string comments = GetSafeString(row, "Comments");
                if (string.IsNullOrWhiteSpace(answerValue) && string.IsNullOrWhiteSpace(comments))
                    continue;

                DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.QualityEventChecklistAnswers
SET AnswerValue = @AnswerValue,
    Comments = @Comments,
    AnsweredBy = @AnsweredBy,
    AnsweredDate = SYSDATETIME()
WHERE QualityEventID = @QualityEventID
  AND QuestionID = @QuestionID;

IF @@ROWCOUNT = 0
BEGIN
    INSERT dbo.QualityEventChecklistAnswers
    (
        QualityEventID, QuestionID, AnswerValue, Comments, AnsweredBy, AnsweredDate
    )
    VALUES
    (
        @QualityEventID, @QuestionID, @AnswerValue, @Comments, @AnsweredBy, SYSDATETIME()
    );
END;",
                    new[]
                    {
                        new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                        new SqlParameter("@QuestionID", SqlDbType.Int) { Value = questionId },
                        new SqlParameter("@AnswerValue", SqlDbType.NVarChar, 250) { Value = string.IsNullOrWhiteSpace(answerValue) ? (object)DBNull.Value : answerValue },
                        new SqlParameter("@Comments", SqlDbType.NVarChar, -1) { Value = string.IsNullOrWhiteSpace(comments) ? (object)DBNull.Value : comments },
                        new SqlParameter("@AnsweredBy", SqlDbType.NVarChar, 100) { Value = string.IsNullOrWhiteSpace(currentUser) ? (object)DBNull.Value : currentUser }
                    }, connection, transaction);
            }
        }

        public static DataTable GetPRMAffectedResults(int qualityEventId)
        {
            return DatabaseHelper.ExecuteQuery(@"
SELECT
    AffectedResultID,
    QualityEventID,
    CASE WHEN SourceModule = N'PRM' THEN SourceResultID ELSE SampleTestID END AS SampleTestID,
    SourceModule,
    SourceResultID,
    TestID,
    TestName,
    ResultValue,
    SpecificationLimit,
    SpecificationNumericLimit,
    Unit,
    FailureType,
    EvidenceSchemaVersion,
    CreatedDate
FROM dbo.QualityEventAffectedResults affected
WHERE QualityEventID = @QualityEventID
  AND EXISTS
  (
      SELECT 1 FROM dbo.QualityEvents qualityEvent
      WHERE qualityEvent.QualityEventID = affected.QualityEventID
        AND UPPER(LTRIM(RTRIM(ISNULL(qualityEvent.SourceModule, N'')))) = N'PRM'
  )
ORDER BY AffectedResultID;",
                new[]
                {
                    new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId }
                });
        }

        public static DataTable GetPRMActions(int qualityEventId)
        {
            return DatabaseHelper.ExecuteQuery(@"
SELECT
    QualityEventActionID,
    QualityEventID,
    ActionType,
    ActionDescription,
    PerformedBy,
    PerformedDate,
    ElectronicSignatureID
FROM dbo.QualityEventActions action
WHERE QualityEventID = @QualityEventID
  AND EXISTS
  (
      SELECT 1 FROM dbo.QualityEvents qualityEvent
      WHERE qualityEvent.QualityEventID = action.QualityEventID
        AND UPPER(LTRIM(RTRIM(ISNULL(qualityEvent.SourceModule, N'')))) = N'PRM'
  )
ORDER BY PerformedDate DESC, QualityEventActionID DESC;",
                new[]
                {
                    new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId }
                });
        }

        public static void AddPRMQualityEventAction(int qualityEventId, string actionType, string actionDescription, string currentUser)
        {
            DatabaseHelper.ExecuteNonQuery(@"
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
    @QualityEventID,
    @ActionType,
    @ActionDescription,
    @PerformedBy,
    SYSDATETIME()
);",
                new[]
                {
                    new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                    new SqlParameter("@ActionType", SqlDbType.NVarChar, 100) { Value = string.IsNullOrWhiteSpace(actionType) ? (object)DBNull.Value : actionType },
                    new SqlParameter("@ActionDescription", SqlDbType.NVarChar, -1) { Value = string.IsNullOrWhiteSpace(actionDescription) ? (object)DBNull.Value : actionDescription },
                    new SqlParameter("@PerformedBy", SqlDbType.NVarChar, 100) { Value = string.IsNullOrWhiteSpace(currentUser) ? (object)DBNull.Value : currentUser }
                });
        }

        private static void EnsureChecklistSchema(DataTable table)
        {
            if (table == null)
                return;

            EnsureColumn(table, "QuestionID", typeof(int));
            EnsureColumn(table, "SectionName", typeof(string));
            EnsureColumn(table, "QuestionText", typeof(string));
            EnsureColumn(table, "AnswerValue", typeof(string));
            EnsureColumn(table, "Comments", typeof(string));
            EnsureColumn(table, "IsRequired", typeof(bool));
            EnsureColumn(table, "ExpectedAnswer", typeof(string));
            EnsureColumn(table, "AnswerType", typeof(string));
        }

        private static void EnsureColumn(DataTable table, string columnName, Type type)
        {
            if (!table.Columns.Contains(columnName))
                table.Columns.Add(columnName, type);
        }

        private static string GetSafeString(DataRow row, string columnName)
        {
            if (row == null || row.Table == null || !row.Table.Columns.Contains(columnName))
                return "";

            if (row[columnName] == DBNull.Value || row[columnName] == null)
                return "";

            return row[columnName].ToString() ?? "";
        }

        private static int GetSafeInt(DataRow row, string columnName)
        {
            string value = GetSafeString(row, columnName);
            if (int.TryParse(value, out int result))
                return result;

            return 0;
        }
    }
}
