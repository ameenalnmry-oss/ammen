using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;

namespace PharmaLIMS.Services
{
    public sealed class LabEquipmentChoice
    {
        public int EquipmentID { get; init; }
        public string EquipmentCode { get; init; } = string.Empty;
        public string EquipmentName { get; init; } = string.Empty;
        public string EquipmentType { get; init; } = string.Empty;
        public bool IsAvailable { get; init; }
        public string ReadinessLabel { get; init; } = string.Empty;
        public string DisplayName => EquipmentCode + " - " + EquipmentName +
            (IsAvailable ? string.Empty : " [" + ReadinessLabel + "]");
    }

    public static class LabEquipmentUsageService
    {
        public static IReadOnlyList<LabEquipmentChoice> LoadAvailableEquipment(DatabaseConnection database)
        {
            DataTable table = database.ExecuteQuery(@"
SELECT
    EquipmentID,EquipmentCode,EquipmentName,EquipmentType,
    CASE
      WHEN IsActive=1
       AND EquipmentStatus=N'Active'
       AND QualificationStatus IN(N'Qualified',N'Due Soon',N'Not Required')
       AND (NextQualificationDate IS NULL OR NextQualificationDate>=CAST(SYSDATETIME() AS date))
       AND
       (
           CalibrationRequired=0
           OR
           (
               CalibrationStatus IN(N'Calibrated',N'Due Soon',N'Not Required')
               AND (NextCalibrationDate IS NULL OR NextCalibrationDate>=CAST(SYSDATETIME() AS date))
           )
       )
      THEN CAST(1 AS bit) ELSE CAST(0 AS bit)
    END AS IsAvailable,
    CASE
      WHEN IsActive=0 THEN N'Inactive'
      WHEN EquipmentStatus<>N'Active' THEN EquipmentStatus
      WHEN QualificationStatus NOT IN(N'Qualified',N'Due Soon',N'Not Required') THEN QualificationStatus
      WHEN NextQualificationDate IS NOT NULL AND NextQualificationDate<CAST(SYSDATETIME() AS date) THEN N'Qualification Expired'
      WHEN CalibrationRequired=1 AND CalibrationStatus NOT IN(N'Calibrated',N'Due Soon',N'Not Required') THEN CalibrationStatus
      WHEN CalibrationRequired=1 AND NextCalibrationDate IS NOT NULL AND NextCalibrationDate<CAST(SYSDATETIME() AS date) THEN N'Calibration Expired'
      ELSE N'Available'
    END AS ReadinessLabel
FROM dbo.LabEquipment
ORDER BY CASE
  WHEN IsActive=1
   AND EquipmentStatus=N'Active'
   AND QualificationStatus IN(N'Qualified',N'Due Soon',N'Not Required')
   AND (NextQualificationDate IS NULL OR NextQualificationDate>=CAST(SYSDATETIME() AS date))
   AND
   (
       CalibrationRequired=0
       OR
       (
           CalibrationStatus IN(N'Calibrated',N'Due Soon',N'Not Required')
           AND (NextCalibrationDate IS NULL OR NextCalibrationDate>=CAST(SYSDATETIME() AS date))
       )
   )
  THEN 0 ELSE 1 END,
  EquipmentCode;");

            return table.Rows.Cast<DataRow>().Select(row => new LabEquipmentChoice
            {
                EquipmentID = Convert.ToInt32(row["EquipmentID"], CultureInfo.InvariantCulture),
                EquipmentCode = Convert.ToString(row["EquipmentCode"], CultureInfo.InvariantCulture) ?? string.Empty,
                EquipmentName = Convert.ToString(row["EquipmentName"], CultureInfo.InvariantCulture) ?? string.Empty,
                EquipmentType = Convert.ToString(row["EquipmentType"], CultureInfo.InvariantCulture) ?? string.Empty,
                IsAvailable = Convert.ToBoolean(row["IsAvailable"], CultureInfo.InvariantCulture),
                ReadinessLabel = Convert.ToString(row["ReadinessLabel"], CultureInfo.InvariantCulture) ?? string.Empty
            }).ToList();
        }

        public static Dictionary<int, int?> LoadCurrentAssignments(
            DatabaseConnection database,
            string module,
            int parentRecordId)
        {
            DataTable table = database.ExecuteQuery(@"
SELECT ResultRecordID,EquipmentID
FROM dbo.LabEquipmentUsage
WHERE Module=@Module AND ParentRecordID=@ParentRecordID;",
                new[]
                {
                    new SqlParameter("@Module", SqlDbType.NVarChar, 20) { Value = module },
                    new SqlParameter("@ParentRecordID", SqlDbType.Int) { Value = parentRecordId }
                });

            return table.Rows.Cast<DataRow>().ToDictionary(
                row => Convert.ToInt32(row["ResultRecordID"], CultureInfo.InvariantCulture),
                row => row["EquipmentID"] == DBNull.Value
                    ? (int?)null
                    : Convert.ToInt32(row["EquipmentID"], CultureInfo.InvariantCulture));
        }

        public static void PersistAssignmentInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            string module,
            int parentRecordId,
            int resultRecordId,
            int? equipmentId,
            string signedBy,
            string userRole,
            string meaning,
            string reason,
            string recordReference)
        {
            if (parentRecordId <= 0 || resultRecordId <= 0)
                throw new ArgumentOutOfRangeException(nameof(resultRecordId));
            if (string.IsNullOrWhiteSpace(module))
                throw new ArgumentException("Equipment usage module is required.", nameof(module));

            int? currentEquipmentId = null;
            using (var current = new SqlCommand(@"
SELECT EquipmentID
FROM dbo.LabEquipmentUsage WITH (UPDLOCK,HOLDLOCK)
WHERE Module=@Module AND ResultRecordID=@ResultRecordID;", connection, transaction))
            {
                current.Parameters.Add("@Module", SqlDbType.NVarChar, 20).Value = module.Trim().ToUpperInvariant();
                current.Parameters.Add("@ResultRecordID", SqlDbType.Int).Value = resultRecordId;
                object? value = current.ExecuteScalar();
                if (value != null && value != DBNull.Value)
                    currentEquipmentId = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }

            string equipmentCode = string.Empty;
            string equipmentName = string.Empty;
            string equipmentType = string.Empty;
            string equipmentStatus = string.Empty;
            string qualificationStatus = string.Empty;
            string calibrationStatus = string.Empty;
            DateTime? nextQualification = null;
            DateTime? nextCalibration = null;

            if (equipmentId.HasValue)
            {
                using var equipment = new SqlCommand(@"
SELECT EquipmentCode,EquipmentName,EquipmentType,EquipmentStatus,QualificationStatus,
       CalibrationRequired,CalibrationStatus,NextCalibrationDate,NextQualificationDate,IsActive
FROM dbo.LabEquipment WITH (UPDLOCK,HOLDLOCK)
WHERE EquipmentID=@EquipmentID;", connection, transaction);
                equipment.Parameters.Add("@EquipmentID", SqlDbType.Int).Value = equipmentId.Value;
                using SqlDataReader reader = equipment.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("The selected laboratory equipment no longer exists.");

                equipmentCode = reader.GetString(0);
                equipmentName = reader.GetString(1);
                equipmentType = reader.GetString(2);
                equipmentStatus = reader.GetString(3);
                qualificationStatus = reader.GetString(4);
                bool calibrationRequired = reader.GetBoolean(5);
                calibrationStatus = reader.GetString(6);
                nextCalibration = reader.IsDBNull(7) ? null : reader.GetDateTime(7);
                nextQualification = reader.IsDBNull(8) ? null : reader.GetDateTime(8);
                bool isActive = reader.GetBoolean(9);

                DateTime today;
                using (var clock = new SqlCommand("SELECT CAST(SYSDATETIME() AS date);", connection, transaction))
                    today = Convert.ToDateTime(clock.ExecuteScalar(), CultureInfo.InvariantCulture).Date;

                bool qualificationReady =
                    qualificationStatus is "Qualified" or "Due Soon" or "Not Required" &&
                    (!nextQualification.HasValue || nextQualification.Value.Date >= today);
                bool calibrationReady =
                    !calibrationRequired ||
                    (
                        calibrationStatus is "Calibrated" or "Due Soon" or "Not Required" &&
                        (!nextCalibration.HasValue || nextCalibration.Value.Date >= today)
                    );

                if (!isActive ||
                    !string.Equals(equipmentStatus, "Active", StringComparison.OrdinalIgnoreCase) ||
                    !qualificationReady ||
                    !calibrationReady)
                {
                    throw new InvalidOperationException(
                        "Selected equipment is not available for controlled use. " +
                        "Refresh the record and choose an Active, qualified, and in-calibration device.");
                }
            }

            if (currentEquipmentId == equipmentId)
                return;

            if (equipmentId.HasValue)
            {
                using var upsert = new SqlCommand(@"
MERGE dbo.LabEquipmentUsage WITH (HOLDLOCK) AS target
USING (SELECT @Module AS Module,@ResultRecordID AS ResultRecordID) AS source
ON target.Module=source.Module AND target.ResultRecordID=source.ResultRecordID
WHEN MATCHED THEN
  UPDATE SET ParentRecordID=@ParentRecordID,EquipmentID=@EquipmentID,
             AssignedBy=@AssignedBy,AssignedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN
  INSERT(Module,ParentRecordID,ResultRecordID,EquipmentID,AssignedBy,AssignedAt)
  VALUES(@Module,@ParentRecordID,@ResultRecordID,@EquipmentID,@AssignedBy,SYSUTCDATETIME());",
                    connection, transaction);
                upsert.Parameters.Add("@Module", SqlDbType.NVarChar, 20).Value = module.Trim().ToUpperInvariant();
                upsert.Parameters.Add("@ParentRecordID", SqlDbType.Int).Value = parentRecordId;
                upsert.Parameters.Add("@ResultRecordID", SqlDbType.Int).Value = resultRecordId;
                upsert.Parameters.Add("@EquipmentID", SqlDbType.Int).Value = equipmentId.Value;
                upsert.Parameters.Add("@AssignedBy", SqlDbType.NVarChar, 100).Value = signedBy;
                upsert.ExecuteNonQuery();
            }
            else
            {
                using var clear = new SqlCommand(@"
DELETE dbo.LabEquipmentUsage
WHERE Module=@Module AND ResultRecordID=@ResultRecordID;", connection, transaction);
                clear.Parameters.Add("@Module", SqlDbType.NVarChar, 20).Value = module.Trim().ToUpperInvariant();
                clear.Parameters.Add("@ResultRecordID", SqlDbType.Int).Value = resultRecordId;
                clear.ExecuteNonQuery();
            }

            using (var history = new SqlCommand(@"
INSERT dbo.LabEquipmentUsageHistory
(Module,ParentRecordID,ResultRecordID,PreviousEquipmentID,EquipmentID,
 EquipmentCodeSnapshot,EquipmentNameSnapshot,EquipmentTypeSnapshot,
 EquipmentStatusSnapshot,QualificationStatusSnapshot,CalibrationStatusSnapshot,
 NextQualificationDateSnapshot,NextCalibrationDateSnapshot,
 ChangeType,MeaningOfSignature,ActionReason,SignedBy,UserRole,SignedAt,SourceWorkstation)
VALUES
(@Module,@ParentRecordID,@ResultRecordID,@PreviousEquipmentID,@EquipmentID,
 @EquipmentCode,@EquipmentName,@EquipmentType,
 @EquipmentStatus,@QualificationStatus,@CalibrationStatus,
 @NextQualificationDate,@NextCalibrationDate,
 @ChangeType,@Meaning,@Reason,@SignedBy,@UserRole,SYSUTCDATETIME(),@Workstation);",
                connection, transaction))
            {
                history.Parameters.Add("@Module", SqlDbType.NVarChar, 20).Value = module.Trim().ToUpperInvariant();
                history.Parameters.Add("@ParentRecordID", SqlDbType.Int).Value = parentRecordId;
                history.Parameters.Add("@ResultRecordID", SqlDbType.Int).Value = resultRecordId;
                history.Parameters.Add("@PreviousEquipmentID", SqlDbType.Int).Value =
                    currentEquipmentId.HasValue ? currentEquipmentId.Value : DBNull.Value;
                history.Parameters.Add("@EquipmentID", SqlDbType.Int).Value =
                    equipmentId.HasValue ? equipmentId.Value : DBNull.Value;
                history.Parameters.Add("@EquipmentCode", SqlDbType.NVarChar, 50).Value =
                    string.IsNullOrWhiteSpace(equipmentCode) ? DBNull.Value : equipmentCode;
                history.Parameters.Add("@EquipmentName", SqlDbType.NVarChar, 200).Value =
                    string.IsNullOrWhiteSpace(equipmentName) ? DBNull.Value : equipmentName;
                history.Parameters.Add("@EquipmentType", SqlDbType.NVarChar, 100).Value =
                    string.IsNullOrWhiteSpace(equipmentType) ? DBNull.Value : equipmentType;
                history.Parameters.Add("@EquipmentStatus", SqlDbType.NVarChar, 30).Value =
                    string.IsNullOrWhiteSpace(equipmentStatus) ? DBNull.Value : equipmentStatus;
                history.Parameters.Add("@QualificationStatus", SqlDbType.NVarChar, 30).Value =
                    string.IsNullOrWhiteSpace(qualificationStatus) ? DBNull.Value : qualificationStatus;
                history.Parameters.Add("@CalibrationStatus", SqlDbType.NVarChar, 30).Value =
                    string.IsNullOrWhiteSpace(calibrationStatus) ? DBNull.Value : calibrationStatus;
                history.Parameters.Add("@NextQualificationDate", SqlDbType.Date).Value =
                    nextQualification.HasValue ? nextQualification.Value.Date : DBNull.Value;
                history.Parameters.Add("@NextCalibrationDate", SqlDbType.Date).Value =
                    nextCalibration.HasValue ? nextCalibration.Value.Date : DBNull.Value;
                history.Parameters.Add("@ChangeType", SqlDbType.NVarChar, 20).Value =
                    !equipmentId.HasValue ? "CLEAR" : currentEquipmentId.HasValue ? "REASSIGN" : "ASSIGN";
                history.Parameters.Add("@Meaning", SqlDbType.NVarChar, 250).Value = meaning;
                history.Parameters.Add("@Reason", SqlDbType.NVarChar, 500).Value = reason;
                history.Parameters.Add("@SignedBy", SqlDbType.NVarChar, 100).Value = signedBy;
                history.Parameters.Add("@UserRole", SqlDbType.NVarChar, 100).Value =
                    string.IsNullOrWhiteSpace(userRole) ? DBNull.Value : userRole;
                history.Parameters.Add("@Workstation", SqlDbType.NVarChar, 100).Value = Environment.MachineName;
                history.ExecuteNonQuery();
            }

            DatabaseHelper.AddAuditTrailAdvanced(
                connection,
                transaction,
                "LabEquipmentUsage",
                resultRecordId,
                equipmentId.HasValue ? "Laboratory Equipment Assignment" : "Laboratory Equipment Assignment Cleared",
                currentEquipmentId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                equipmentId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                reason,
                signedBy,
                "EquipmentID",
                null,
                recordReference,
                module.Trim().ToUpperInvariant());
        }
    }
}
