using Microsoft.Data.SqlClient;
using PharmaLIMS.Interfaces;
using System;
using System.Data;
using System.Globalization;

namespace PharmaLIMS.Repositories;

public sealed class CultureMediaRepository : ICultureMediaRepository
{
    private readonly string _connectionString;

    public CultureMediaRepository(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A valid database connection string is required.", nameof(connectionString));

        _connectionString = connectionString;
    }

    public DataTable Load(CultureMediaQuery operation, params SqlParameter[] parameters)
        => QuerySql(SqlFor(operation), parameters);

    private static string SqlFor(CultureMediaQuery operation)
        => operation switch
        {
            CultureMediaQuery.LoadSelectedReleaseReportSummary => @"
SELECT TOP (1)
    MediaLotID,
    QualificationDate,
    ReviewedBy,
    OverallResult
FROM dbo.MediaQualifications
WHERE MediaQualificationID = @MediaQualificationID
  AND QualificationType = 'Media Lot Promotion Test / Release';",
            CultureMediaQuery.LoadSelectedReleaseReportTests => @"
SELECT
    TestName,
    OrganismName,
    ATCCNumber,
    InoculumLevel,
    ExpectedResult,
    ActualResult,
    ControlCount,
    TestCount,
    RecoveryPercent,
    IncubationConditions,
    TestResult
FROM dbo.MediaQualificationTests
WHERE MediaQualificationID = @MediaQualificationID;",
            CultureMediaQuery.LoadOrganismsForReport => @"
SELECT
    TestName,
    OrganismName,
    ATCCNumber,
    InoculumLevel,
    ExpectedResult,
    ActualResult,
    ControlCount,
    TestCount,
    RecoveryPercent,
    IncubationConditions,
    TestResult,
    Remarks
FROM dbo.MediaQualificationTests
WHERE MediaQualificationID = @MediaQualificationID
ORDER BY MediaQualificationTestID;",
            CultureMediaQuery.LoadMediaLotReceiptForPrint => @"
SELECT TOP (1)
    l.MediaLotID,
    m.MediaCode,
    m.MediaName,
    l.LotNumber,
    l.ReceivedDate,
    l.ExpiryDate,
    l.QuantityReceived,
    l.ReceiptStatus,
    l.ReceivedBy
FROM dbo.CultureMediaLots l
INNER JOIN dbo.CultureMedia m ON m.MediaID = l.MediaID
WHERE l.MediaLotID = @MediaLotID;",
            CultureMediaQuery.LoadReleaseReportHeaderForPrint => @"
SELECT TOP (1)
    q.MediaQualificationID,
    q.MediaLotID,
    q.QualificationNo,
    q.QualificationDate,
    q.PerformedBy,
    q.ReviewedBy,
    q.ReleasedBy,
    q.ReviewDate,
    q.ReleaseDate,
    q.QualificationStatus,
    q.OverallResult,
    q.QualificationStartedAt,
    q.MinimumIncubationHoursSnapshot,
    q.IncubationCompletedAt,
    q.Remarks,
    m.MediaCode,
    m.MediaName,
    m.Manufacturer,
    m.StorageCondition,
    l.LotNumber,
    l.ManufacturerLot,
    l.Supplier,
    l.COANumber,
    l.ReceivedDate,
    l.ReceiptStatus,
    l.ExpiryDate
FROM dbo.MediaQualifications q
INNER JOIN dbo.CultureMediaLots l ON l.MediaLotID = q.MediaLotID
INNER JOIN dbo.CultureMedia m ON m.MediaID = l.MediaID
WHERE q.MediaQualificationID = @MediaQualificationID;",
            CultureMediaQuery.LoadReleaseReportTestsForPrint => @"
SELECT
    TestName,
    OrganismName,
    ATCCNumber,
    InoculumLevel,
    ExpectedResult,
    ActualResult,
    ControlCount,
    TestCount,
    RecoveryPercent,
    IncubationConditions,
    TestResult
FROM dbo.MediaQualificationTests
WHERE MediaQualificationID = @MediaQualificationID
ORDER BY MediaQualificationTestID;",
            CultureMediaQuery.LoadReleasedMediaLabelData => @"
SELECT TOP (1)
    l.MediaLotID,
    m.MediaCode,
    m.MediaName,
    l.LotNumber,
    l.ExpiryDate,
    l.ReceiptStatus
FROM dbo.CultureMediaLots l
INNER JOIN dbo.CultureMedia m ON m.MediaID = l.MediaID
WHERE l.MediaLotID = @MediaLotID;",
            CultureMediaQuery.LoadPreparationForPrint => @"
SELECT TOP (1)
    p.MediaPreparationID,
    p.MediaPreparationNo,
    p.PreparationDate,
    p.ExpiryDate,
    p.QuantityPrepared,
    p.PowderQuantityG,
    p.PreparedBy,
    p.BatchSize,
    p.FinalPH,
    p.Appearance,
    p.AutoclaveCycleNo,
    p.AutoclaveTemperature,
    p.AutoclaveHoldingTime,
    p.SterilityReview,
    p.SterilityReviewedBy,
    p.SterilityReviewedAt,
    p.VisualCheckedBy,
    p.VisualCheckedAt,
    p.ReleaseStatus,
    p.ReleasedBy,
    p.ReleasedAt,
    p.Remarks,
    m.MediaCode,
    m.MediaName,
    m.MediaType,
    l.LotNumber,
    l.ManufacturerLot,
    l.Supplier,
    l.COANumber,
    l.ExpiryDate AS SourceLotExpiry
FROM dbo.MediaPreparations p
INNER JOIN dbo.CultureMedia m ON m.MediaID = p.MediaID
INNER JOIN dbo.CultureMediaLots l ON l.MediaLotID = p.MediaLotID
WHERE p.MediaPreparationID = @MediaPreparationID;",
            CultureMediaQuery.LoadPreparedMediaLabelData => @"
SELECT TOP (1)
    p.MediaPreparationNo,
    m.MediaCode,
    m.MediaName,
    l.LotNumber,
    p.PreparationDate,
    p.ExpiryDate,
    p.QuantityPrepared,
    p.PreparedBy,
    p.ReleaseStatus,
    p.FinalPH
FROM dbo.MediaPreparations p
INNER JOIN dbo.CultureMedia m ON m.MediaID = p.MediaID
LEFT JOIN dbo.CultureMediaLots l ON l.MediaLotID = p.MediaLotID
WHERE p.MediaPreparationID = @MediaPreparationID;",
            CultureMediaQuery.ResolveMediaLotBySelectedLotId => @"
SELECT TOP 1
    l.MediaLotID,
    l.MediaID,
    m.MediaCode,
    m.MediaName,
    m.MediaType,
    m.StorageCondition,
    l.LotNumber,
    l.Supplier,
    l.ExpiryDate,
    m.DefaultExpiryDays
FROM dbo.CultureMediaLots l
INNER JOIN dbo.CultureMedia m ON m.MediaID = l.MediaID
WHERE l.MediaLotID = @MediaLotID;",
            CultureMediaQuery.ResolveMediaLotByMediaId => @"
SELECT TOP 1
    l.MediaLotID,
    l.MediaID,
    m.MediaCode,
    m.MediaName,
    m.MediaType,
    m.StorageCondition,
    l.LotNumber,
    l.Supplier,
    l.ExpiryDate,
    m.DefaultExpiryDays
FROM dbo.CultureMediaLots l
INNER JOIN dbo.CultureMedia m ON m.MediaID = l.MediaID
WHERE UPPER(LTRIM(RTRIM(l.LotNumber))) = UPPER(LTRIM(RTRIM(@LotNumber)))
ORDER BY l.MediaLotID DESC;",
            CultureMediaQuery.ValidateMediaLotForPreparation => @"
SELECT TOP (1)
    ReceiptStatus,
    ExpiryDate
FROM dbo.CultureMediaLots
WHERE MediaLotID = @MediaLotID;",
            CultureMediaQuery.ValidatePreparationSourceLotForRelease => @"
SELECT TOP (1)
    l.ReceiptStatus,
    l.ExpiryDate
FROM dbo.MediaPreparations p
INNER JOIN dbo.CultureMediaLots l ON l.MediaLotID = p.MediaLotID
WHERE p.MediaPreparationID = @MediaPreparationID;",
            CultureMediaQuery.LoadPreparationReleaseGateRecord => @"
SELECT TOP (1)
    MediaPreparationID,
    PreparedBy,
    ExpiryDate,
    SterilityReview,
    SterilityReviewedBy,
    SterilityReviewedAt,
    VisualCheckedBy,
    VisualCheckedAt,
    ReleaseStatus
FROM dbo.MediaPreparations
WHERE MediaPreparationID = @MediaPreparationID;",
            CultureMediaQuery.LoadReceipts => @"
SELECT
    l.MediaLotID,
    l.MediaID,
    m.MediaCode,
    m.MediaName,
    m.MediaType,
    m.StorageCondition,
    l.LotNumber,
    l.Supplier,
    l.ManufacturerLot,
    l.ReceivedDate,
    CONVERT(varchar(11), l.ReceivedDate, 106) AS ReceivedDateText,
    l.ExpiryDate,
    CASE WHEN l.ExpiryDate IS NULL THEN '' ELSE CONVERT(varchar(11), l.ExpiryDate, 106) END AS ExpiryDateText,
    l.QuantityReceived,
    l.InitialStockG,
    l.CurrentStockG,
    l.StockStatus,
    l.COANumber,
    l.ReceivedBy,
    l.ReceiptStatus,
    CASE WHEN l.ReceiptStatus = 'Accepted' THEN 'Released' ELSE l.ReceiptStatus END AS DisplayReceiptStatus,
    l.Remarks,
    m.MediaCode + ' - ' + m.MediaName + ' / Lot ' + ISNULL(l.LotNumber, '') AS DisplayName
FROM dbo.CultureMediaLots l
INNER JOIN dbo.CultureMedia m ON m.MediaID = l.MediaID
ORDER BY l.MediaLotID DESC;",
            CultureMediaQuery.LoadReleaseReports => @"
SELECT
    q.MediaQualificationID,
    q.MediaLotID,
    q.QualificationNo,
    l.LotNumber,
    m.MediaCode,
    m.MediaName,
    q.QualificationDate,
    CONVERT(varchar(11), q.QualificationDate, 106) AS QualificationDateText,
    q.PerformedBy,
    q.ReviewedBy,
    q.ReleasedBy,
    q.ReviewDate,
    q.ReleaseDate,
    q.QualificationStatus,
    q.OverallResult,
    q.QualificationStartedAt,
    q.MinimumIncubationHoursSnapshot,
    q.IncubationCompletedAt,
    q.Remarks
FROM dbo.MediaQualifications q
INNER JOIN dbo.CultureMediaLots l ON l.MediaLotID = q.MediaLotID
INNER JOIN dbo.CultureMedia m ON m.MediaID = l.MediaID
WHERE q.QualificationType = 'Media Lot Promotion Test / Release'
ORDER BY q.MediaQualificationID DESC;",
            CultureMediaQuery.LoadPreparations => @"
SELECT
    p.MediaPreparationID,
    p.MediaPreparationNo,
    p.MediaID,
    p.MediaLotID,
    m.MediaCode,
    m.MediaName,
    l.LotNumber,
    l.Supplier,
    l.COANumber,
    l.ExpiryDate AS LotExpiryDate,
    p.PreparationDate,
    CONVERT(varchar(11), p.PreparationDate, 106) AS PreparationDateText,
    p.ExpiryDate,
    CASE WHEN p.ExpiryDate IS NULL THEN '' ELSE CONVERT(varchar(11), p.ExpiryDate, 106) END AS ExpiryDateText,
    p.QuantityPrepared,
    p.PowderQuantityG,
    p.PreparedBy,
    p.BatchSize,
    p.FinalPH,
    p.Appearance,
    p.AutoclaveCycleNo,
    p.AutoclaveTemperature,
    p.AutoclaveHoldingTime,
    CASE
        WHEN UPPER(LTRIM(RTRIM(ISNULL(p.SterilityReview, '')))) IN ('RELEASED', 'GPT PASSED') THEN 'Passed'
        WHEN UPPER(LTRIM(RTRIM(ISNULL(p.SterilityReview, '')))) IN ('REJECTED', 'GPT FAILED') THEN 'Failed'
        ELSE p.SterilityReview
    END AS SterilityReview,
    p.ReleaseStatus,
    p.Remarks,
    p.MediaPreparationNo + ' / ' + m.MediaCode + ' - ' + m.MediaName + ' / Lot ' + ISNULL(l.LotNumber, '') AS DisplayName
FROM dbo.MediaPreparations p
INNER JOIN dbo.CultureMedia m ON m.MediaID = p.MediaID
LEFT JOIN dbo.CultureMediaLots l ON l.MediaLotID = p.MediaLotID
ORDER BY p.MediaPreparationID DESC;",
            CultureMediaQuery.LoadMediaForFilters => @"
SELECT DISTINCT
    m.MediaID,
    m.MediaCode,
    m.MediaName
FROM dbo.CultureMedia m
INNER JOIN dbo.CultureMediaLots l ON l.MediaID = m.MediaID
WHERE m.IsActive = 1
ORDER BY m.MediaCode;",
            CultureMediaQuery.LoadStoredMediaForPreparation => @"
SELECT
    l.MediaLotID,
    l.MediaID,
    m.MediaCode,
    m.MediaName,
    m.MediaType,
    m.StorageCondition,
    l.LotNumber,
    l.Supplier,
    l.ManufacturerLot,
    l.ReceivedDate,
    l.ExpiryDate,
    l.QuantityReceived,
    l.CurrentStockG,
    l.StockStatus,
    l.COANumber,
    l.ReceiptStatus,
    m.DefaultExpiryDays,
    m.MediaCode + ' - ' + m.MediaName + ' / Lot ' + ISNULL(l.LotNumber, '') +
        ' / Stock ' + CONVERT(varchar(40), ISNULL(l.CurrentStockG, 0)) + ' g' +
        CASE WHEN l.ExpiryDate IS NULL THEN '' ELSE ' / Exp ' + CONVERT(varchar(11), l.ExpiryDate, 106) END AS DisplayName
FROM dbo.CultureMediaLots l
INNER JOIN dbo.CultureMedia m ON m.MediaID = l.MediaID
WHERE l.ReceiptStatus IN ('Released', 'Accepted')
  AND (l.ExpiryDate IS NULL OR l.ExpiryDate >= CAST(GETDATE() AS date))
  AND ISNULL(l.CurrentStockG, 0) > 0
  AND m.IsActive = 1
ORDER BY m.MediaCode, l.MediaLotID DESC;",
            CultureMediaQuery.LoadMediaLotsForRelease => @"
SELECT
    l.MediaLotID,
    l.MediaID,
    m.MediaCode,
    m.MediaName,
    m.MediaType,
    m.StorageCondition,
    l.LotNumber,
    l.Supplier,
    l.COANumber,
    l.ReceivedDate,
    l.ExpiryDate,
    l.QuantityReceived,
    l.ReceiptStatus,
    m.MediaCode + ' - ' + m.MediaName + ' / Lot ' + ISNULL(l.LotNumber, '') + ' / ' + l.ReceiptStatus AS DisplayName
FROM dbo.CultureMediaLots l
INNER JOIN dbo.CultureMedia m ON m.MediaID = l.MediaID
WHERE l.ReceiptStatus IN ('Quarantine', 'Released', 'Accepted')
  AND m.IsActive = 1
ORDER BY CASE WHEN l.ReceiptStatus = 'Quarantine' THEN 0 ELSE 1 END, l.MediaLotID DESC;",
            CultureMediaQuery.LoadGptHistory => @"
SELECT TOP 5
    t.TestName,
    t.OrganismName,
    t.TestResult
FROM dbo.MediaQualificationTests t
INNER JOIN dbo.MediaQualifications q ON q.MediaQualificationID = t.MediaQualificationID
WHERE q.MediaLotID = @MediaLotID
ORDER BY t.MediaQualificationTestID DESC;",
            CultureMediaQuery.LoadDashboardSummary => @"
SELECT
    COUNT(*) AS Total,
    SUM(CASE WHEN ReceiptStatus IN ('Released', 'Accepted') THEN 1 ELSE 0 END) AS Released,
    SUM(CASE WHEN ReceiptStatus = 'Quarantine' THEN 1 ELSE 0 END) AS Quarantine,
    SUM(CASE WHEN ReceiptStatus = 'Rejected' THEN 1 ELSE 0 END) AS Rejected,
    SUM(CASE WHEN ExpiryDate <= DATEADD(DAY, 7, GETDATE()) AND ExpiryDate >= GETDATE() AND ReceiptStatus NOT IN ('Rejected') THEN 1 ELSE 0 END) AS Expiring
FROM dbo.CultureMediaLots;",
            CultureMediaQuery.LoadMediaLotForStockReconciliation => @"
SELECT TOP (1) MediaLotID, LotNumber, CurrentStockG, ReceiptStatus
FROM dbo.CultureMediaLots
WHERE MediaLotID = @MediaLotID;",
            CultureMediaQuery.LoadQualificationTimingRequirements => @"
SELECT RequirementID, MediaTypePattern, TestName, MinimumIncubationHours, ApprovedBy, ApprovedAt,
       TimingConfirmedMinimumIncubationHours, TimingConfirmedBy, TimingConfirmedAt
FROM dbo.CultureMediaQualificationRequirements
WHERE IsActive=1
  AND ApprovalStatus=N'Approved'
  AND ApprovedBy IS NOT NULL
  AND ApprovedAt IS NOT NULL
ORDER BY MediaTypePattern, TestName, EffectiveDate DESC, RequirementID DESC;",
            CultureMediaQuery.LoadQualificationWorkflowRecord => @"
SELECT TOP (1)
    MediaQualificationID,
    QualificationNo,
    MediaLotID,
    PerformedBy,
    ReviewedBy,
    ReleasedBy,
    ReviewDate,
    ReleaseDate,
    QualificationStatus,
    OverallResult,
    QualificationStartedAt,
    MinimumIncubationHoursSnapshot,
    IncubationCompletedAt
FROM dbo.MediaQualifications
WHERE MediaQualificationID = @MediaQualificationID;",
            CultureMediaQuery.LoadQualificationTests => @"
SELECT TestName, OrganismName, ATCCNumber, InoculumLevel, ExpectedResult, ActualResult,
       ControlCount, TestCount, RecoveryPercent, IncubationConditions, TestResult
FROM dbo.MediaQualificationTests
WHERE MediaQualificationID = @MediaQualificationID
ORDER BY MediaQualificationTestID;",
            CultureMediaQuery.LoadMediaLotQualificationValidation => @"
SELECT TOP (1)
    ReceiptStatus,
    ReceivedDate,
    ExpiryDate
FROM dbo.CultureMediaLots
WHERE MediaLotID = @MediaLotID;",
            CultureMediaQuery.LoadApplicableQualificationRequirements => @"
;WITH Applicable AS
(
    SELECT
        r.TestName,
        r.MinimumRecoveryPercent,
        r.MaximumRecoveryPercent,
        r.MinimumIncubationHours,
        r.TimingConfirmedMinimumIncubationHours,
        r.TimingConfirmedBy,
        r.TimingConfirmedAt,
        ROW_NUMBER() OVER
        (
            PARTITION BY r.TestName
            ORDER BY CASE WHEN r.MediaTypePattern = N'%' THEN 1 ELSE 0 END,
                     LEN(r.MediaTypePattern) DESC,
                     r.EffectiveDate DESC,
                     r.RequirementID DESC
        ) AS rn
    FROM dbo.CultureMediaQualificationRequirements r
    INNER JOIN dbo.CultureMediaLots l ON l.MediaLotID = @MediaLotID
    INNER JOIN dbo.CultureMedia m ON m.MediaID = l.MediaID
    WHERE r.IsActive = 1
      AND r.IsRequired = 1
      AND r.ApprovalStatus = N'Approved'
      AND r.ApprovedBy IS NOT NULL
      AND r.ApprovedAt IS NOT NULL
      AND r.TimingConfirmedBy IS NOT NULL
      AND r.TimingConfirmedAt IS NOT NULL
      AND r.TimingConfirmedMinimumIncubationHours = r.MinimumIncubationHours
      AND r.EffectiveDate <= CAST(SYSDATETIME() AS date)
      AND ISNULL(m.MediaType, N'') LIKE r.MediaTypePattern
)
SELECT TestName, MinimumRecoveryPercent, MaximumRecoveryPercent, MinimumIncubationHours,
       TimingConfirmedMinimumIncubationHours, TimingConfirmedBy, TimingConfirmedAt
FROM Applicable
WHERE rn = 1
ORDER BY TestName;",
            CultureMediaQuery.LoadQualificationRequirementSnapshots => @"
SELECT TestName, MinimumRecoveryPercent, MaximumRecoveryPercent,
       MinimumIncubationHoursSnapshot
FROM dbo.MediaQualificationRequirementSnapshots
WHERE MediaQualificationID = @MediaQualificationID
ORDER BY SnapshotID;",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported controlled Culture Media operation.")
        };

    public object? ReadScalar(CultureMediaScalar operation, params SqlParameter[] parameters)
        => ScalarSql(SqlFor(operation), parameters);

    private static string SqlFor(CultureMediaScalar operation)
        => operation switch
        {
            CultureMediaScalar.PreparationPreparedBy => "SELECT PreparedBy FROM dbo.MediaPreparations WHERE MediaPreparationID = @MediaPreparationID;",
            CultureMediaScalar.HasSignedPreparationControls => @"
SELECT CASE WHEN VisualCheckedBy IS NOT NULL
                  OR VisualCheckedAt IS NOT NULL
                  OR SterilityReviewedBy IS NOT NULL
                  OR SterilityReviewedAt IS NOT NULL
            THEN 1 ELSE 0 END
FROM dbo.MediaPreparations
WHERE MediaPreparationID = @MediaPreparationID;",
            CultureMediaScalar.HasVisualCheckSignature => @"
SELECT CASE WHEN VisualCheckedBy IS NOT NULL OR VisualCheckedAt IS NOT NULL THEN 1 ELSE 0 END
FROM dbo.MediaPreparations
WHERE MediaPreparationID = @MediaPreparationID;",
            CultureMediaScalar.PreparationReleaseStatus => @"
SELECT ReleaseStatus
FROM dbo.MediaPreparations
WHERE MediaPreparationID = @MediaPreparationID;",
            CultureMediaScalar.LoadSopField => @"
SELECT TOP (1) FieldValue
FROM dbo.CultureMediaSopFields
WHERE EntityType = @EntityType
  AND EntityID = @EntityID
  AND AnnexureCode = @AnnexureCode
  AND FieldName = @FieldName;",
            CultureMediaScalar.CultureMediaSopSchemaReady => @"
SELECT CASE WHEN
    (SELECT COUNT(1)
     FROM sys.tables
     WHERE schema_id = SCHEMA_ID(N'dbo')
       AND name IN
       (
           N'CultureMediaSopFields',
           N'CultureMediaVisualChecks',
           N'CultureMediaSignatures',
           N'CultureMediaStockTransactions',
           N'CultureMediaStockReconciliations',
           N'CultureMediaPreparationDispositions',
           N'CultureMediaPrintHistory',
           N'CultureMediaQualificationRequirements',
           N'MediaQualifications',
           N'MediaQualificationRequirementSnapshots'
       )) = 10
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'ApprovalStatus') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'ApprovedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'ApprovedAt') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'MinimumIncubationHours') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedMinimumIncubationHours') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedAt') IS NOT NULL
    AND COL_LENGTH(N'dbo.MediaQualifications',N'QualificationStartedAt') IS NOT NULL
    AND COL_LENGTH(N'dbo.MediaQualifications',N'MinimumIncubationHoursSnapshot') IS NOT NULL
    AND COL_LENGTH(N'dbo.MediaQualifications',N'IncubationCompletedAt') IS NOT NULL
THEN 1 ELSE 0 END;",
            CultureMediaScalar.FindMediaMasterId => @"
SELECT TOP (1) MediaID
FROM dbo.CultureMedia
WHERE UPPER(LTRIM(RTRIM(MediaCode))) = UPPER(LTRIM(RTRIM(@MediaCode)))
ORDER BY MediaID;",
            CultureMediaScalar.MediaLotReceiptStatus => @"
SELECT ReceiptStatus
FROM dbo.CultureMediaLots
WHERE MediaLotID = @MediaLotID;",
            CultureMediaScalar.HasActiveQualificationForLot => @"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.MediaQualifications
    WHERE MediaLotID = @MediaLotID
      AND QualificationType = 'Media Lot Promotion Test / Release'
      AND QualificationStatus IN ('In Progress', 'Pending Review', 'Qualified', 'Released')
) THEN 1 ELSE 0 END;",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported controlled Culture Media operation.")
        };

    public int RunCommand(CultureMediaCommand operation, params SqlParameter[] parameters)
        => ExecuteSql(SqlFor(operation), parameters);

    private static string SqlFor(CultureMediaCommand operation)
        => operation switch
        {
            CultureMediaCommand.InsertReleaseOrganism => @"
INSERT INTO dbo.MediaQualificationTests
(MediaQualificationID, TestName, OrganismName, ATCCNumber, InoculumLevel, ExpectedResult, ActualResult, ControlCount, TestCount, RecoveryPercent, IncubationConditions, TestResult, Remarks)
VALUES
(@MediaQualificationID, @TestName, @OrganismName, @ATCCNumber, @InoculumLevel, @ExpectedResult, @ActualResult, @ControlCount, @TestCount, @RecoveryPercent, @IncubationConditions, @TestResult, @Remarks);",
            CultureMediaCommand.SaveSopField => @"
MERGE dbo.CultureMediaSopFields AS target
USING (SELECT @EntityType AS EntityType, @EntityID AS EntityID, @AnnexureCode AS AnnexureCode, @FieldName AS FieldName) AS source
ON target.EntityType = source.EntityType
   AND target.EntityID = source.EntityID
   AND target.AnnexureCode = source.AnnexureCode
   AND target.FieldName = source.FieldName
WHEN MATCHED THEN
    UPDATE SET FieldValue = @FieldValue, UpdatedBy = @UserName, UpdatedAt = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (EntityType, EntityID, AnnexureCode, FieldName, FieldValue, CreatedBy)
    VALUES (@EntityType, @EntityID, @AnnexureCode, @FieldName, @FieldValue, @UserName);",
            CultureMediaCommand.RecordCultureMediaPrint => @"
INSERT INTO dbo.CultureMediaPrintHistory
(EntityType, EntityID, RecordNumber, DocumentType, PrintSequence, PrintedBy, PrintedAt, Reason)
SELECT
    @EntityType,
    @EntityID,
    @RecordNumber,
    @DocumentType,
    ISNULL(MAX(PrintSequence), 0) + 1,
    @PrintedBy,
    SYSUTCDATETIME(),
    @Reason
FROM dbo.CultureMediaPrintHistory WITH (UPDLOCK, HOLDLOCK)
WHERE EntityType = @EntityType
  AND EntityID = @EntityID
  AND DocumentType = @DocumentType;",
            CultureMediaCommand.AddCultureMediaSignature => @"
INSERT INTO dbo.CultureMediaSignatures
(EntityType, EntityID, RecordNumber, ActionType, ActionReason, SignedBy, MeaningOfSignature)
VALUES
(@EntityType, @EntityID, @RecordNumber, @ActionType, @ActionReason, @SignedBy, @MeaningOfSignature);",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported controlled Culture Media operation.")
        };

    private DataTable QuerySql(string sql, params SqlParameter[] parameters)
    {
        using var connection = new SqlConnection(_connectionString);
        using var command = CreateCommand(connection, sql, parameters);
        using var adapter = new SqlDataAdapter(command);
        var table = new DataTable();
        adapter.Fill(table);
        return table;
    }

    private int ExecuteSql(string sql, params SqlParameter[] parameters)
    {
        using var connection = new SqlConnection(_connectionString);
        using var command = CreateCommand(connection, sql, parameters);
        connection.Open();
        return command.ExecuteNonQuery();
    }

    private object? ScalarSql(string sql, params SqlParameter[] parameters)
    {
        using var connection = new SqlConnection(_connectionString);
        using var command = CreateCommand(connection, sql, parameters);
        connection.Open();
        return command.ExecuteScalar();
    }

    public string GenerateNumber(string sequenceName, string prefix)
    {
        const string sql = @"
DECLARE @Year int = YEAR(GETDATE());

MERGE dbo.MediaNumberSequences WITH (HOLDLOCK) AS target
USING (SELECT @SequenceName AS SequenceName, @Year AS SequenceYear) AS source
ON target.SequenceName = source.SequenceName
   AND target.SequenceYear = source.SequenceYear
WHEN MATCHED THEN
    UPDATE SET
        LastNumber = target.LastNumber + 1,
        Prefix = @Prefix,
        ModifiedAt = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (SequenceName, SequenceYear, LastNumber, Prefix, ModifiedAt)
    VALUES (@SequenceName, @Year, 1, @Prefix, SYSDATETIME())
OUTPUT inserted.Prefix + '-' + CONVERT(varchar(4), inserted.SequenceYear) + '-' + RIGHT('0000' + CONVERT(varchar(20), inserted.LastNumber), 4);";

        object? result = ScalarSql(sql,
            new SqlParameter("@SequenceName", sequenceName),
            new SqlParameter("@Prefix", prefix));

        return result?.ToString() ?? string.Empty;
    }

    public int FindExistingMediaLotId(int mediaId, string lotNumber)
    {
        if (mediaId <= 0 || string.IsNullOrWhiteSpace(lotNumber))
            return 0;

        const string sql = @"
SELECT TOP 1 MediaLotID
FROM dbo.CultureMediaLots
WHERE MediaID = @MediaID
  AND UPPER(LTRIM(RTRIM(LotNumber))) = UPPER(LTRIM(RTRIM(@LotNumber)))
ORDER BY MediaLotID DESC;";

        object? result = ScalarSql(sql,
            new SqlParameter("@MediaID", SqlDbType.Int) { Value = mediaId },
            new SqlParameter("@LotNumber", SqlDbType.NVarChar, 100) { Value = lotNumber.Trim() });

        return result == null || result == DBNull.Value
            ? 0
            : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static SqlCommand CreateCommand(SqlConnection connection, string sql, SqlParameter[]? parameters)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL command text cannot be empty.", nameof(sql));

        var command = new SqlCommand(sql, connection);
        if (parameters is { Length: > 0 })
            command.Parameters.AddRange(parameters);
        return command;
    }

    private static object DbValue(string value)
        => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}
