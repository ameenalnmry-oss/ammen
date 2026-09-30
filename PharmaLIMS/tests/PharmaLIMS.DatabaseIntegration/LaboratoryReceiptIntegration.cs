using System.Data;
using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
internal static class LaboratoryReceiptIntegration
{
    internal static async Task VerifyAsync(SqlConnection connection)
    {
        if(!connection.Database.StartsWith("PharmaLIMS_Integration_",StringComparison.Ordinal)) throw new InvalidOperationException("Disposable database required.");
        foreach(string kind in new[]{"PRM","EM"})
        foreach(string scenario in new[]{"success","duplicate","early","future","identity","reviewed","update","delete","rollback"})
        {
            using var tx=connection.BeginTransaction();
            try
            {
                string number="RECEIPT-"+Guid.NewGuid().ToString("N");
                using var setup=new SqlCommand(kind=="PRM" ? @"
INSERT dbo.PRM_Samples(SampleNumber,SampleCategory,SampleDateTime,CreatedDate,CreatedBy,SampleStatus)
OUTPUT INSERTED.SampleID VALUES(@number,N'Primary Packaging','20260101','20260103',N'Fixture',N'Registered');" : @"
INSERT dbo.EM_Areas(AreaCode,AreaName,Grade) VALUES(@number,N'Receipt fixture',N'D');
DECLARE @area int=SCOPE_IDENTITY();
INSERT dbo.EM_Events(EventNo,AreaId,EventDate,WorkflowStatus)
OUTPUT INSERTED.Id VALUES(@number,@area,'20260101',N'Registered');",connection,tx);
                setup.Parameters.Add("@number",SqlDbType.NVarChar,100).Value=number;
                int id=Convert.ToInt32(await setup.ExecuteScalarAsync());
                if(scenario=="reviewed")
                {
                    using var change=new SqlCommand(kind=="PRM"?"UPDATE dbo.PRM_Samples SET SampleStatus=N'Reviewed' WHERE SampleID=@id":"UPDATE dbo.EM_Events SET WorkflowStatus=N'Reviewed' WHERE Id=@id",connection,tx);
                    change.Parameters.Add("@id",SqlDbType.Int).Value=id;await change.ExecuteNonQueryAsync();
                }
                async Task Capture()
                {
                    using var cmd=new SqlCommand(LaboratoryReceiptSql.Insert,connection,tx);
                    cmd.Parameters.AddRange(LaboratoryReceiptSql.Parameters(kind,id,scenario=="identity"?"WRONG":number,
                        scenario=="early"?new DateTime(2025,12,31):scenario=="future"?DateTime.Now.AddYears(2):new DateTime(2026,1,2,10,15,0),
                        "Fixture receiver","Received intact against approved procedure","Acceptance for laboratory testing","Technician","Fixture"));
                    await cmd.ExecuteScalarAsync();
                }
                int expected=scenario switch {"early" or "future"=>55305,"identity"=>55303,"reviewed"=>55304,"duplicate"=>55306,"update" or "delete"=>55301,_=>0};
                try
                {
                    await Capture();
                    if(scenario=="duplicate") await Capture();
                    if(scenario is "update" or "delete")
                    {
                        using var mutate=new SqlCommand(scenario=="update"?"UPDATE dbo.LaboratoryReceipts SET ReceiptDecision=N'Accepted' WHERE SampleNumber=@number":"DELETE dbo.LaboratoryReceipts WHERE SampleNumber=@number",connection,tx);
                        mutate.Parameters.Add("@number",SqlDbType.NVarChar,100).Value=number;await mutate.ExecuteNonQueryAsync();
                    }
                    if(expected!=0) throw new InvalidOperationException("Receipt guard failed: "+kind+" "+scenario);
                    using var query=new SqlCommand(SampleReceiptRegisterQuery.Query,connection,tx);
                    query.Parameters.AddRange(SampleReceiptRegisterQuery.Parameters("All",new DateTime(2026,1,2),new DateTime(2026,1,2),true));
                    using var reader=await query.ExecuteReaderAsync();var table=new DataTable();table.Load(reader);
                    var rows=table.Rows.Cast<DataRow>().Where(r=>r["SampleNumber"].ToString()==number).ToArray();
                    if(rows.Length!=1 || rows[0]["ReceivedBy"].ToString()!="Fixture receiver" || rows[0]["ReceiptDecision"].ToString()!="Accepted")
                        throw new InvalidOperationException("Signed receipt missing/duplicated in register.");
                }
                catch(SqlException ex) when(expected!=0 && ex.Number==expected) { }
            }
            finally {try {tx.Rollback();} catch(InvalidOperationException) { }}
        }
        using var remaining=new SqlCommand("SELECT COUNT(*) FROM dbo.LaboratoryReceipts WHERE ReceivedBy=N'Fixture receiver'",connection);
        if(Convert.ToInt32(await remaining.ExecuteScalarAsync())!=0) throw new InvalidOperationException("Receipt rollback failed.");
        Console.WriteLine("PASS PRM/EM signed receipt, duplicate, date, identity, reviewed, immutable evidence, rollback and receipt-date register.");
    }
}
