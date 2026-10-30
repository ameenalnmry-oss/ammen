using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System.Data;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class SiteCultureEvidenceCases
{
    internal static void Run(SqlConnection c,string root)
    {
        if(!Regex.IsMatch(c.Database,@"^MediaAmendment_[0-9a-f]{32}$"))throw new Exception("Culture probes require the existing owned database.");
        VerifySources(root);
        CultureChildEvidenceProbe.Run(false);CultureVisibleEvidenceProbe.Run();CultureSigningScopeProbe.Run();
        // Existing-table DDL must finish before statements bind the new columns.
        Sql(c,null,@"ALTER TABLE dbo.MediaPreparations ADD WorkflowRowVersion rowversion,MediaPreparationNo nvarchar(50),MediaID int,PreparationDate date,ExpiryDate date,QuantityPrepared nvarchar(80),PreparedBy nvarchar(100),BatchSize nvarchar(80),Appearance nvarchar(200),AutoclaveCycleNo nvarchar(100),AutoclaveTemperature nvarchar(50),AutoclaveHoldingTime nvarchar(50),Remarks nvarchar(500);");
        Sql(c,null,@"UPDATE dbo.MediaPreparations SET MediaPreparationNo=N'MP-1',MediaID=1,PreparationDate=CONVERT(date,SYSDATETIME()),ExpiryDate=DATEADD(day,30,CONVERT(date,SYSDATETIME())),QuantityPrepared=N'1000 mL',PreparedBy=N'preparer',BatchSize=N'1000 mL',Appearance=N'clear',AutoclaveCycleNo=N'C1',AutoclaveTemperature=N'121',AutoclaveHoldingTime=N'15',Remarks=N'controlled preparation' WHERE MediaPreparationID=1;");
        Sql(c,null,@"CREATE TABLE dbo.CultureMediaLots(MediaLotID int PRIMARY KEY,ReceiptStatus nvarchar(50),ExpiryDate date);INSERT dbo.CultureMediaLots VALUES(10,N'Released',DATEADD(day,30,CONVERT(date,SYSDATETIME())));");
        using(var tx=c.BeginTransaction())
        {
            foreach(var field in CultureVisibleEvidenceProbe.Sop(true))
            {
                if(field.Key.Item2=="LoadNo")continue;
                using var cmd=new SqlCommand("INSERT dbo.CultureMediaSopFields(EntityType,EntityID,AnnexureCode,FieldName,FieldValue) VALUES(N'MediaPreparation',1,@Annexure,@Field,@Value)",c,tx);
                cmd.Parameters.AddWithValue("@Annexure",field.Key.Item1);cmd.Parameters.AddWithValue("@Field",field.Key.Item2);cmd.Parameters.AddWithValue("@Value",field.Value);cmd.ExecuteNonQuery();
            }
            tx.Commit(); // Setup only in the owned fixture, dropped by caller finally.
        }
        using var frozen=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/AMEEN-Culture-evidence-20261030.json")));
        string lockSql=frozen.RootElement.GetProperty("AmendmentLockSql").GetString()!;
        string[] childChanges={
            "UPDATE dbo.CultureMediaSopFields SET FieldValue=N'LOAD-NEW' WHERE FieldName=N'LoadNo'",
            "INSERT dbo.CultureMediaSopFields(EntityType,EntityID,AnnexureCode,FieldName,FieldValue) VALUES(N'MediaPreparation',1,N'1035-L-0005/A6',N'ExtraEvidence',N'new')",
            "DELETE dbo.CultureMediaSopFields WHERE EntityID=1",
            "UPDATE dbo.CultureMediaSignatures SET SignedBy=N'other preparer' WHERE EntityID=1",
            "INSERT dbo.CultureMediaSignatures(EntityType,EntityID,ActionType,SignedBy) VALUES(N'MediaPreparation',1,N'Visual Check Prepared Media',N'reviewer')",
            "DELETE dbo.CultureMediaSignatures WHERE EntityID=1"
        };
        int oldAccepted=0,fixedRejected=0;
        foreach(string mutation in childChanges)
        {
            using var tx=c.BeginTransaction();var loaded=Read(c,tx);var version=(byte[])loaded.Preparation["WorkflowRowVersion"];
            Sql(c,tx,mutation);
            bool oldRejected=Reject(()=>OldGuard(c,tx,lockSql,version));
            if(!oldRejected)oldAccepted++;
            var current=Read(c,tx);
            if(Reject(()=>MediaPreparationAmendmentSnapshot.EnsureCanAmend(loaded,current)))fixedRejected++;
            if(!Reject(()=>MediaPreparationAmendmentSnapshot.EnsureUnchanged(loaded,current)))throw new Exception("Changed child evidence reached reference review/release boundary.");
            tx.Rollback();
        }
        if(oldAccepted!=6||fixedRejected!=6)throw new Exception($"Culture native child matrix: old acceptance{oldAccepted}, fixed rejection{fixedRejected}.");
        CultureUiBindingProbe.Run(c);
        VerifyReleaseGates(c);
        VerifyOwnVisualContinuation(c);
        Console.WriteLine("PASS SITE CULTURE SQL: actual old amendment SELECT/byte guard accepted6 child-only changes; complete site snapshot rejected6/6; actual SQL-backed UI load/failure/new handlers passed. No WPF authentication claim.");
    }

    private static void VerifyReleaseGates(SqlConnection c)
    {
        foreach(string mutation in new[]{
            "UPDATE dbo.MediaPreparations SET ExpiryDate=DATEADD(day,-1,CONVERT(date,SYSDATETIME())) WHERE MediaPreparationID=1",
            "UPDATE dbo.CultureMediaLots SET ReceiptStatus=N'Quarantine' WHERE MediaLotID=10",
            "UPDATE dbo.CultureMediaLots SET ExpiryDate=DATEADD(day,-1,CONVERT(date,SYSDATETIME())) WHERE MediaLotID=10",
            "UPDATE dbo.MediaPreparations SET VisualCheckedBy=NULL WHERE MediaPreparationID=1",
            "UPDATE dbo.MediaPreparations SET VisualCheckedAt=NULL WHERE MediaPreparationID=1",
            "UPDATE dbo.CultureMediaSopFields SET FieldValue=N'Unsatisfactory' WHERE AnnexureCode=N'1035-L-0005/A7' AND FieldName=N'Conclusion'",
            "UPDATE dbo.MediaPreparations SET ReleaseStatus=N'Released' WHERE MediaPreparationID=1"})
        {
            using var tx=c.BeginTransaction();Sql(c,tx,"UPDATE dbo.MediaPreparations SET VisualCheckedBy=N'reviewer',VisualCheckedAt=SYSUTCDATETIME() WHERE MediaPreparationID=1");
            var original=Read(c,tx);CultureReleaseGateProbe.Validate(c,tx);MediaPreparationAmendmentSnapshot.EnsureCanRelease(original,Read(c,tx));
            Sql(c,tx,mutation);if(!Reject(()=>CultureReleaseGateProbe.Validate(c,tx)))throw new Exception("An existing expiry/source/visual release gate regressed.");tx.Rollback();
        }
        Console.WriteLine("PASS SITE CULTURE RELEASE: seven actual unchanged SQL expiry/source/visual/status gates retained; satisfactory control accepted.");
    }

    private static void VerifyOwnVisualContinuation(SqlConnection c)
    {
        MediaPreparationAmendmentSnapshot before,committed;
        using(var tx=c.BeginTransaction())
        {
            before=Read(c,tx);MediaPreparationAmendmentSnapshot.EnsureCanVisualCheck(before,before);
            Sql(c,tx,@"UPDATE dbo.MediaPreparations SET VisualCheckedBy=N'reviewer',VisualCheckedAt=SYSUTCDATETIME() WHERE MediaPreparationID=1;
UPDATE dbo.CultureMediaSopFields SET FieldValue=N'reviewer' WHERE AnnexureCode=N'1035-L-0005/A7' AND FieldName=N'CheckedBy';
INSERT dbo.CultureMediaSignatures(EntityType,EntityID,ActionType,SignedBy) VALUES(N'MediaPreparation',1,N'Visual Check Prepared Media',N'reviewer');");
            committed=Read(c,tx);tx.Commit();
        }
        using(var tx=c.BeginTransaction())
        {
            var now=Read(c,tx);
            if(!Reject(()=>MediaPreparationAmendmentSnapshot.EnsureCanRelease(before,now)))throw new Exception("Old pre-visual evidence allowed release.");
            MediaPreparationAmendmentSnapshot.EnsureCanRelease(committed,now);CultureReleaseGateProbe.Validate(c,tx);
            if(!Reject(()=>MediaPreparationAmendmentSnapshot.EnsureCanVisualCheck(committed,now)))throw new Exception("Visual review could be signed twice.");
            if(!Reject(()=>MediaPreparationAmendmentSnapshot.EnsureCanAmend(committed,now)))throw new Exception("Signed preparation was amendable.");
            tx.Rollback();
        }
        Console.WriteLine("PASS SITE CULTURE continuation: committed visual evidence permits subsequent release; old token, duplicate visual and signed amendment rejected. Synthetic owned SQL fixture, not signer authentication.");
    }

    private static void Sql(SqlConnection c,SqlTransaction? tx,string sql){using var cmd=new SqlCommand(sql,c,tx){CommandTimeout=15};cmd.ExecuteNonQuery();}
    private static MediaPreparationAmendmentSnapshot Read(SqlConnection c,SqlTransaction tx)=>MediaPreparationAmendmentSnapshot.Read(c,tx,1,15);
    private static bool Reject(Action action)=>CultureVisibleEvidenceProbe.Reject(action);
    private static void OldGuard(SqlConnection c,SqlTransaction tx,string sql,byte[] loaded)
    {
        using var cmd=new SqlCommand(sql,c,tx);cmd.Parameters.AddWithValue("@MediaPreparationID",1);using var r=cmd.ExecuteReader();
        if(!r.Read())throw new DBConcurrencyException("Missing preparation.");
        bool signed=!string.IsNullOrWhiteSpace(Convert.ToString(r.GetValue(3)))||!r.IsDBNull(4)||!string.IsNullOrWhiteSpace(Convert.ToString(r.GetValue(5)))||!r.IsDBNull(6);
        CultureMediaPreparationGuard.EnsureAmendable(loaded,(byte[])r.GetValue(2),signed);
    }

    private static string Code(string source)=>Regex.Replace(source,"@\"(?:\"\"|[^\"])*\"|\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/",m=>new string(' ',m.Length));
    private static string Method(string source,string signature)
    {
        string code=Code(source);int start=code.IndexOf(signature,StringComparison.Ordinal);if(start<0)throw new Exception("Missing source "+signature);
        int begin=code.IndexOf('{',start),end=begin+1,depth=1;
        while(depth!=0&&end<code.Length){depth+=code[end]=='{'?1:code[end]=='}'?-1:0;end++;}
        if(depth!=0)throw new Exception("Unbalanced method.");return source[start..end];
    }
    private static void VerifySources(string root)
    {
        string Site(string name)=>File.ReadAllText(Path.Combine(root,"scripts/site-overlays/AMEEN-"+name+".txt")).Replace("\r\n","\n");
        string part=Site("CultureMediaPreparation.xaml.Part2.cs"),sop=Site("CultureMediaPreparation.xaml.Part3.cs");
        string compiled=File.ReadAllText(Path.Combine(root,"tests/PharmaLIMS.MediaAmendmentRegression/CultureUiBindingMethods.cs")).Replace("\r\n","\n");
        string fixedUi=Method(compiled,"private sealed class Corrected"),oldUi=Method(compiled,"private sealed class Frozen");
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/AMEEN-Culture-evidence-20261030.json")));
        foreach(var entry in new[]{("private void DgPreparations_SelectionChanged(",part),("private void LoadPreparationSopFields(",sop),("private void BtnNewPreparation_Click(",part)})
        {
            if(Method(fixedUi,entry.Item1)!=Method(entry.Item2,entry.Item1))throw new Exception("Actual compiled UI method differs from complete artifact.");
            if(Method(oldUi,entry.Item1)!=fixture.RootElement.GetProperty("UiMethods").GetProperty(entry.Item1).GetString())throw new Exception("Frozen UI method drift.");
        }
        if(Method(fixedUi,"private void InvalidatePreparationEvidence(")!=Method(sop,"private void InvalidatePreparationEvidence("))throw new Exception("Compiled invalidation differs.");
        if(Method(fixedUi,"private void EnsurePreparationSigningScope(")!=Method(sop,"private void EnsurePreparationSigningScope("))throw new Exception("Compiled signing scope differs.");
        string[] signatures={"private void BtnSavePreparation_Click(","private void BtnSignPreparationVisualCheck_Click(","private void BtnReleasePreparation_Click("};
        foreach(string signature in signatures)
        {
            string method=Code(Method(part,signature));
            int capture=method.IndexOf("var expectedPreparation = _loadedPreparationSnapshot",StringComparison.Ordinal),modal=method.IndexOf("ConfirmCultureMediaSignature",StringComparison.Ordinal),scope=method.IndexOf("EnsurePreparationSigningScope(targetId, displayedNo, expectedPreparation)",StringComparison.Ordinal),tx=method.IndexOf("DatabaseHelper.ExecuteInTransaction",StringComparison.Ordinal);
            if(!(capture>=0&&capture<modal&&modal<scope&&scope<tx)||!method.Contains("EnsureInputsUnchanged(signedInputs",StringComparison.Ordinal))throw new Exception("Signing target/UI input is not frozen around the modal boundary.");
            if(!method.Contains("originalEvidence.AuditValue, savedEvidence.AuditValue",StringComparison.Ordinal)&&!method.Contains("originalEvidence?.AuditValue ?? string.Empty, savedEvidence.AuditValue",StringComparison.Ordinal))throw new Exception("Complete actual old/new audit is not wired.");
            string guard=signature.Contains("BtnSave")?"EnsureCanAmend":signature.Contains("Visual")?"EnsureCanVisualCheck":"EnsureCanRelease";
            int gate=method.IndexOf("MediaPreparationAmendmentSnapshot."+guard+"(expectedPreparation, originalEvidence)",StringComparison.Ordinal);
            if(gate<tx)throw new Exception("Complete loaded evidence comparison missing inside transaction.");
            int write=method.IndexOf(signature.Contains("BtnSave")?"const string updateSql":signature.Contains("Visual")?"SaveSopFieldInTransaction":"ValidatePreparationReleaseGateInTransaction",gate,StringComparison.Ordinal);
            if(write<gate)throw new Exception("Evidence guard does not precede controlled writes/gates.");
            if(!method.Contains("InvalidatePreparationEvidence()",StringComparison.Ordinal)||!method.Contains("committed = true",StringComparison.Ordinal))throw new Exception("Post-commit/failure state not explicit.");
        }
        string load=Code(Method(part,"private void DgPreparations_SelectionChanged("));
        int binding=load.IndexOf("LoadPreparationSopFields",StringComparison.Ordinal),publish=load.IndexOf("_loadedPreparationSnapshot = loaded",StringComparison.Ordinal);
        if(binding<0||publish<binding||load.Contains("LoadSopField(",StringComparison.Ordinal))throw new Exception("Complete evidence publishes before binding.");
        Console.WriteLine("PASS SITE CULTURE source provenance: actual compiled load/SOP/new/invalidation methods match artifacts; three signing callers freeze scope, validate inputs/evidence and retain complete audits.");
    }
}
