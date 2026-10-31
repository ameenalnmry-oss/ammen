using System.Data;
using System.Text.RegularExpressions;

// Executes the exact validation-reload method. Providers and controls are adapters,
// not SQL queries, WPF rendering or authorization evidence.
internal static partial class SiteGenericReloadProbe
{
    private abstract class Adapter
    {
        protected string? pendingSavedInvestigationVersion="saved",loadedInvestigationVersion="old";
        protected int qualityEventId=1;
        protected DataTable? checklistTable,rootCauseWhysTable,impactAssessmentTable,capaItemsTable,retestingTable,distributionTable;
        protected Control dgAffectedResults,dgActions,dgChecklist,dgRootCauseWhys,dgImpactAssessment,dgCAPAItems,dgRetesting,dgDistribution;
        internal int Fault,BindingFault,Captures,Reads,Bindings;
        internal bool BeforeChanged,AfterChanged;
        internal string? Published=>loadedInvestigationVersion;
        internal bool MissingPending{set{if(value)pendingSavedInvestigationVersion=null;}}
        protected Adapter(){dgAffectedResults=new(this);dgActions=new(this);dgChecklist=new(this);dgRootCauseWhys=new(this);dgImpactAssessment=new(this);dgCAPAItems=new(this);dgRetesting=new(this);dgDistribution=new(this);}
        internal abstract void Reload();
        protected sealed class Control(Adapter owner)
        {public object ItemsSource{set{if(++owner.Bindings==owner.BindingFault)throw new InvalidOperationException("Injected binding failure.");}}}
        protected static class AppConfig{public const string ConnectionString="adapter";}
        protected static class QualityEventInvestigationConcurrencyGuard
        {
            internal static Adapter Current=null!;
            public static string CaptureLoadedVersion(string cs,int id,bool prm){Current.Captures++;return (Current.Captures==1&&Current.BeforeChanged)||(Current.Captures==2&&Current.AfterChanged)?"changed":"saved";}
        }
        protected static class DatabaseHelper
        {
            public static DataTable? GetQualityEventAffectedResults(int id)=>QualityEventInvestigationConcurrencyGuard.Current.Read();
            public static DataTable? GetQualityEventActions(int id)=>QualityEventInvestigationConcurrencyGuard.Current.Read();
        }
        internal void Select()=>QualityEventInvestigationConcurrencyGuard.Current=this;
        private DataTable? Read(){Reads++;if(Fault==Reads)throw new InvalidOperationException("Injected read failure.");return Fault==100+Reads?null:new DataTable();}
        protected DataTable? GetFilteredChecklist()=>Read();protected DataTable? LoadRootCauseWhysTable()=>Read();
        protected DataTable? LoadImpactAssessmentTable()=>Read();protected DataTable? LoadCAPAItemsTable()=>Read();
        protected DataTable? LoadRetestingTable()=>Read();protected DataTable? LoadDistributionTable()=>Read();
        protected void EnsureEnvironmentalMonitoringStructuredDefaults(){}
    }

    internal static void Run(string? root=null)
    {
        if(root!=null)
        {
            string source=File.ReadAllText(Path.Combine(root,"scripts/site-overlays/AMEEN-QualityEventInvestigation.xaml.cs.txt")).Replace("\r\n","\n");
            string compiled=File.ReadAllText(Path.Combine(root,"tests/PharmaLIMS.InvestigationConcurrencyRegression/SiteGenericReloadMethods.cs")).Replace("\r\n","\n");
            string method=SiteGenericInvestigationCases.Method(source,"private void ReloadInvestigationTablesForValidation(");
            if(method!=SiteGenericInvestigationCases.Method(compiled,"private void ReloadInvestigationTablesForValidation("))throw new Exception("Generic reload compiled method differs from full artifact.");
        }
        int rejected=0;
        bool Reject(Actual view){view.Select();try{view.Reload();}catch(InvalidOperationException){return view.Published==null;}catch(DBConcurrencyException){return view.Published==null;}return false;}
        foreach(int fault in Enumerable.Range(1,8).Concat(Enumerable.Range(101,8)))if(Reject(new Actual{Fault=fault}))rejected++;
        foreach(var view in new[]{new Actual{BeforeChanged=true},new Actual{AfterChanged=true},new Actual{MissingPending=true},new Actual{BindingFault=1},new Actual{BindingFault=4},new Actual{BindingFault=8}})if(Reject(view))rejected++;
        var good=new Actual();good.Select();good.Reload();
        if(rejected!=22||good.Published!="saved"||good.Reads!=8||good.Bindings!=8)throw new Exception($"Generic reload publication guard failed: {rejected}/22.");
        Console.WriteLine("PASS SITE GENERIC QE RELOAD:22 failed/null reads, changed/missing saved token and binding failures rejected without publication; unchanged own-save reload accepted. Exact method with control/read adapters, not WPF/SQL.");
    }
}
