using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System.Data;

internal static class SampleEvidenceDashboardIntegration
{
    internal static async Task VerifyAsync(SqlConnection connection)
    {
        if (!connection.Database.StartsWith("PharmaLIMS_Integration_",StringComparison.Ordinal))
            throw new InvalidOperationException("Evidence/dashboard tests require a disposable database.");
        using var transaction=connection.BeginTransaction();
        try
        {
            async Task<DataTable> Query(string sql,params SqlParameter[] parameters)
            {
                using var command=new SqlCommand(sql,connection,transaction) {CommandTimeout=60};
                command.Parameters.AddRange(parameters);
                using var reader=await command.ExecuteReaderAsync(); DataTable table=new();table.Load(reader);return table;
            }
            static void Require(bool ok,string message) {if(!ok)throw new InvalidOperationException("Sample evidence/dashboard: "+message);}
            int before=Convert.ToInt32((await Query(DashboardSampleQuery.Overdue)).Rows[0][0]);
            var fixtures=await Query(@"
DECLARE @sample int,@other int,@test int,@otherTest int,@certificate int,@otherCertificate int;
INSERT dbo.Samples(SampleNumber,SampleType,CreatedDate,Status,SamplingDateTime,SampledBy) VALUES(N'EVIDENCE-FIXTURE-WATER',N'PW',DATEADD(DAY,-8,SYSDATETIME()),N'Registered',SYSDATETIME(),N'Fixture');
SET @sample=CONVERT(int,SCOPE_IDENTITY());
INSERT dbo.Samples(SampleNumber,SampleType,CreatedDate,Status,SamplingDateTime,SampledBy) VALUES(N'EVIDENCE-FIXTURE-OTHER',N'PTW',DATEADD(DAY,-6,SYSDATETIME()),N'Registered',SYSDATETIME(),N'Fixture');
SET @other=CONVERT(int,SCOPE_IDENTITY());
INSERT dbo.Samples(SampleNumber,SampleType,CreatedDate,Status,SamplingDateTime,SampledBy) VALUES(N'EVIDENCE-FIXTURE-REJECTED',N'PW',DATEADD(DAY,-8,SYSDATETIME()),N'Rejected',SYSDATETIME(),N'Fixture');
INSERT dbo.PRM_Samples(SampleNumber,SampleCategory,CreatedDate,SampleStatus) VALUES
(N'EVIDENCE-FIXTURE-PP',N'Primary Packaging',DATEADD(DAY,-8,SYSDATETIME()),N'Registered'),
(N'EVIDENCE-FIXTURE-CANCELLED',N'Raw Material',DATEADD(DAY,-8,SYSDATETIME()),N'Cancelled');
DECLARE @testId int=(SELECT TOP(1) TestID FROM dbo.Tests ORDER BY TestID);
INSERT dbo.SampleTests(SampleID,TestID) VALUES(@sample,@testId); SET @test=CONVERT(int,SCOPE_IDENTITY());
INSERT dbo.SampleTests(SampleID,TestID) VALUES(@other,@testId); SET @otherTest=CONVERT(int,SCOPE_IDENTITY());
INSERT dbo.Certificates(CertificateNumber,SampleID,IssueDate,IssuedBy,IsCancelled,CertificateStatus)
VALUES(N'EVIDENCE-FIXTURE-COA',@sample,'20200101',N'Fixture',1,N'Cancelled'); SET @certificate=CONVERT(int,SCOPE_IDENTITY());
INSERT dbo.Certificates(CertificateNumber,SampleID,IssueDate,IssuedBy,IsCancelled,CertificateStatus)
VALUES(N'EVIDENCE-FIXTURE-OTHER-COA',@other,'20200101',N'Fixture',1,N'Cancelled'); SET @otherCertificate=CONVERT(int,SCOPE_IDENTITY());
INSERT dbo.AuditTrail(TableName,RecordID,Action,ChangedBy,PerformedBy,SampleNumber) VALUES
(N'Samples',@sample,N'FIXTURE-SAMPLE',N'Fixture',N'Fixture',NULL),
(N'SampleTests',@test,N'FIXTURE-TEST',N'Fixture',N'Fixture',NULL),
(N'Certificates',@certificate,N'FIXTURE-CERTIFICATE',N'Fixture',N'Fixture',N'EVIDENCE-FIXTURE-WATER'),
(N'Certificates',@sample,N'FIXTURE-LEGACY-CERTIFICATE',N'Fixture',N'Fixture',N'EVIDENCE-FIXTURE-COA'),
(N'Certificates',@certificate,N'FIXTURE-NUMBERLESS-CERTIFICATE',N'Fixture',N'Fixture',NULL),
(N'Users',@sample,N'FORBIDDEN-USER-COLLISION',N'Fixture',N'Fixture',N'EVIDENCE-FIXTURE-WATER'),
(N'PRM_Samples',@sample,N'FORBIDDEN-PRM-COLLISION',N'Fixture',N'Fixture',N'EVIDENCE-FIXTURE-WATER'),
(N'SampleTests',@otherTest,N'FORBIDDEN-OTHER-TEST',N'Fixture',N'Fixture',NULL),
(N'Certificates',@certificate,N'FORBIDDEN-OTHER-CERTIFICATE',N'Fixture',N'Fixture',N'EVIDENCE-FIXTURE-OTHER-COA');
INSERT dbo.ElectronicSignatures(SampleID,ActionType,ActionReason,SignedBy,MeaningOfSignature,UserRole)
VALUES(@sample,N'FIXTURE-SIGNATURE',N'Fixture evidence',N'Fixture',N'Review',N'Supervisor'),
(@other,N'FORBIDDEN-OTHER-SIGNATURE',N'Fixture evidence',N'Fixture',N'Review',N'Supervisor');
SELECT @sample AS SampleID,@other AS OtherSampleID;");
            int id=Convert.ToInt32(fixtures.Rows[0]["SampleID"]);
            var header=await Query(SampleDetailsEvidenceQuery.Header,new SqlParameter("@sampleId",SqlDbType.Int) {Value=id});
            Require(header.Rows.Count==1 && header.Rows[0]["SampleNumber"].ToString()=="EVIDENCE-FIXTURE-WATER","sample header and receipt-reason schema contract");
            var audit=await Query(SampleDetailsEvidenceQuery.AuditTrail,SampleDetailsEvidenceQuery.Parameters(id,"EVIDENCE-FIXTURE-WATER"));
            var actions=audit.Rows.Cast<DataRow>().Select(r=>r["ActionType"].ToString()!).ToArray();
            Require(actions.Count(a=>a.StartsWith("FIXTURE-",StringComparison.Ordinal))==5,"parent, child, current/legacy certificate evidence missing");
            Require(!actions.Any(a=>a.StartsWith("FORBIDDEN-",StringComparison.Ordinal)),"unrelated table/sample evidence leaked");
            Require(new[] {"ActionType","FieldName","OldValue","NewValue","Reason","PerformedBy","PerformedAt","ComputerName"}.All(audit.Columns.Contains),"audit grid column contract");
            var blankNumber=await Query(SampleDetailsEvidenceQuery.AuditTrail,SampleDetailsEvidenceQuery.Parameters(id,string.Empty));
            Require(blankNumber.Rows.Cast<DataRow>().Any(r=>r["ActionType"].ToString()=="FIXTURE-SAMPLE"),"blank sample number loses authoritative parent evidence");
            var signatures=await Query(SampleDetailsEvidenceQuery.Signatures,new SqlParameter("@sampleId",SqlDbType.Int) {Value=id});
            Require(signatures.Rows.Count==1 && signatures.Rows[0]["ActionType"].ToString()=="FIXTURE-SIGNATURE","signature scope / migrated schema");
            int after=Convert.ToInt32((await Query(DashboardSampleQuery.Overdue)).Rows[0][0]);
            Require(after==before+2,"old water + packaging counted; recent/rejected/cancelled excluded");
            Console.WriteLine("PASS Sample details audit/signature SQL, table/child/legacy-certificate isolation and seven-day water/PRM dashboard counts.");
        }
        finally {transaction.Rollback();}
    }
}
