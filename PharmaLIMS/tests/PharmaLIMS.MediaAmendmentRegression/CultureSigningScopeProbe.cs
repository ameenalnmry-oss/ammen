using System.Data;
using System.Reflection;
using PharmaLIMS.Services;

internal static class CultureSigningScopeProbe
{
    internal static int Run()
    {
        var loaded=CultureVisibleEvidenceProbe.Snapshot(CultureVisibleEvidenceProbe.Data());
        int rejected=0,expected=0;
        foreach(var change in new Action<State>[] {
            s=>s.CurrentId=2,s=>s.CurrentNo="MP-OTHER",s=>s.Current=null,s=>s.Failed=true,
            s=>{var ds=CultureVisibleEvidenceProbe.Data();ds.Tables[0].Rows[0]["FinalPH"]=7.5m;s.Current=CultureVisibleEvidenceProbe.Snapshot(ds);},
            s=>s.Original=null,s=>s.NewMode=true})
        {
            var state=new State{Original=loaded,Current=loaded};change(state);expected++;
            if(CultureVisibleEvidenceProbe.Reject(()=>Scope(state)))rejected++;
        }
        foreach(var state in new[]{new State{TargetId=0,CurrentId=0,TargetNo="Generated",CurrentNo="Generated",NewMode=false},new State{TargetId=0,CurrentId=0,TargetNo="Generated",CurrentNo="Generated",NewMode=true,Current=loaded}})
        {expected++;if(CultureVisibleEvidenceProbe.Reject(()=>Scope(state)))rejected++;}
        Scope(new State{Original=loaded,Current=CultureVisibleEvidenceProbe.Snapshot(CultureVisibleEvidenceProbe.Data())});
        Scope(new State{TargetId=0,CurrentId=0,TargetNo="Generated",CurrentNo="Generated",NewMode=true});
        var before=CultureVisibleEvidenceProbe.Parameters();var after=CultureVisibleEvidenceProbe.Parameters();after.Single(p=>p.ParameterName=="@FinalPH").Value=7.5m;
        expected++;if(CultureVisibleEvidenceProbe.Reject(()=>Invoke("EnsureInputsUnchanged",before,after,CultureVisibleEvidenceProbe.Sop(true),CultureVisibleEvidenceProbe.Sop(true))))rejected++;
        var sop=CultureVisibleEvidenceProbe.Sop(true);sop[("1035-L-0005/A7","Conclusion")]="Unsatisfactory";
        expected++;if(CultureVisibleEvidenceProbe.Reject(()=>Invoke("EnsureInputsUnchanged",before,CultureVisibleEvidenceProbe.Parameters(),CultureVisibleEvidenceProbe.Sop(true),sop)))rejected++;
        foreach(var state in new[]{(Failed:true,New:false,Id:1),(Failed:false,New:false,Id:0),(Failed:false,New:true,Id:-1)})
        {expected++;if(CultureVisibleEvidenceProbe.Reject(()=>Invoke("EnsureSaveSelection",state.Failed,state.New,state.Id)))rejected++;}
        Invoke("EnsureSaveSelection",false,true,0);Invoke("EnsureSaveSelection",false,false,1);
        Console.WriteLine($"CULTURE SIGNING SCOPE: {rejected}/{expected} changed selection/input/invalid mode states rejected; stable loaded and explicit-new controls accepted. Helper data tests, not modal WPF execution.");
        if(rejected!=expected)throw new Exception("RED: signing scope/input changes were not rejected.");return 0;
    }
    private sealed class State
    {internal int TargetId=1,CurrentId=1;internal string TargetNo="MP-1",CurrentNo="MP-1";internal MediaPreparationAmendmentSnapshot? Original,Current;internal bool Failed,NewMode;}
    private static void Scope(State s)=>Invoke("EnsureSigningScope",s.TargetId,s.TargetNo,s.Original,s.CurrentId,s.CurrentNo,s.Current,s.Failed,s.NewMode);
    private static void Invoke(string name,params object?[] args)
    {
        var method=typeof(MediaPreparationAmendmentSnapshot).GetMethod(name,BindingFlags.Public|BindingFlags.Static);
        if(method==null)return; // Existing code has no such selection/input check.
        try{method.Invoke(null,args);}catch(TargetInvocationException ex){throw ex.InnerException!;}
    }
}
