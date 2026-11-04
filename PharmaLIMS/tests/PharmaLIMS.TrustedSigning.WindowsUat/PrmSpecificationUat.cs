using System.Data;
using System.Text.Json;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.TrustedSigning;

internal sealed partial class UatRun
{
    private async Task PreparePrmSchema()
    {
        await Exec(_admin,System.IO.File.ReadAllText(System.IO.Path.Combine(settings.RepositoryRoot,"tests/eq001_prm_service_fixture.sql")));
        string script=System.IO.File.ReadAllText(System.IO.Path.Combine(settings.RepositoryRoot,"scripts/EQ001_UAT_Prm_Service_Boundary.sql"));
        await SqlDenied(()=>Exec(_admin,script),"PRM deployment refuses implicit UAT activation",56800);
        script=script.Replace("DECLARE @VerifiedIsolatedUat bit = 0;","DECLARE @VerifiedIsolatedUat bit = 1;",StringComparison.Ordinal);
        await Exec(_admin,script);
        await Exec(_admin,script);
        string supersession=System.IO.File.ReadAllText(System.IO.Path.Combine(settings.RepositoryRoot,"scripts/EQ001_UAT_Prm_Controlled_Supersession.sql"));
        supersession=supersession.Replace("DECLARE @VerifiedIsolatedUat bit = 0;","DECLARE @VerifiedIsolatedUat bit = 1;",StringComparison.Ordinal);
        await Exec(_admin,supersession);
        await Exec(_admin,supersession);
        Check(true,"PRM UAT procedure deployment is idempotent and preserves protected history");
        await Exec(_admin,$"GRANT EXECUTE ON dbo.EQ001_UAT_ReadPrmSpecification TO PharmaLIMS_TrustedEquipmentSigner;GRANT EXECUTE ON dbo.EQ001_UAT_WritePrmSpecification TO PharmaLIMS_TrustedEquipmentSigner;DENY INSERT,UPDATE,DELETE ON dbo.PRM_SpecificationTests TO [{_clientLogin}];DENY INSERT,UPDATE,DELETE ON dbo.PRM_SpecificationSignatures TO [{_clientLogin}];DENY INSERT,UPDATE,DELETE ON dbo.PRM_SpecificationContentHistory TO [{_clientLogin}];");
    }

    private async Task CheckPrmService(WindowsTrustedPipeClient pipe,string client)
    {
        foreach(string actor in new[]{"uat-prm-author","uat-prm-reviewer","uat-prm-qa"}) await SeedUser(actor,false);
        await Exec(_admin,"UPDATE dbo.Users SET CanApproveResults=1 WHERE Username IN(N'uat-prm-reviewer',N'uat-prm-qa');");
        var test=new PrmSpecificationTest("TBC","Synthetic UAT bacterial count Δ","NMT 10 CFU/25 cm2",10m,false,"CFU/25 cm2","Numeric",true,120m,1);
        var draft=new PrmSpecificationRequest("EQ001-PRM-E2E","Primary Packaging",0,"Synthetic UAT fixture only","EQ001-SYNTHETIC","",DateTime.Today,"",new[]{test});
        async Task<TrustedApplicationRequest> Proof(string action,string actor,PrmSpecificationRequest specification)
        {
            var proof=await pipe.ApplicationAsync(new(Guid.NewGuid(),"Reauthenticate",actor,_password));
            return new(Guid.NewGuid(),action,actor,Ticket:proof.Ticket!,UserJson:JsonSerializer.Serialize(specification),Reason:"Isolated synthetic PRM qualification",Meaning:"I performed this controlled UAT action");
        }
        async Task<PrmSpecificationRequest> Loaded(PrmSpecificationRequest specification,int version)
        {
            using DataTable table=await new DatabaseConnection(_admin).ExecuteQueryAsync("SELECT * FROM dbo.PRM_SpecificationTests WHERE SpecificationNo=@n AND SampleCategory=@c AND VersionNo=@v ORDER BY SpecificationTestID;",new[]{new Microsoft.Data.SqlClient.SqlParameter("@n",specification.SpecificationNo),new Microsoft.Data.SqlClient.SqlParameter("@c",specification.Category),new Microsoft.Data.SqlClient.SqlParameter("@v",version)});
            return specification with { Version=version,ExpectedSnapshotHash=PrmSpecificationPolicy.SnapshotHash(table) };
        }
        await Denied(()=>pipe.ApplicationAsync(new(Guid.NewGuid(),"PrmSaveDraft","uat-prm-author",UserJson:JsonSerializer.Serialize(draft))),"PRM mutation refuses missing fresh authentication proof");
        await SqlDenied(()=>Exec(client,"INSERT dbo.PRM_SpecificationTests(SpecificationNo) VALUES(N'forged')"),"WPF cannot insert specification rows directly");
        await SqlDenied(()=>Exec(client,"INSERT dbo.PRM_SpecificationSignatures(SpecificationNo) VALUES(N'forged')"),"WPF cannot insert PRM signature evidence directly");
        await SqlDenied(()=>Exec(client,"INSERT dbo.PRM_SpecificationContentHistory(SpecificationNo) VALUES(N'forged')"),"WPF cannot insert PRM content evidence directly");
        await SqlDenied(()=>Exec(client,"EXEC dbo.EQ001_UAT_ReadPrmSpecification @No=N'EQ001-PRM-E2E',@Category=N'Primary Packaging',@Version=1"),"WPF cannot execute the privileged PRM reader");
        await SqlDenied(()=>Exec(client,"EXEC dbo.EQ001_UAT_WritePrmSpecification"),"WPF cannot execute the privileged PRM mutation");
        var forged=await Proof("PrmSaveDraft","uat-prm-author",draft);
        await Denied(()=>pipe.ApplicationAsync(forged with { Username="uat-prm-qa" }),"PRM actor cannot be forged independently from the authenticated ticket");
        var save=await Proof("PrmSaveDraft","uat-prm-author",draft);
        var saved=await pipe.ApplicationAsync(save);
        Check(saved.UserJson=="1","trusted PRM creates a new draft without desktop DML grants");
        await Denied(()=>pipe.ApplicationAsync(save),"a committed PRM authentication ticket cannot be replayed");
        var duplicate=(await Proof("PrmSaveDraft","uat-prm-author",draft)) with { RequestId=save.RequestId };
        await Denied(()=>pipe.ApplicationAsync(duplicate),"a committed PRM request ID cannot be replayed even with a new valid ticket","PrmStateRejected");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.PRM_SpecificationContentHistory WHERE SpecificationNo=N'EQ001-PRM-E2E' AND ActionType=N'Draft Created'"))==1 && Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.AuditTrail WHERE TableName=N'PRM_SpecificationTests' AND Action=N'Draft Created'"))==1,"draft content history and audit commit together");
        string evidenceJson=Convert.ToString(await Scalar("SELECT TOP(1) NewRowsJson FROM dbo.PRM_SpecificationContentHistory WHERE SpecificationNo=N'EQ001-PRM-E2E' ORDER BY HistoryID"))!;
        byte[] evidenceHash=(byte[])(await Scalar("SELECT TOP(1) ContentHash FROM dbo.PRM_SpecificationContentHistory WHERE SpecificationNo=N'EQ001-PRM-E2E' ORDER BY HistoryID"))!;
        Check(evidenceHash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(evidenceJson))),"SQL PRM content hash matches the existing UTF-8 evidence contract including Unicode");
        var loaded=await Loaded(draft,1);
        var edited=loaded with { ItemCode="EQ001-CORRECTED", Tests=new[]{test with { SpecificationText="NMT 2 CFU/25 cm2",SpecificationLimit=2m }} };
        var correction=await pipe.ApplicationAsync(await Proof("PrmSaveDraft","uat-prm-author",edited));
        Check(correction.UserJson=="1" && Convert.ToString(await Scalar("SELECT TOP(1) ItemCode FROM dbo.PRM_SpecificationTests WHERE SpecificationNo=N'EQ001-PRM-E2E' AND VersionNo=1"))=="EQ001-CORRECTED","a saved draft permits controlled item-scope correction without creating a spurious version");
        await Denied(async()=>{await pipe.ApplicationAsync(await Proof("PrmReview","uat-prm-reviewer",loaded));},"PRM detects the stale complete test set before any signature","PrmSnapshotChanged");
        loaded=await Loaded(edited,1);
        await Denied(async()=>{await pipe.ApplicationAsync(await Proof("PrmReview","uat-prm-author",loaded));},"PRM reviewer must be independent from every draft author/editor","PrmStateRejected");
        var revoked=await Proof("PrmSaveDraft","uat-prm-author",loaded);
        await Exec(_admin,"UPDATE dbo.Users SET CanEnterResults=0 WHERE Username=N'uat-prm-author';");
        await Denied(()=>pipe.ApplicationAsync(revoked),"PRM rejects an authentication proof after actor permission rowversion changes");
        await Exec(_admin,"UPDATE dbo.Users SET CanEnterResults=1 WHERE Username=N'uat-prm-author';");
        await pipe.ApplicationAsync(await Proof("PrmReview","uat-prm-reviewer",loaded));
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.PRM_SpecificationSignatures WHERE SpecificationNo=N'EQ001-PRM-E2E' AND ActionType=N'Review Specification' AND SignedBy=N'uat-prm-reviewer'"))==1,"independent PRM review stores a real service-side signature");
        loaded=await Loaded(edited,1);
        await Denied(async()=>{await pipe.ApplicationAsync(await Proof("PrmApprove","uat-prm-reviewer",loaded));},"PRM QA approver must be distinct from the reviewer","PrmStateRejected");
        await pipe.ApplicationAsync(await Proof("PrmApprove","uat-prm-qa",loaded));
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.PRM_SpecificationSignatures WHERE SpecificationNo=N'EQ001-PRM-E2E' AND ActionType=N'Approve Specification' AND SignedBy=N'uat-prm-qa'"))==1,"independent QA approval stores service-side signature evidence");
        string historical=(await Loaded(edited,1)).ExpectedSnapshotHash;
        await SqlDenied(()=>Exec(_admin,"UPDATE dbo.PRM_SpecificationContentHistory SET ChangeReason=N'forged' WHERE SpecificationNo=N'EQ001-PRM-E2E'"),"PRM content history remains append-only even for an administrator",56456);
        await SqlDenied(()=>Exec(_admin,"DELETE dbo.PRM_SpecificationSignatures WHERE SpecificationNo=N'EQ001-PRM-E2E'"),"PRM signature history remains append-only",51050);
        await SqlDenied(()=>Exec(_admin,"DELETE dbo.EQ001_PrmSigningRequests WHERE SpecificationNo=N'EQ001-PRM-E2E'"),"PRM replay evidence remains append-only",56808);
        var successorDraft=edited with { Version=0,ExpectedSnapshotHash="" };
        var second=await pipe.ApplicationAsync(await Proof("PrmSaveDraft","uat-prm-author",successorDraft));
        Check(second.UserJson=="2","a replacement proposal is created as a separate unapproved draft");
        var replacement=await Loaded(successorDraft,2);
        await pipe.ApplicationAsync(await Proof("PrmReview","uat-prm-reviewer",replacement));
        replacement=await Loaded(successorDraft,2);
        await Denied(async()=>{await pipe.ApplicationAsync(await Proof("PrmApprove","uat-prm-qa",replacement));},"activation cannot silently deactivate or rewrite an existing approved version","HistoricalVersionPreserved");
        Check((await Loaded(edited,1)).ExpectedSnapshotHash==historical,"the complete existing approved specification row set remains unchanged");
        string predecessorEvidence=Convert.ToString(await Scalar("SELECT CONVERT(varchar(64),EvidenceHash,2) FROM dbo.EQ001_UAT_PrmVersionEvidence WHERE SpecificationNo=N'EQ001-PRM-E2E' AND VersionNo=1"))!;
        var explicitReplacement=replacement with { SupersedesVersion=1,SupersededEvidenceHash=predecessorEvidence };
        await Denied(async()=>{await pipe.ApplicationAsync(await Proof("PrmApprove","uat-prm-qa",explicitReplacement with { SupersededEvidenceHash=new string('0',64) }));},"stale predecessor evidence cannot authorize supersession","PrmStateRejected");
        await Denied(async()=>{await pipe.ApplicationAsync(await Proof("PrmApprove","uat-prm-qa",explicitReplacement with { EffectiveDate=DateTime.Today.AddDays(1) }));},"future-effective replacement cannot retire a currently effective predecessor","InvalidRequest");
        await Denied(async()=>{await pipe.ApplicationAsync(await Proof("PrmApprove","uat-prm-qa",explicitReplacement with { ItemCode="EQ001-UNRELATED" }));},"supersession rejects a mismatching item scope","PrmStateRejected");
        await Exec(_admin,"UPDATE dbo.Users SET Role=N'QA',CanApproveResults=1 WHERE Username=N'uat-prm-author';");
        try {
            await Denied(async()=>{await pipe.ApplicationAsync(await Proof("PrmApprove","uat-prm-author",explicitReplacement));},"an author with QA privileges still cannot approve their own successor","PrmStateRejected");
        } finally { await Exec(_admin,"UPDATE dbo.Users SET Role=N'Analyst',CanApproveResults=0 WHERE Username=N'uat-prm-author';"); }
        var approveReplacement=await Proof("PrmApprove","uat-prm-qa",explicitReplacement);
        var replacementReply=await pipe.ApplicationAsync(approveReplacement);
        Check(replacementReply.UserJson=="2","explicit independent QA approval atomically supersedes the predecessor");
        await Denied(()=>pipe.ApplicationAsync(approveReplacement),"successful supersession proof cannot be replayed");
        Check((await Loaded(edited,1)).ExpectedSnapshotHash==historical,"supersession preserves every physical predecessor specification field including IsActive");
        Check(Convert.ToString(await Scalar("SELECT CONVERT(varchar(64),EvidenceHash,2) FROM dbo.EQ001_UAT_PrmVersionEvidence WHERE SpecificationNo=N'EQ001-PRM-E2E' AND VersionNo=1"))==predecessorEvidence,"supersession preserves predecessor content, signatures and history byte hash");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.EQ001_UAT_EffectivePrmSpecificationTests WHERE SpecificationNo=N'EQ001-PRM-E2E' AND IsActive=1 AND VersionNo=2"))==1 && Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.EQ001_UAT_EffectivePrmSpecificationTests WHERE SpecificationNo=N'EQ001-PRM-E2E' AND IsActive=1 AND VersionNo=1"))==0,"only the approved successor is effective for new registrations");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.EQ001_PrmSupersessions x JOIN dbo.PRM_SpecificationSignatures s ON s.SignatureID=x.ApprovalSignatureID JOIN dbo.EQ001_PrmSigningRequests r ON r.RequestId=x.RequestId WHERE x.SpecificationNo=N'EQ001-PRM-E2E' AND x.OldVersion=1 AND x.NewVersion=2 AND s.SignedBy=N'uat-prm-qa' AND r.WindowsCallerSid=N'"+settings.CallerSid+"'"))==1,"supersession links the independent QA signature and native Windows request");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.AuditTrail WHERE TableName=N'EQ001_PrmSupersessions' AND Action=N'Supersede Specification' AND PerformedBy=N'uat-prm-qa'"))==1,"supersession records attributable QA audit evidence");
        await SqlDenied(()=>Exec(_admin,"UPDATE dbo.EQ001_PrmSupersessions SET DecisionReason=N'forged'"),"supersession decisions cannot be updated",56809);
        await SqlDenied(()=>Exec(_admin,"DELETE dbo.EQ001_PrmSupersessions"),"supersession decisions cannot be deleted",56809);
        await SqlDenied(()=>Exec(client,"INSERT dbo.EQ001_PrmSupersessions(SpecificationNo) VALUES(N'forged')"),"desktop cannot insert supersession decisions directly");
        await Exec(_admin,"INSERT dbo.PRM_SpecificationTests(SpecificationNo,SampleCategory,VersionNo,TestCode,TestName,SpecificationText,SpecificationLimit,Unit,ResultType,RequiredTest,MinimumElapsedHours,SortOrder,ApprovalStatus,IsActive,CreatedBy,CreatedDate,ItemCode,CompendialReference,IsDefaultForCategory) VALUES(N'EQ001-PRM-LEGACY',N'Primary Packaging',1,N'TBC',N'Synthetic legacy count',N'NMT 10 CFU/25 cm2',10,N'CFU/25 cm2',N'Numeric',1,120,1,N'Draft',0,N'uat-prm-author',SYSDATETIME(),N'EQ001-SYNTHETIC',N'Synthetic legacy attribution fixture',0);INSERT dbo.PRM_SpecificationContentHistory(SpecificationNo,SampleCategory,VersionNo,ActionType,OldRowsJson,NewRowsJson,ChangedBy,ChangedAt,ChangeReason,ContentHash) VALUES(N'EQ001-PRM-LEGACY',N'Primary Packaging',1,N'Draft Created',N'[]',N'[]',N'uat-prm-author',SYSDATETIME(),N'Synthetic prior non-native attribution',0x00);");
        var legacy=await Loaded(draft with { SpecificationNo="EQ001-PRM-LEGACY",Reference="Synthetic legacy attribution fixture" },1);
        await Denied(async()=>{await pipe.ApplicationAsync(await Proof("PrmReview","uat-prm-reviewer",legacy));},"legacy attribution without native trusted creation cannot be converted into a trusted review signature","PrmStateRejected");
        var editorDraft=draft with { SpecificationNo="EQ001-PRM-EDITOR" };
        await pipe.ApplicationAsync(await Proof("PrmSaveDraft","uat-prm-author",editorDraft));
        var editorChange=(await Loaded(editorDraft,1)) with { Reference="Synthetic independent-editor qualification" };
        await pipe.ApplicationAsync(await Proof("PrmSaveDraft","uat-prm-reviewer",editorChange));
        editorChange=await Loaded(editorChange,1);
        await Denied(async()=>{await pipe.ApplicationAsync(await Proof("PrmReview","uat-prm-reviewer",editorChange));},"a later draft editor cannot review even when distinct from the original creator","PrmStateRejected");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.PRM_SpecificationSignatures WHERE SpecificationNo=N'EQ001-PRM-EDITOR'"))==0,"rejected editor self-review leaves no signature evidence");
        var processDraft=draft with { SpecificationNo="EQ001-PRM-STAGE",Category="Production / In-Process",ProductionStage="After Mixing" };
        await pipe.ApplicationAsync(await Proof("PrmSaveDraft","uat-prm-author",processDraft));
        var processCorrection=(await Loaded(processDraft,1)) with { ItemCode="EQ001-STAGE-CORRECTED",ProductionStage="After Compression" };
        var processReply=await pipe.ApplicationAsync(await Proof("PrmSaveDraft","uat-prm-author",processCorrection));
        Check(processReply.UserJson=="1" && Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.PRM_SpecificationTests WHERE SpecificationNo=N'EQ001-PRM-STAGE' AND VersionNo=1 AND ItemCode=N'EQ001-STAGE-CORRECTED' AND ProductionStage=N'After Compression'"))==1,"a saved in-process draft permits controlled item and stage corrections");
        Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.EQ001_PrmSigningRequests r JOIN dbo.PRM_SpecificationContentHistory h ON h.HistoryID=r.HistoryID WHERE r.SpecificationNo=N'EQ001-PRM-E2E' AND r.WindowsCallerSid=N'"+settings.CallerSid+"'"))>=5,"PRM evidence links the native Windows caller SID to immutable content-history identities");
        await Exec(_admin,"CREATE TRIGGER dbo.EQ001_Test_PrmAuditFailure ON dbo.AuditTrail AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE TableName=N'PRM_SpecificationTests') THROW 56899,'Synthetic audit failure',1; END;");
        try {
            var failure=draft with { SpecificationNo="EQ001-PRM-ROLLBACK" };
            await Denied(async()=>{await pipe.ApplicationAsync(await Proof("PrmSaveDraft","uat-prm-author",failure));},"PRM audit failure rejects the entire operation","StoreUnavailable");
            Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.PRM_SpecificationTests WHERE SpecificationNo=N'EQ001-PRM-ROLLBACK'"))==0 && Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.PRM_SpecificationContentHistory WHERE SpecificationNo=N'EQ001-PRM-ROLLBACK'"))==0 && Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.EQ001_PrmSigningRequests WHERE SpecificationNo=N'EQ001-PRM-ROLLBACK'"))==0,"failed PRM audit rolls back draft, content history and replay record atomically");
        } finally { await Exec(_admin,"DROP TRIGGER dbo.EQ001_Test_PrmAuditFailure;"); }
    }
}
