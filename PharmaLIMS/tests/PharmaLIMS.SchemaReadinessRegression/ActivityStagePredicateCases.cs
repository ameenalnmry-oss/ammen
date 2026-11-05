using Microsoft.Data.SqlClient;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ActivityStagePredicateCases
{
    internal static async Task RunAsync(SqlConnection connection, string sourceRoot)
    {
        if (!connection.Database.StartsWith("PharmaLIMS_Integration_Schema_", StringComparison.Ordinal))
            throw new InvalidOperationException("Activity predicate checks require the owned disposable fixture.");
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(sourceRoot,"Database/MigrationManifest.json")));
        string Read(string version)
        {
            var entry=manifest.RootElement.GetProperty("migrations").EnumerateArray()
                .Single(value=>value.GetProperty("versionKey").GetString()==version);
            byte[] bytes=File.ReadAllBytes(Path.Combine(sourceRoot,"Database",entry.GetProperty("file").GetString()!));
            if(!Convert.ToHexString(SHA256.HashData(bytes)).Equals(entry.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase))
                throw new Exception("Activity migration source hash mismatch.");
            string sql=System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n","\n");
            int start=sql.IndexOf("IF NOT EXISTS(\n SELECT 1 FROM dbo.MicroEquipmentActivities",StringComparison.Ordinal);
            int end=sql.IndexOf(" THROW 56513",start,StringComparison.Ordinal);
            if(start<0 || end<0)throw new Exception("Activity predicate extraction failed.");
            string predicate=sql[(start+3)..end].Replace("''","'").Replace(" WITH(XLOCK,HOLDLOCK)","");
            foreach(string name in new[]{"MicroEquipmentActivities","MicroEquipmentActivitySignatures","MicroEquipmentStructuredEvidence","MicroEquipmentCalibrationPoints"})
                predicate=predicate.Replace("dbo."+name,"Fixture"+name,StringComparison.Ordinal);
            return predicate;
        }
        string before=Read("20261031_001"),active=Read("20261105_001");
        async Task<bool> Accepts(string predicate,string kind,string status,string stage,string actor,bool otherReview,bool sameReview,bool passingEvidence)
        {
            using var command=new SqlCommand(Fixture+"SELECT CASE WHEN "+predicate+" THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END;",connection);
            command.Parameters.Add("@Activity",SqlDbType.BigInt).Value=1L;
            command.Parameters.Add("@Version",SqlDbType.Binary,8).Value=new byte[]{1,0,0,0,0,0,0,0};
            command.Parameters.Add("@Prior",SqlDbType.NVarChar,30).Value=stage=="Reviewed"?"Submitted":"Reviewed";
            command.Parameters.Add("@Stage",SqlDbType.NVarChar,30).Value=stage;
            command.Parameters.Add("@Actor",SqlDbType.NVarChar,100).Value=actor;
            command.Parameters.Add("@Kind",SqlDbType.NVarChar,60).Value=kind;
            command.Parameters.Add("@Status",SqlDbType.NVarChar,30).Value=status;
            command.Parameters.Add("@OtherReview",SqlDbType.Bit).Value=otherReview;
            command.Parameters.Add("@SameReview",SqlDbType.Bit).Value=sameReview;
            command.Parameters.Add("@PassingEvidence",SqlDbType.Bit).Value=passingEvidence;
            return (bool)(await command.ExecuteScalarAsync())!;
        }
        foreach(var item in new[]{
            ("same reviewer approval","Use",false,true,false),
            ("approval without review","Use",false,false,false),
            ("mixed reviewers including approver","Use",true,true,false),
            ("cleaning approval without passing evidence","Cleaning",true,false,false)})
        {
            if(!await Accepts(before,item.Item2,"Reviewed","Approved","qa-fixture",item.Item3,item.Item4,item.Item5))
                throw new Exception("Frozen R9 RED witness did not reproduce: "+item.Item1);
            if(await Accepts(active,item.Item2,"Reviewed","Approved","qa-fixture",item.Item3,item.Item4,item.Item5))
                throw new Exception("Active activity predicate accepted: "+item.Item1);
            Console.WriteLine("PASS actual SQL predicate rejects "+item.Item1+"; frozen controlled R9 witness accepts.");
        }
        if(!await Accepts(active,"Use","Reviewed","Approved","qa-fixture",true,false,false) ||
           !await Accepts(active,"Cleaning","Reviewed","Approved","qa-fixture",true,false,true) ||
           !await Accepts(active,"Use","Submitted","Reviewed","qa-fixture",false,false,false) ||
           await Accepts(active,"Use","Submitted","Reviewed","technician-fixture",false,false,false))
            throw new Exception("Activity predicate unchanged positive/performer-separation controls failed.");
        Console.WriteLine("PASS activity SQL predicate: four unsafe R9 witnesses blocked and four independent-review/evidence controls retained; full service execution is a separate native test.");
    }

    private const string Fixture=@"
;WITH FixtureMicroEquipmentActivities AS(
 SELECT CAST(1 AS bigint) ActivityID,@Status ActivityStatus,CAST(0x0100000000000000 AS binary(8)) VersionToken,
 N'technician-fixture' PerformedBy,SYSDATETIMEOFFSET() ActualStartAt,SYSDATETIMEOFFSET() ActualEndAt,
 N'UAT-METHOD' MethodReference,@Kind ActivityType),
FixtureMicroEquipmentActivitySignatures AS(
 SELECT CAST(1 AS bigint) ActivityID,N'Reviewed' SignatureMeaning,N'reviewer-fixture' SignedBy WHERE @OtherReview=1
 UNION ALL SELECT CAST(1 AS bigint),N'Reviewed',N'qa-fixture' WHERE @SameReview=1),
FixtureMicroEquipmentStructuredEvidence AS(
 SELECT CAST(1 AS bigint) ActivityID,N'Cleaning' EvidenceType,
 CAST(NULL AS decimal(18,6)) ObservedValue,CAST(NULL AS decimal(18,6)) NormalMinimum,
 CAST(NULL AS decimal(18,6)) NormalMaximum,CAST(NULL AS decimal(18,6)) ReferenceValue,
 CAST(NULL AS decimal(18,6)) MaximumAbsoluteError,N'PASS' Outcome WHERE @PassingEvidence=1),
FixtureMicroEquipmentCalibrationPoints AS(
 SELECT CAST(NULL AS bigint) ActivityID,CAST(NULL AS int) PointNo,CAST(NULL AS decimal(18,6)) ObservedValue,
 CAST(NULL AS decimal(18,6)) NormalMinimum,CAST(NULL AS decimal(18,6)) NormalMaximum,
 CAST(NULL AS decimal(18,6)) ReferenceValue,CAST(NULL AS decimal(18,6)) MaxAbsoluteError,
 CAST(NULL AS nvarchar(4)) Outcome WHERE 1=0)
";
}
