using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class SiteEmMembershipCases
{
    private sealed record Case(string Name,string Sql,bool Native=true,int ExpectedPlan=1);

    internal static void Run(string cs,string root,bool corrected)
    {
        if(!Regex.IsMatch(new SqlConnectionStringBuilder(cs).InitialCatalog,@"^EQ001_UAT_EM_[0-9a-f]{12}$"))throw new Exception("Only the owned EM regression database is allowed.");
        VerifySources(root,corrected);
        using(var c=new SqlConnection(cs))
        {
            c.Open();
            // SQL Server binds existing-table columns before executing a batch.
            // Complete the schema-only fixture command before using its columns.
            using(var schema=new SqlCommand("ALTER TABLE dbo.EM_PlanSamples ADD AreaID int,Method nvarchar(100),IsNegativeControl bit;",c){CommandTimeout=15})schema.ExecuteNonQuery();
            using var q=new SqlCommand(@"
INSERT dbo.EM_Plans(PlanID,Status) VALUES(2,N'Ready for Results');
INSERT dbo.EM_Events(Id,PlanID,EventNo,AreaId,EmployeeId,EmployeeName,WorkflowStatus,FinalResult) VALUES(2,2,N'EM-2',1,N'E9',N'Bob',N'Pending',N'Pending');
UPDATE dbo.EM_Plans SET Status=N'Ready for Results' WHERE PlanID=1;
UPDATE dbo.EM_Events SET PlanID=1,AreaId=1,EmployeeId=N'E1',EmployeeName=N'Alice',WorkflowStatus=N'Reviewed',FinalResult=N'Results Entered' WHERE Id=1;
UPDATE dbo.EM_EventPlates SET PlanSampleID=1,Method=N'Personnel Monitoring' WHERE EventId=1;
UPDATE dbo.EM_PlanSamples SET PlanID=1,EventID=1,AreaID=1,Method=N'Personnel Monitoring',IsNegativeControl=0,Status=N'Ready for Results',EmployeeID=N'E1',EmployeeName=N'Alice' WHERE PlanSampleID=1;
INSERT dbo.EM_PlanSamples(PlanSampleID,PlanID,EventID,AreaID,Method,IsNegativeControl,Status,EmployeeID,EmployeeName) VALUES(20,2,2,1,N'Personnel Monitoring',0,N'Ready for Results',N'E9',N'Bob');",c){CommandTimeout=15};q.ExecuteNonQuery();
        }
        var invalid=new[]{
            new Case("missing link","UPDATE dbo.EM_EventPlates SET PlanSampleID=NULL WHERE Id=1"),
            new Case("unknown link","UPDATE dbo.EM_EventPlates SET PlanSampleID=999 WHERE Id=1"),
            new Case("other person's plan sample","UPDATE dbo.EM_EventPlates SET PlanSampleID=20 WHERE Id=1"),
            new Case("wrong plan","UPDATE dbo.EM_PlanSamples SET PlanID=2 WHERE PlanSampleID=1"),
            new Case("wrong event","UPDATE dbo.EM_PlanSamples SET EventID=2 WHERE PlanSampleID=1"),
            new Case("wrong area","UPDATE dbo.EM_PlanSamples SET AreaID=2 WHERE PlanSampleID=1"),
            new Case("changed planned method","UPDATE dbo.EM_PlanSamples SET Method=N'Settle Plate' WHERE PlanSampleID=1"),
            new Case("changed plate method","UPDATE dbo.EM_EventPlates SET Method=N'Settle Plate' WHERE Id=1"),
            new Case("negative-control link","UPDATE dbo.EM_PlanSamples SET IsNegativeControl=1 WHERE PlanSampleID=1"),
            new Case("cancelled sample","UPDATE dbo.EM_PlanSamples SET Status=N'Cancelled' WHERE PlanSampleID=1"),
            new Case("missing sample employee ID","UPDATE dbo.EM_PlanSamples SET EmployeeID=NULL WHERE PlanSampleID=1"),
            new Case("missing sample employee name","UPDATE dbo.EM_PlanSamples SET EmployeeName=NULL WHERE PlanSampleID=1"),
            new Case("contradictory event employee ID","UPDATE dbo.EM_Events SET EmployeeId=N'E9' WHERE Id=1"),
            new Case("contradictory event employee name","UPDATE dbo.EM_Events SET EmployeeName=N'Bob' WHERE Id=1"),
            new Case("partial event identity without ID","UPDATE dbo.EM_Events SET EmployeeId=NULL WHERE Id=1"),
            new Case("partial event identity without name","UPDATE dbo.EM_Events SET EmployeeName=NULL WHERE Id=1"),
            new Case("deleted plan sample","DELETE dbo.EM_PlanSamples WHERE PlanSampleID=1"),
            new Case("missing sample event","UPDATE dbo.EM_PlanSamples SET EventID=NULL WHERE PlanSampleID=1"),
            new Case("missing sample status","UPDATE dbo.EM_PlanSamples SET Status=NULL WHERE PlanSampleID=1"),
            new Case("unknown control status","UPDATE dbo.EM_PlanSamples SET IsNegativeControl=NULL WHERE PlanSampleID=1"),
            new Case("ordinal employee-ID distinction","UPDATE dbo.EM_Events SET EmployeeId=N'I' WHERE Id=1;UPDATE dbo.EM_PlanSamples SET EmployeeID=N'ı' WHERE PlanSampleID=1"),
            new Case("changed employee name case","UPDATE dbo.EM_PlanSamples SET EmployeeName=N'ALICE' WHERE PlanSampleID=1"),
            new Case("whitespace-only employee ID","UPDATE dbo.EM_PlanSamples SET EmployeeID=NCHAR(9) WHERE PlanSampleID=1")
        };
        var valid=new[]{
            new Case("individual with multiple plates per sample",""),
            new Case("completed parent and sample","UPDATE dbo.EM_Plans SET Status=N'Completed' WHERE PlanID=1;UPDATE dbo.EM_PlanSamples SET Status=N'Completed' WHERE PlanSampleID=1"),
            new Case("normalized employee ID case","UPDATE dbo.EM_PlanSamples SET EmployeeID=N'e1' WHERE PlanSampleID=1"),
            new Case("normalized surrounding identity spaces","UPDATE dbo.EM_Events SET EmployeeId=N' E1 ',EmployeeName=N' Alice ' WHERE Id=1"),
            new Case("valid historical aggregate identities","UPDATE dbo.EM_Events SET EmployeeId=NULL,EmployeeName=NULL WHERE Id=1;UPDATE dbo.EM_PlanSamples SET PlanID=1,EventID=1,EmployeeID=N'E2',EmployeeName=N'Bob' WHERE PlanSampleID=20;UPDATE dbo.EM_EventPlates SET PlanSampleID=20 WHERE Id=2"),
            new Case("non-personnel samples","UPDATE dbo.EM_Events SET EmployeeId=NULL,EmployeeName=NULL WHERE Id=1;UPDATE dbo.EM_EventPlates SET Method=N'Settle Plate';UPDATE dbo.EM_PlanSamples SET Method=N'Settle Plate',EmployeeID=NULL,EmployeeName=NULL WHERE PlanSampleID=1"),
            new Case("standalone Development remains allowed","UPDATE dbo.EM_Events SET PlanID=NULL WHERE Id=1",false,0),
            new Case("distinct samples for the same individual","UPDATE dbo.EM_PlanSamples SET PlanID=1,EventID=1,EmployeeID=N'E1',EmployeeName=N'Alice' WHERE PlanSampleID=20;UPDATE dbo.EM_EventPlates SET PlanSampleID=20 WHERE Id=2"),
            new Case("ordinal Unicode case folding","UPDATE dbo.EM_Events SET EmployeeId=N'Å1' WHERE Id=1;UPDATE dbo.EM_PlanSamples SET EmployeeID=N'å1' WHERE PlanSampleID=1")
        };
        var existingGates=new[]{
            new Case("cancelled plan","UPDATE dbo.EM_Plans SET Status=N'Cancelled' WHERE PlanID=1"),
            new Case("unreleased plan","UPDATE dbo.EM_Plans SET Status=N'Planned' WHERE PlanID=1"),
            new Case("cancelled event workflow","UPDATE dbo.EM_Events SET WorkflowStatus=N'Cancelled' WHERE Id=1"),
            new Case("cancelled event interpretation","UPDATE dbo.EM_Events SET FinalResult=N'Cancelled' WHERE Id=1"),
            new Case("Production requires plan","UPDATE dbo.EM_Events SET PlanID=NULL WHERE Id=1"),
            new Case("missing parent event","DELETE dbo.EM_Events WHERE Id=1")
        };
        int frozenUnsafe=0;
        foreach(bool fix in corrected?new[]{false,true}:new[]{false})
        {
            int rejected=0;
            foreach(var test in invalid)
            {
                bool reject=Probe(cs,test,fix);
                if(fix&&!reject)throw new Exception("Invalid membership accepted: "+test.Name);
                if(!fix&&reject)throw new Exception("Frozen gap did not reproduce: "+test.Name);
                if(reject)rejected++;else frozenUnsafe++;
            }
            foreach(var test in valid)if(Probe(cs,test,fix))throw new Exception("Valid case rejected: "+test.Name);
            foreach(var test in existingGates)if(!Probe(cs,test,fix))throw new Exception("Existing gate regressed: "+test.Name);
            Console.WriteLine($"SITE EM MEMBERSHIP {(fix?"corrected":"frozen")}: {rejected}/23 invalid links rejected; 9 valid identity/history controls and 6 existing gates preserved. Exact readonly SQL guard; probes rolled back.");
        }
        if(frozenUnsafe!=23)throw new Exception("Incomplete frozen membership reproduction.");
        if(corrected)VerifyLocks(cs);
        Console.WriteLine(corrected?"PASS SITE EM MEMBERSHIP: 23/23 corrected rejections, 9 valid histories, 6 existing gates, retained membership locks; original records untouched.":"PASS SITE EM MEMBERSHIP RED: 23 unsafe membership acceptances reproduced; 9 valid histories and 6 existing gates retained. No corrected acceptance claimed.");
    }

    private static bool Probe(string cs,Case test,bool corrected)
    {
        using var c=new SqlConnection(cs);c.Open();using var tx=c.BeginTransaction();
        try
        {
            if(test.Sql.Length!=0){using var change=new SqlCommand(test.Sql,c,tx);change.ExecuteNonQuery();}
            string before=Fingerprint(c,tx);bool rejected=false;
            try
            {
                int plan=corrected?Corrected.EnsureEmSourcePlanInTransaction(c,tx,1,test.Native):Frozen.EnsureEmSourcePlanInTransaction(c,tx,1,test.Native);
                if(plan!=test.ExpectedPlan)throw new Exception("Wrong returned plan.");
            }
            catch(SqlException ex) when(ex.Number==55515){rejected=true;}
            catch(InvalidOperationException){rejected=true;}
            catch(DBConcurrencyException){rejected=true;}
            if(before!=Fingerprint(c,tx))throw new Exception("Readonly guard changed fixture data.");
            return rejected;
        }
        finally{tx.Rollback();}
    }

    private static string Fingerprint(SqlConnection c,SqlTransaction tx)
    {
        using var q=new SqlCommand(@"SELECT (SELECT * FROM dbo.EM_Events ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES)+N'|'+(SELECT * FROM dbo.EM_EventPlates ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES)+N'|'+(SELECT * FROM dbo.EM_PlanSamples ORDER BY PlanSampleID FOR JSON PATH,INCLUDE_NULL_VALUES)",c,tx);
        return (string)q.ExecuteScalar()!;
    }

    private static void VerifyLocks(string cs)
    {
        using var c=new SqlConnection(cs);c.Open();using var tx=c.BeginTransaction();
        Corrected.EnsureEmSourcePlanInTransaction(c,tx,1,true);int rejected=0;
        foreach(string sql in new[]{"UPDATE dbo.EM_PlanSamples SET EmployeeName=N'changed' WHERE PlanSampleID=1","UPDATE dbo.EM_EventPlates SET PlanSampleID=20 WHERE Id=1","INSERT dbo.EM_EventPlates(Id,EventId,Method,PlanSampleID) VALUES(30,1,N'Personnel Monitoring',1)"})
        {
            using var other=new SqlConnection(cs);other.Open();using var change=new SqlCommand("SET LOCK_TIMEOUT 300; "+sql,other){CommandTimeout=5};
            try{change.ExecuteNonQuery();throw new Exception("Concurrent membership writer escaped locks.");}
            catch(SqlException ex) when(ex.Number==1222){rejected++;}
        }
        tx.Rollback();if(rejected!=3)throw new Exception("Incomplete membership lock controls.");
    }

    private static string Method(string source,string signature)
    {
        string code=Regex.Replace(source,"@\"(?:\"\"|[^\"])*\"|\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/",m=>new string(' ',m.Length));
        int start=code.IndexOf(signature,StringComparison.Ordinal);if(start<0)throw new Exception("Missing source: "+signature);
        int begin=code.IndexOf('{',start),depth=1,end=begin+1;
        while(depth!=0&&end<code.Length){depth+=code[end]=='{'?1:code[end]=='}'?-1:0;end++;}
        if(depth!=0)throw new Exception("Unbalanced source.");return source[start..end];
    }

    private static void VerifySources(string root,bool corrected)
    {
        string source=File.ReadAllText(Path.Combine(root,"tests/PharmaLIMS.EmLifecycleRegression/SiteEmMembershipMethods.cs")).Replace("\r\n","\n");
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/AMEEN-EM-membership-20261030.json")));
        string frozen=Method(source,"private static class Frozen");
        foreach(var entry in new[]{("Lock","internal static void LockEmSourcePlanInTransaction("),("Guard","internal static int EnsureEmSourcePlanInTransaction(")})
            if(Method(frozen,entry.Item2)!=fixture.RootElement.GetProperty(entry.Item1).GetString())throw new Exception("Frozen compiled SQL method drift.");
        if(corrected)
        {
            string site=File.ReadAllText(Path.Combine(root,"scripts/site-overlays/AMEEN-DatabaseHelper.EmSourcePlan.cs.txt")).Replace("\r\n","\n");
            string compiled=Method(source,"private static class Corrected");
            foreach(string sig in new[]{"internal static void LockEmSourcePlanInTransaction(","internal static int EnsureEmSourcePlanInTransaction("})
                if(Method(site,sig)!=Method(compiled,sig))throw new Exception("Corrected compiled SQL guard differs from the complete artifact.");
        }
    }
}
