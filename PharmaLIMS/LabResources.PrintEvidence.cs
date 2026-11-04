using Microsoft.Data.SqlClient;
using System;
using System.Data;

namespace PharmaLIMS
{
    public partial class LabResources
    {
        // Validate both historical stage hashes and materialize all printed
        // evidence while the same parent lock/transaction is still held.
        private (DataTable Activity, DataTable Signatures, DataTable Evidence, DataTable Points, DataTable Links)
            LoadActivityPrintEvidence(long activityId)
        {
            var data = new DataSet();
            _database.ExecuteInTransaction((connection, transaction) =>
            {
                using var command = new SqlCommand(@"
EXEC dbo.MicroEquipment_ValidateActivitySignatures @Activity;
SELECT a.ActivityID,a.EquipmentID,a.ActivityType,a.ActivityStatus,a.PerformedBy,
       a.ActualStartAt,a.ActualEndAt,a.MethodReference,a.ActivityReason,a.CreatedAt,
       a.EquipmentCodeSnapshot,a.EquipmentNameSnapshot
FROM dbo.MicroEquipmentActivities a WITH(HOLDLOCK) WHERE a.ActivityID=@Activity;
SELECT SignatureMeaning,SignedBy,SignedAt,SignatureReason,ContentHash
FROM dbo.MicroEquipmentActivitySignatures WITH(HOLDLOCK)
WHERE ActivityID=@Activity ORDER BY SignatureID;
SELECT EvidenceType,ReferenceValue,ObservedValue,MaximumAbsoluteError,AbsoluteError,
       NormalMinimum,NormalMaximum,CleaningAgentLot,Outcome,RecordedBy,RecordedAt
FROM dbo.MicroEquipmentStructuredEvidence WITH(HOLDLOCK) WHERE ActivityID=@Activity;
SELECT PointNo,ReferenceValue,ObservedValue,MaxAbsoluteError,NormalMinimum,NormalMaximum,AbsoluteError,Outcome
FROM dbo.MicroEquipmentCalibrationPoints WITH(HOLDLOCK)
WHERE ActivityID=@Activity ORDER BY PointNo;
SELECT Module,ParentRecordID,ResultRecordID,SampleNumberSnapshot,TestCodeSnapshot,LinkedBy,LinkedAt
FROM dbo.MicroEquipmentActivityLinks WITH(HOLDLOCK)
WHERE ActivityID=@Activity ORDER BY LinkID;", connection, transaction);
                command.Parameters.Add("@Activity", SqlDbType.BigInt).Value = activityId;
                using var adapter = new SqlDataAdapter(command);
                adapter.Fill(data);
                if (data.Tables.Count != 5 || data.Tables[0].Rows.Count != 1)
                    throw new InvalidOperationException("Complete equipment print evidence is unavailable.");
            });
            return (data.Tables[0], data.Tables[1], data.Tables[2], data.Tables[3], data.Tables[4]);
        }
    }
}
