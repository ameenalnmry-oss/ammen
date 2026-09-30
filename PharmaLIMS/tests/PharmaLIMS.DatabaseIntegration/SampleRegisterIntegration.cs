using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System.Data;
internal static class SampleRegisterIntegration
{
    internal static async Task VerifyAsync(SqlConnection connection)
    {
        if (!connection.Database.StartsWith("PharmaLIMS_Integration_",StringComparison.Ordinal)) throw new InvalidOperationException("Register tests require disposable database.");
        using var transaction=connection.BeginTransaction();
        try
        {
            using var insert=new SqlCommand(@"INSERT dbo.Samples(SampleNumber,SampleType,SamplingDateTime,ReceivedDateTime,CreatedDate,SampledBy,Status)
VALUES(N'REGISTER-TEST-PW',N'PW','20260101','20260102','20260103',N'Fixture sampler',N'Rejected'),
(N'REGISTER-TEST-PTW',N'PTW','20260101',NULL,'20260103',N'Fixture sampler',N'Registered');",connection,transaction);
            await insert.ExecuteNonQueryAsync();
            using var packagingInsert=new SqlCommand(@"INSERT dbo.PRM_Samples(SampleNumber,SampleCategory,MaterialCode,MaterialName,ManufacturerLotNo,SampleDateTime,CreatedDate,CreatedBy)
VALUES(N'REGISTER-TEST-PP',N'Primary Packaging',N'PP-BOTTLE-01',N'Fixture bottle',N'PP-LOT-01','20260101','20260103',N'Fixture registrar');",connection,transaction);
            await packagingInsert.ExecuteNonQueryAsync();
            async Task<DataTable> Query(string type,DateTime day,bool receipt)
            {
                using var command=new SqlCommand(SampleReceiptRegisterQuery.Query,connection,transaction) {CommandTimeout=60};
                command.Parameters.AddRange(SampleReceiptRegisterQuery.Parameters(type,day,day,receipt));
                using var reader=await command.ExecuteReaderAsync();var table=new DataTable();table.Load(reader);return table;
            }
            var all=await Query("All",new DateTime(2026,1,3),false);
            if (all.Rows.Cast<DataRow>().Count(r=>r["SampleNumber"].ToString()!.StartsWith("REGISTER-TEST-"))!=3) throw new InvalidOperationException("Register excluded legitimate TEST numbers or wrong registration date.");
            if (all.Rows.Cast<DataRow>().GroupBy(r=>r["RecordKind"]+":"+r["SampleID"]).Any(g=>g.Count()>1)) throw new InvalidOperationException("Register duplicates source records.");
            var packaging=await Query("Primary Packaging",new DateTime(2026,1,3),false);
            var pp=packaging.Rows.Cast<DataRow>().Single(r=>r["SampleNumber"].ToString()=="REGISTER-TEST-PP");
            if (!pp["Description"].ToString()!.Contains("PP-BOTTLE-01 | Fixture bottle") || pp["BatchOrLot"].ToString()!="PP-LOT-01" || !pp.IsNull("ReceivedDateTime")) throw new InvalidOperationException("Packaging material identity / lot / receipt evidence lost.");
            var raw=await Query("Raw Material",new DateTime(2026,1,3),false);
            if (raw.Rows.Cast<DataRow>().Any(r=>r["SampleNumber"].ToString()=="REGISTER-TEST-PP")) throw new InvalidOperationException("Packaging incorrectly included as raw material.");
            var pw=await Query("Purified Water",new DateTime(2026,1,2),true);
            if (!pw.Rows.Cast<DataRow>().Any(r=>r["SampleNumber"].ToString()=="REGISTER-TEST-PW")) throw new InvalidOperationException("PW alias / receipt filter failed.");
            var ptw=await Query("Potable Water",new DateTime(2026,1,3),true);
            if (ptw.Rows.Cast<DataRow>().Any(r=>r["SampleNumber"].ToString()=="REGISTER-TEST-PTW")) throw new InvalidOperationException("Missing receipt replaced by registration date.");
            // Compile every branch against the fully migrated disposable schema.
            using var compile=new SqlCommand(SampleReceiptRegisterQuery.Query,connection,transaction) {CommandTimeout=60};
            compile.Parameters.AddRange(SampleReceiptRegisterQuery.Parameters("All",new DateTime(1900,1,1),new DateTime(2099,12,31),false));
            using var fullReader=await compile.ExecuteReaderAsync();var full=new DataTable();full.Load(fullReader);
            if (full.Rows.Cast<DataRow>().Any(r=>r["RecordKind"].ToString()!="WATER" && !r.IsNull("ReceivedDateTime"))) throw new InvalidOperationException("Non-water laboratory receipt inferred.");
            Console.WriteLine("PASS Sample register SQL grain, all source tables, alias filters and receipt/registration separation.");
        }
        finally { transaction.Rollback(); }
    }
}
