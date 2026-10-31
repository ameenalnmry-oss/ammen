using System.Data;
using System.Text.Json;

// Fault injection into the exact child-loading/version-publication source segment.
// Read providers and stable version capture are adapters, not WPF/SQL acceptance.
internal static class SitePrmLoadFailureCases
{
    internal static void Run(string project)
    {
        string source=File.ReadAllText(Path.Combine(project,"scripts/site-overlays/AMEEN-PRMQualityEventInvestigation.xaml.cs.txt"));
        int start=source.IndexOf("                    if (data != null)\n",StringComparison.Ordinal);
        int end=source.IndexOf("                });",start,StringComparison.Ordinal);
        if(start<0 || end<0 || source[start..end]!=Corrected.Expected)
            throw new Exception("Compiled corrected load probe differs from the supplied source segment.");
        using var frozen=JsonDocument.Parse(File.ReadAllText(Path.Combine(project,"tests/fixtures/AMEEN-PRM-investigation-load-20261030.json")));
        if(frozen.RootElement.GetProperty("LoadTail").GetString()!=Frozen.Expected ||
           frozen.RootElement.GetProperty("Helpers").GetString()!=Frozen.Helpers)
            throw new Exception("Frozen load probe drifted.");
        int oldAccepted=0,rejected=0;
        foreach(int fault in new[]{1,2,3,4})
        {
            var old=new Frozen{Fault=fault};
            if(old.Load().EvidenceVersion=="stable") oldAccepted++;
            var corrected=new Corrected{Fault=fault};
            bool threw=false;
            try{corrected.Load();}catch(InvalidOperationException){threw=true;}
            if(threw && corrected.Data.EvidenceVersion.Length==0) rejected++;
        }
        if(oldAccepted!=4 || rejected!=4 || new Corrected().Load().EvidenceVersion!="stable")
            throw new Exception($"Child load fail-closed regression: old acceptance {oldAccepted}/4, corrected rejection {rejected}/4.");
        Console.WriteLine("PASS SITE PRM LOAD: frozen accepted four failed/null child reads; corrected rejected 4/4 without publishing a version; successful reads accepted. Exact source segment, not full WPF.");
    }
    private class DataSet { public DataTable AffectedResults=new(); public DataTable Actions=new(); public string EvidenceVersion=""; }
    private class Adapter
    {
        public int Fault;
        public DataSet Data=new();
        protected int qualityEventId=1;
        protected Adapter PRMQualityEventInvestigationService => this;
        public DataTable GetPRMAffectedResults(int id) => Read(1,3);
        public DataTable GetPRMActions(int id) => Read(2,4);
        private DataTable Read(int error,int missing)
        {
            if(Fault==error) throw new InvalidOperationException("Injected child query failure.");
            return Fault==missing ? null! : new DataTable();
        }
    }
    private static class AppConfig { public const string ConnectionString="adapter"; }
    private static class QualityEventInvestigationConcurrencyGuard
    { public static string CaptureLoadedVersion(string cs,int id,bool prm)=>"stable"; }
    private static class ApplicationLogger { public static void Error(string message,Exception ex){} }
    private sealed class Frozen : Adapter
    {
        internal const string Expected="                    if (data != null)\n                    {\n                        data.AffectedResults = SafeLoadAffectedResults();\n                        data.Actions = SafeLoadActions();\n                        string versionAfterLoad = QualityEventInvestigationConcurrencyGuard.CaptureLoadedVersion(AppConfig.ConnectionString, qualityEventId, true);\n                        if (!string.Equals(versionBeforeLoad, versionAfterLoad, StringComparison.Ordinal))\n                            throw new DBConcurrencyException(\"The PRM investigation changed while loading. Reload before continuing.\");\n                        data.EvidenceVersion = versionAfterLoad;\n                    }\n                    return data;\n";
        internal const string Helpers="private DataTable SafeLoadAffectedResults()\n        {\n            try\n            {\n                return PRMQualityEventInvestigationService.GetPRMAffectedResults(qualityEventId) ?? new DataTable();\n            }\n            catch (Exception ex)\n            {\n                ApplicationLogger.Error($\"Unable to load affected PRM results for QualityEventId={qualityEventId}.\", ex);\n                return new DataTable();\n            }\n        }\nprivate DataTable SafeLoadActions()\n        {\n            try\n            {\n                return PRMQualityEventInvestigationService.GetPRMActions(qualityEventId) ?? new DataTable();\n            }\n            catch (Exception ex)\n            {\n                ApplicationLogger.Error($\"Unable to load PRM actions for QualityEventId={qualityEventId}.\", ex);\n                return new DataTable();\n            }\n        }";
private DataTable SafeLoadAffectedResults()
        {
            try
            {
                return PRMQualityEventInvestigationService.GetPRMAffectedResults(qualityEventId) ?? new DataTable();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error($"Unable to load affected PRM results for QualityEventId={qualityEventId}.", ex);
                return new DataTable();
            }
        }
private DataTable SafeLoadActions()
        {
            try
            {
                return PRMQualityEventInvestigationService.GetPRMActions(qualityEventId) ?? new DataTable();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error($"Unable to load PRM actions for QualityEventId={qualityEventId}.", ex);
                return new DataTable();
            }
        }
        public DataSet Load()
        {
            string versionBeforeLoad="stable";
            var data=Data;
                    if (data != null)
                    {
                        data.AffectedResults = SafeLoadAffectedResults();
                        data.Actions = SafeLoadActions();
                        string versionAfterLoad = QualityEventInvestigationConcurrencyGuard.CaptureLoadedVersion(AppConfig.ConnectionString, qualityEventId, true);
                        if (!string.Equals(versionBeforeLoad, versionAfterLoad, StringComparison.Ordinal))
                            throw new DBConcurrencyException("The PRM investigation changed while loading. Reload before continuing.");
                        data.EvidenceVersion = versionAfterLoad;
                    }
                    return data;
        }
    }
    private sealed class Corrected : Adapter
    {
        internal const string Expected="                    if (data != null)\n                    {\n                        data.AffectedResults = PRMQualityEventInvestigationService.GetPRMAffectedResults(qualityEventId)\n                            ?? throw new InvalidOperationException(\"Affected PRM results were not loaded. Reload before continuing.\");\n                        data.Actions = PRMQualityEventInvestigationService.GetPRMActions(qualityEventId)\n                            ?? throw new InvalidOperationException(\"PRM actions were not loaded. Reload before continuing.\");\n                        string versionAfterLoad = QualityEventInvestigationConcurrencyGuard.CaptureLoadedVersion(AppConfig.ConnectionString, qualityEventId, true);\n                        if (!string.Equals(versionBeforeLoad, versionAfterLoad, StringComparison.Ordinal))\n                            throw new DBConcurrencyException(\"The PRM investigation changed while loading. Reload before continuing.\");\n                        data.EvidenceVersion = versionAfterLoad;\n                    }\n                    return data;\n";
        public DataSet Load()
        {
            string versionBeforeLoad="stable";
            var data=Data;
                    if (data != null)
                    {
                        data.AffectedResults = PRMQualityEventInvestigationService.GetPRMAffectedResults(qualityEventId)
                            ?? throw new InvalidOperationException("Affected PRM results were not loaded. Reload before continuing.");
                        data.Actions = PRMQualityEventInvestigationService.GetPRMActions(qualityEventId)
                            ?? throw new InvalidOperationException("PRM actions were not loaded. Reload before continuing.");
                        string versionAfterLoad = QualityEventInvestigationConcurrencyGuard.CaptureLoadedVersion(AppConfig.ConnectionString, qualityEventId, true);
                        if (!string.Equals(versionBeforeLoad, versionAfterLoad, StringComparison.Ordinal))
                            throw new DBConcurrencyException("The PRM investigation changed while loading. Reload before continuing.");
                        data.EvidenceVersion = versionAfterLoad;
                    }
                    return data;
        }
    }
}
