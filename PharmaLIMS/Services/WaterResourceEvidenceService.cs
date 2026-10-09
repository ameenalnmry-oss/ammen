using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;

namespace PharmaLIMS.Services
{
    public sealed class WaterResourceSnapshot
    {
        public int? PrimaryEquipmentId { get; set; }
        public string SupportingEquipmentCodes { get; set; } = "";
        public string KitCode { get; set; } = "";
        public string KitLot { get; set; } = "";
        public DateTime? KitExpiry { get; set; }
    }

    public static class WaterResourceEvidenceService
    {
        public static Dictionary<int, WaterResourceSnapshot> LoadLatest(DatabaseConnection database, int sampleId)
        {
            var table = database.ExecuteQuery(@"
WITH batches AS
(
 SELECT SampleTestID,BatchID,
 ROW_NUMBER() OVER(PARTITION BY SampleTestID ORDER BY MAX(EvidenceID) DESC) rn
 FROM dbo.WaterResultResourceEvidence WHERE SampleID=@SampleID
 GROUP BY SampleTestID,BatchID
)
SELECT e.SampleTestID,e.ResourceKind,e.EquipmentID,e.EquipmentCodeSnapshot,
 e.KitCode,e.KitLot,e.KitExpiry
FROM dbo.WaterResultResourceEvidence e
JOIN batches b ON b.SampleTestID=e.SampleTestID AND b.BatchID=e.BatchID
WHERE b.rn=1 ORDER BY e.EvidenceID;",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = sampleId } });
            var result = new Dictionary<int,WaterResourceSnapshot>();
            foreach (DataRow row in table.Rows)
            {
                int id = Convert.ToInt32(row["SampleTestID"], CultureInfo.InvariantCulture);
                if (!result.TryGetValue(id, out var entry))
                    result[id] = entry = new WaterResourceSnapshot();
                string kind = Convert.ToString(row["ResourceKind"],CultureInfo.InvariantCulture) ?? "";
                if (kind == "TEST_KIT")
                {
                    entry.KitCode = Convert.ToString(row["KitCode"], CultureInfo.InvariantCulture) ?? "";
                    entry.KitLot = Convert.ToString(row["KitLot"], CultureInfo.InvariantCulture) ?? "";
                    entry.KitExpiry = row["KitExpiry"] == DBNull.Value ? null : Convert.ToDateTime(row["KitExpiry"], CultureInfo.InvariantCulture);
                }
                else if (kind == "INSTRUMENT" && row["EquipmentID"] != DBNull.Value)
                {
                    int equipmentId = Convert.ToInt32(row["EquipmentID"], CultureInfo.InvariantCulture);
                    if (!entry.PrimaryEquipmentId.HasValue && string.IsNullOrWhiteSpace(entry.KitCode))
                        entry.PrimaryEquipmentId = equipmentId;
                    else
                    {
                        string code = Convert.ToString(row["EquipmentCodeSnapshot"], CultureInfo.InvariantCulture) ?? "";
                        if (code.Length > 0) entry.SupportingEquipmentCodes +=
                            (entry.SupportingEquipmentCodes.Length > 0 ? ", " : "") + code;
                    }
                }
            }
            return result;
        }
        public static bool IsKitEligible(string? name)
        {
            string n = (name ?? "").Trim().ToUpperInvariant();
            return n.Contains("CHLOR") || n.Contains("HARDNESS") ||
                n.Contains("CALCIUM") || n.Contains("MAGNESIUM") ||
                n.Contains("NITRATE") || n.Contains("SULPHATE") ||
                n.Contains("SULFATE") || n.Contains("AMMON") ||
                n is "ACIDITY" or "ALKALINITY" or "OXIDISABLE SUBSTANCES" or
                  "OXIDIZABLE SUBSTANCES" or "HEAVY METALS (AS PB)";
        }

        public static bool IsCompatibleSupport(string? name, LabEquipmentChoice equipment)
        {
            string n = (name ?? "").ToUpperInvariant();
            string type = (equipment.EquipmentType + " " + equipment.EquipmentName).ToUpperInvariant();
            if (n.Contains("MICROBIAL") || n.Contains("TAMC") || n.Contains("TYMC") ||
                n.Contains("COLIFORM") || n.Contains("ESCHERICHIA") ||
                n.Contains("PSEUDOMONAS") || n.Contains("SALMONELLA") ||
                n.Contains("STAPHYLOCOCCUS") || n.Contains("BURKHOLDERIA"))
                return type.Contains("VACUUM PUMP") || type.Contains("INCUBATOR") ||
                    type.Contains("COLONY COUNTER") || type.Contains("AUTOCLAVE");
            if (n.Contains("RESIDUE ON EVAPORATION"))
                return type.Contains("OVEN") || type.Contains("HOT PLATE") || type.Contains("BALANCE");
            if (n.Contains("NITRATE"))
                return type.Contains("WATER BATH");
            return false;
        }

        public static bool RequiresKit(string? name)
        {
            string s = (name ?? "").Trim().ToUpperInvariant();
            return s is "RESIDUAL CHLORINE" or "FREE CHLORINE" or "FREE RESIDUAL CHLORINE" or "CHLORINE";
        }

        public static void Validate(string name, string result, int? primaryId,
            string extraCodes, string kitCode, string kitLot, DateTime? expiry,
            IReadOnlyCollection<LabEquipmentChoice> available)
        {
            if (string.IsNullOrWhiteSpace(result)) return;
            var extras = ParseCodes(extraCodes);
            if (WaterTestResourcePolicy.IsNoInstrumentRequired(name))
            {
                if (primaryId.HasValue || extras.Count > 0 || !string.IsNullOrWhiteSpace(kitCode))
                    throw new InvalidOperationException("Visual examination must not have physical instrument or test kit.");
                return;
            }
            if (RequiresKit(name))
            {
                if (primaryId.HasValue || extras.Count > 0)
                    throw new InvalidOperationException("Chlorine kit method cannot be assigned unrelated instruments.");
                if (string.IsNullOrWhiteSpace(kitCode) || string.IsNullOrWhiteSpace(kitLot) || !expiry.HasValue)
                    throw new InvalidOperationException("Chlorine test kit identification, lot and expiry date are required.");
                if (expiry.Value.Date < DateTime.Today)
                    throw new InvalidOperationException("Expired chlorine test kit cannot be used.");
                return;
            }
            bool usesKit = !string.IsNullOrWhiteSpace(kitCode);
            if (usesKit)
            {
                if (!IsKitEligible(name))
                    throw new InvalidOperationException("This test is not configured for test-kit execution.");
                if (primaryId.HasValue)
                    throw new InvalidOperationException("Choose either kit execution or a primary instrument, not both.");
                if (string.IsNullOrWhiteSpace(kitLot) || !expiry.HasValue || expiry.Value.Date < DateTime.Today)
                    throw new InvalidOperationException("Enter a valid test kit lot and an unexpired expiry date.");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(kitLot) || expiry.HasValue)
                    throw new InvalidOperationException("Test kit identity is required when lot/expiry is entered.");
                if (!primaryId.HasValue)
                    throw new InvalidOperationException("An approved instrument or controlled test kit is required.");
                var compatible = WaterTestResourcePolicy.FilterEquipment(name, available);
                if (!compatible.Any(e => e.IsAvailable && e.EquipmentID == primaryId.Value))
                    throw new InvalidOperationException("Primary instrument is unavailable or incompatible.");
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in extras)
            {
                if (!names.Add(code))
                    throw new InvalidOperationException("Duplicate supporting instrument.");
                var found = available.FirstOrDefault(e => e.EquipmentCode.Equals(code, StringComparison.OrdinalIgnoreCase));
                if (found == null || !found.IsAvailable || found.EquipmentID == primaryId ||
                    !IsCompatibleSupport(name, found))
                    throw new InvalidOperationException("Invalid, unavailable or incompatible supporting instrument: " + code);
            }
        }

        public static List<string> ParseCodes(string? codes) =>
            (codes ?? "").Split(new[] {',', ';', '\n'}, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        public static void Append(SqlConnection connection, SqlTransaction transaction,
            int sampleId, int sampleTestId, int testId, string testName, string result,
            int? primaryId, string extras, string kitCode, string kitLot, DateTime? expiry,
            string signer, IReadOnlyCollection<LabEquipmentChoice> equipment)
        {
            if (string.IsNullOrWhiteSpace(result)) return;
            Validate(testName, result, primaryId, extras, kitCode, kitLot, expiry, equipment);
            Guid batch = Guid.NewGuid();
            void Insert(string kind, LabEquipmentChoice? item, string? code, string? lot, DateTime? date)
            {
                if (item != null)
                {
                    using var readiness = new SqlCommand(@"
SELECT CASE WHEN IsActive=1 AND EquipmentStatus=N'Active'
 AND QualificationStatus IN(N'Qualified',N'Due Soon',N'Not Required')
 AND (NextQualificationDate IS NULL OR NextQualificationDate>=CAST(SYSDATETIME() AS date))
 AND (CalibrationRequired=0 OR (CalibrationStatus IN(N'Calibrated',N'Due Soon',N'Not Required')
 AND (NextCalibrationDate IS NULL OR NextCalibrationDate>=CAST(SYSDATETIME() AS date))))
 THEN 1 ELSE 0 END
FROM dbo.LabEquipment WITH(UPDLOCK,HOLDLOCK)
WHERE EquipmentID=@EquipmentID;", connection, transaction);
                    readiness.Parameters.Add("@EquipmentID", SqlDbType.Int).Value = item.EquipmentID;
                    object? ready = readiness.ExecuteScalar();
                    if (ready == null || Convert.ToInt32(ready, CultureInfo.InvariantCulture) != 1)
                        throw new InvalidOperationException("Selected laboratory resource is unavailable at signature time: " + item.EquipmentCode);
                }
                using var cmd = new SqlCommand(@"INSERT dbo.WaterResultResourceEvidence
(BatchID,SampleID,SampleTestID,TestID,ResourceKind,EquipmentID,EquipmentCodeSnapshot,EquipmentNameSnapshot,
KitCode,KitLot,KitExpiry,ResultSnapshot,SignedBy)
VALUES(@Batch,@Sample,@SampleTest,@Test,@Kind,@Equipment,@EquipmentCode,@EquipmentName,
@KitCode,@KitLot,@Expiry,@Result,@Signer);", connection, transaction);
                cmd.Parameters.Add("@Batch", SqlDbType.UniqueIdentifier).Value = batch;
                cmd.Parameters.Add("@Sample", SqlDbType.Int).Value = sampleId;
                cmd.Parameters.Add("@SampleTest", SqlDbType.Int).Value = sampleTestId;
                cmd.Parameters.Add("@Test", SqlDbType.Int).Value = testId;
                cmd.Parameters.Add("@Kind", SqlDbType.NVarChar, 20).Value = kind;
                cmd.Parameters.Add("@Equipment", SqlDbType.Int).Value = item == null ? DBNull.Value : item.EquipmentID;
                cmd.Parameters.Add("@EquipmentCode", SqlDbType.NVarChar, 50).Value = item == null ? DBNull.Value : item.EquipmentCode;
                cmd.Parameters.Add("@EquipmentName", SqlDbType.NVarChar, 200).Value = item == null ? DBNull.Value : item.EquipmentName;
                cmd.Parameters.Add("@KitCode", SqlDbType.NVarChar, 100).Value = code == null ? DBNull.Value : code;
                cmd.Parameters.Add("@KitLot", SqlDbType.NVarChar, 100).Value = lot == null ? DBNull.Value : lot;
                cmd.Parameters.Add("@Expiry", SqlDbType.Date).Value = date.HasValue ? date.Value.Date : DBNull.Value;
                cmd.Parameters.Add("@Result", SqlDbType.NVarChar, 200).Value = result.Trim();
                cmd.Parameters.Add("@Signer", SqlDbType.NVarChar, 100).Value = signer;
                cmd.ExecuteNonQuery();
            }
            if (WaterTestResourcePolicy.IsNoInstrumentRequired(testName))
            {
                Insert("NO_INSTRUMENT", null, null, null, null);
                return;
            }
            if (!string.IsNullOrWhiteSpace(kitCode))
                Insert("TEST_KIT", null, kitCode.Trim(), kitLot.Trim(), expiry);
            else
                Insert("INSTRUMENT", equipment.Single(e => e.EquipmentID == primaryId), null, null, null);
            foreach (string code in ParseCodes(extras))
                Insert("INSTRUMENT", equipment.Single(e =>
                    e.EquipmentCode.Equals(code, StringComparison.OrdinalIgnoreCase)), null, null, null);
        }
    }
}
