using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;

namespace PharmaLIMS.Services
{
    public sealed class WaterLesExecutionSnapshot
    {
        public int SampleTestID { get; init; }
        public decimal? SampleTemperatureC { get; init; }
        public string VerificationReference { get; init; } = string.Empty;
        public bool VerificationConfirmed { get; init; }
        public string ExecutionRemarks { get; init; } = string.Empty;
    }

    public static class WaterLesExecutionService
    {
        public static bool IsStructuredLesTest(int testId) => testId == 2 || testId == 3;

        public static string GetProcedureReference(int testId) =>
            IsStructuredLesTest(testId) ? "MQC-G-0018" : string.Empty;

        public static string GetGuidance(int testId)
        {
            return testId switch
            {
                2 => "Routine direct pH measurement using a calibrated pH meter according to the current approved MQC-G-0018 method. Follow any stage-specific KCl instruction only when that approved method explicitly requires it.",
                3 => "Conductivity: collect 100 mL in clean conductivity-grade glassware, avoid air bubbles, maintain 25 C +/- 1 C, verify the calibrated conductivity meter with the approved KCl standard, and record the reading in uS/cm.",
                _ => string.Empty
            };
        }

        public static Dictionary<int, WaterLesExecutionSnapshot> LoadLatest(
            DatabaseConnection database,
            int sampleId)
        {
            DataTable table = database.ExecuteQuery(@"
WITH ranked AS
(
    SELECT
        e.SampleTestID,
        e.SampleTemperatureC,
        e.VerificationReference,
        e.VerificationConfirmed,
        e.ExecutionRemarks,
        ROW_NUMBER() OVER
        (
            PARTITION BY e.SampleTestID
            ORDER BY e.SignedAt DESC,e.EvidenceID DESC
        ) AS rn
    FROM dbo.WaterResultExecutionEvidence e
    WHERE e.SampleID=@SampleID
)
SELECT SampleTestID,SampleTemperatureC,VerificationReference,VerificationConfirmed,ExecutionRemarks
FROM ranked
WHERE rn=1;",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = sampleId } });

            return table.Rows.Cast<DataRow>().ToDictionary(
                row => Convert.ToInt32(row["SampleTestID"], CultureInfo.InvariantCulture),
                row => new WaterLesExecutionSnapshot
                {
                    SampleTestID = Convert.ToInt32(row["SampleTestID"], CultureInfo.InvariantCulture),
                    SampleTemperatureC = row["SampleTemperatureC"] == DBNull.Value
                        ? (decimal?)null
                        : Convert.ToDecimal(row["SampleTemperatureC"], CultureInfo.InvariantCulture),
                    VerificationReference = Convert.ToString(row["VerificationReference"], CultureInfo.InvariantCulture) ?? string.Empty,
                    VerificationConfirmed = Convert.ToBoolean(row["VerificationConfirmed"], CultureInfo.InvariantCulture),
                    ExecutionRemarks = Convert.ToString(row["ExecutionRemarks"], CultureInfo.InvariantCulture) ?? string.Empty
                });
        }

        public static void ValidateForSave(
            int testId,
            string testName,
            string resultValue,
            string temperatureText,
            string verificationReference,
            bool verificationConfirmed)
        {
            if (string.IsNullOrWhiteSpace(resultValue) || !IsStructuredLesTest(testId))
                return;

            if (!verificationConfirmed)
                throw new InvalidOperationException(
                    "LES execution evidence is incomplete for '" + testName +
                    "'. Confirm the instrument calibration / verification check before saving.");

            if (string.IsNullOrWhiteSpace(verificationReference))
                throw new InvalidOperationException(
                    "LES execution evidence is incomplete for '" + testName +
                    "'. Enter the calibration / verification reference.");

            if (testId == 3) WaterLesTemperatureContract.Parse(temperatureText);

        }

        public static void AppendEvidenceInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId,
            int sampleTestId,
            int testId,
            string rawResult,
            int equipmentId,
            string temperatureText,
            string verificationReference,
            bool verificationConfirmed,
            string executionRemarks,
            string signedBy,
            string userRole,
            string meaning,
            string reason)
        {
            if (!IsStructuredLesTest(testId) || string.IsNullOrWhiteSpace(rawResult))
                return;

            string procedureReference = GetProcedureReference(testId);
            string guidance = GetGuidance(testId);
            decimal? temperatureC = testId == 3 ? WaterLesTemperatureContract.Parse(temperatureText) : null;

            string equipmentCode;
            string equipmentName;
            string equipmentType;
            using (var cmd = new SqlCommand(@"
SELECT EquipmentCode,EquipmentName,EquipmentType
FROM dbo.LabEquipment WITH (UPDLOCK,HOLDLOCK)
WHERE EquipmentID=@EquipmentID;", connection, transaction))
            {
                cmd.Parameters.Add("@EquipmentID", SqlDbType.Int).Value = equipmentId;
                using SqlDataReader reader = cmd.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("The selected equipment no longer exists.");
                equipmentCode = reader.GetString(0);
                equipmentName = reader.GetString(1);
                equipmentType = reader.GetString(2);
            }

            using var insert = new SqlCommand(@"
INSERT dbo.WaterResultExecutionEvidence
(
    SampleID,SampleTestID,TestID,ProcedureReference,MethodGuidanceSnapshot,
    SampleTemperatureC,VerificationReference,VerificationConfirmed,ExecutionRemarks,
    RawResultSnapshot,EquipmentID,EquipmentCodeSnapshot,EquipmentNameSnapshot,EquipmentTypeSnapshot,
    SignedBy,UserRole,MeaningOfSignature,ActionReason,SignedAt,SourceWorkstation
)
VALUES
(
    @SampleID,@SampleTestID,@TestID,@ProcedureReference,@Guidance,
    @SampleTemperatureC,@VerificationReference,@VerificationConfirmed,@ExecutionRemarks,
    @RawResult,@EquipmentID,@EquipmentCode,@EquipmentName,@EquipmentType,
    @SignedBy,@UserRole,@Meaning,@Reason,SYSUTCDATETIME(),@Workstation
);", connection, transaction);

            insert.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
            insert.Parameters.Add("@SampleTestID", SqlDbType.Int).Value = sampleTestId;
            insert.Parameters.Add("@TestID", SqlDbType.Int).Value = testId;
            insert.Parameters.Add("@ProcedureReference", SqlDbType.NVarChar, 100).Value = procedureReference;
            insert.Parameters.Add("@Guidance", SqlDbType.NVarChar, 1000).Value = guidance;
            insert.Parameters.Add("@SampleTemperatureC", SqlDbType.Decimal).Value =
                temperatureC.HasValue ? temperatureC.Value : DBNull.Value;
            insert.Parameters["@SampleTemperatureC"].Precision = 5;
            insert.Parameters["@SampleTemperatureC"].Scale = 2;
            insert.Parameters.Add("@VerificationReference", SqlDbType.NVarChar, 200).Value =
                string.IsNullOrWhiteSpace(verificationReference) ? DBNull.Value : verificationReference.Trim();
            insert.Parameters.Add("@VerificationConfirmed", SqlDbType.Bit).Value = verificationConfirmed;
            insert.Parameters.Add("@ExecutionRemarks", SqlDbType.NVarChar, 1000).Value =
                string.IsNullOrWhiteSpace(executionRemarks) ? DBNull.Value : executionRemarks.Trim();
            insert.Parameters.Add("@RawResult", SqlDbType.NVarChar, 200).Value = rawResult.Trim();
            insert.Parameters.Add("@EquipmentID", SqlDbType.Int).Value = equipmentId;
            insert.Parameters.Add("@EquipmentCode", SqlDbType.NVarChar, 50).Value = equipmentCode;
            insert.Parameters.Add("@EquipmentName", SqlDbType.NVarChar, 200).Value = equipmentName;
            insert.Parameters.Add("@EquipmentType", SqlDbType.NVarChar, 100).Value = equipmentType;
            insert.Parameters.Add("@SignedBy", SqlDbType.NVarChar, 100).Value = signedBy;
            insert.Parameters.Add("@UserRole", SqlDbType.NVarChar, 100).Value =
                string.IsNullOrWhiteSpace(userRole) ? DBNull.Value : userRole;
            insert.Parameters.Add("@Meaning", SqlDbType.NVarChar, 250).Value = meaning;
            insert.Parameters.Add("@Reason", SqlDbType.NVarChar, 500).Value = reason;
            insert.Parameters.Add("@Workstation", SqlDbType.NVarChar, 100).Value = Environment.MachineName;
            insert.ExecuteNonQuery();
        }
    }
}
