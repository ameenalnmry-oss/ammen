using System.Data;
using PharmaLIMS.Services;

internal static partial class Program
{
    private static int passed, failed;
    private static void Run(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected [{expected}], actual [{actual}].");
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static readonly (string Name, string Result, string Spec, string Limit, string Code, string TestName, string Expected)[] NumericCases =
    {
        ("F05 rejects partial multiplication", "2", "NLT 1 * 10^3", "", "", "", "Check Required"),
        ("F05 rejects unknown lower clause", "90", "NMT 100; AT LEAST 95", "", "", "", "Check Required"),
        ("F05 NMT separator is not equality", "50", "NMT = 100", "", "", "", "Conforms"),
        ("F05 RANGE separator is not equality", "100", "RANGE = 95-105", "", "", "", "Conforms"),
        ("F05 upper separated colon", "50", "NMT: 100", "", "", "", "Conforms"),
        ("F05 rejects unsupported prose", "50", "NMT 100 recommended", "", "", "", "Check Required"),
        ("F05 rejects unknown OR", "90", "NMT 100 OR NLT 95", "", "", "", "Check Required"),
        ("F05 double symbolic equality rejected", "50", "== 100", "", "", "", "Check Required"),
        ("F05 symbolic assignment separator rejected", "50", "<= = 100", "", "", "", "Check Required"),
        ("F05 rejects trailing AND", "90", "NMT 100 AND", "", "", "", "Check Required"),
        ("F05 cannot borrow fallback for garbage", "50", "review required", "", "TAMC", "TAMC NMT 100", "Check Required"),
        ("F05 equivalent annotation", "999", "NMT 10^3 CFU/g (1000 CFU/g)", "1000", "TAMC", "", "Conforms"),
        ("F05 contradictory annotation", "99", "NMT 100 CFU/g (1000 CFU/g)", "", "", "", "Check Required"),
        ("F05 hidden rule annotation", "50", "NMT 100 (NLT 95)", "", "", "", "Check Required"),
        ("F05 range reversed", "100", "RANGE 105-95", "", "", "", "Check Required"),
        ("F05 context temperature not acceptance", "50", "NMT 100 CFU/g; incubation RANGE 30-35 C", "", "", "", "Conforms"),
        ("F05 unknown context not ignored", "50", "NMT 100; incubation NLT 30 C", "", "", "", "Check Required"),
        ("F05 unit alternatives", "50", "NMT 100 CFU/g or mL", "", "", "", "Conforms"),
        ("F06 strict conflicting structured upper", "500", "LESS THAN 1000", "100", "TAMC", "", "Check Required"),
        ("F06 strict equal number inclusive conflict", "50", "< 100", "100", "TAMC", "", "Check Required"),
        ("F06 multiple upper conflict", "40", "NMT 100 AND NMT 200", "50", "TAMC", "", "Check Required"),
        ("F06 canonical redundant upper", "90", "NMT 100 AND NMT 200", "100", "TAMC", "", "Conforms"),
        ("F06 intersection rejection", "101", "NMT 100 AND NMT 200", "100", "TAMC", "", "Does Not Conform"),
        ("F06 lower-only structured is incomplete", "90", "NLT 95", "100", "TAMC", "", "Check Required"),
        ("F06 impossible interval", "100", "NLT 105 AND NMT 95", "", "", "", "Check Required"),
        ("F06 empty strict interval", "100", "> 100 AND <= 100", "", "", "", "Check Required"),
        ("NMT inclusive boundary", "100", "NMT 100", "", "", "", "Conforms"),
        ("NMT excursion", "101", "NMT 100", "", "", "", "Does Not Conform"),
        ("strict boundary", "100", "LESS THAN 100", "", "", "", "Does Not Conform"),
        ("strict below", "99", "LESS THAN 100", "", "", "", "Conforms"),
        ("NLT boundary", "95", "NLT 95", "", "", "", "Conforms"),
        ("NLT below", "94", "NLT 95", "", "", "", "Does Not Conform"),
        ("symbol interval", "100", ">= 95 AND <= 105", "", "", "", "Conforms"),
        ("symbol interval outside", "106", ">= 95 AND <= 105", "", "", "", "Does Not Conform"),
        ("word interval", "100", "BETWEEN 95 AND 105", "", "", "", "Conforms"),
        ("bare interval", "94", "95-105", "", "", "", "Does Not Conform"),
        ("exponent lower", "0.5", "NMT 1e-3", "", "", "", "Does Not Conform"),
        ("scientific result entry rejected", "1e2", "NMT 100", "", "", "", "Check Required"),
        ("comma rejected", "100", "NMT 1,000", "", "", "", "Check Required"),
        ("negative input", "-1", "NMT 100", "", "", "", "Check Required"),
        ("exponent underflow rejected", "0", "NMT 1e-29", "", "", "", "Check Required"),
        ("power overflow rejected", "1", "NMT 10^29", "", "", "", "Check Required"),
        ("negative structured rejected", "1", "", "-1", "TAMC", "", "Check Required"),
        ("structured only count", "100", "", "100", "TAMC", "", "Conforms"),
        ("structured only unrelated test", "100", "", "100", "ASSAY", "", "Check Required"),
        ("identity only structured", "100", "TAMC", "100", "TAMC", "", "Conforms"),
        ("different test not borrowed", "100", "TYMC NMT 100", "1000", "TAMC", "", "Check Required"),
        ("specific TYMC intersection", "101", "NMT 10^3 CFU/g; TYMC NMT 10^2 CFU/g", "100", "TYMC", "", "Does Not Conform"),
        ("test labelled bounds", "100", "TAMC NMT 105; NLT 95", "105", "TAMC", "", "Conforms"),
        ("different explicitly labelled test ignored", "50", "TAMC NMT 100; TYMC NMT 10", "100", "TAMC", "", "Conforms"),
        ("numeric blank is not tested", "", "NMT 100", "", "", "", "Not Tested"),
        ("unicode less equal", "100", "≤ 100", "", "", "", "Conforms"),
        ("unicode interval", "100", "95–105", "", "", "", "Conforms"),
        ("context with time", "100", "NMT 100; incubation 30-35 C for 3 days", "", "", "", "Conforms"),
    };

    private static int Main()
    {
        foreach (var test in NumericCases)
            Run(test.Name, () => Equal(test.Expected,
                PrmResultInterpretationEvaluator.Evaluate("Numeric", test.Spec, test.Result, test.Limit, test.Code, test.TestName)));

        Run("F01 60 in frozen 1000 L is OOS", () => {
            var r = EmResultCalculator.Calculate(60, "Active Air Sampling", 1000, 25m, 50m);
            Equal<decimal?>(60m, r.Value); Equal("OOS", r.Status);
        });
        Run("F01 the same count at 2000 L differs", () => {
            var r = EmResultCalculator.Calculate(60, "Active Air Sampling", 2000, 25m, 50m);
            Equal<decimal?>(30m, r.Value); Equal("Alert", r.Status);
        });
        Run("F02 fractional excursion is never int-rounded", () => {
            var r = EmResultCalculator.Calculate(1, "Active Air Sampling", 800, 1m, 1m);
            Equal<decimal?>(1.25m, r.Value); Equal("OOS", r.Status);
        });
        Run("F02 rational strict boundary survives quotient rounding", () => {
            var r = EmResultCalculator.Calculate(1, "Active Air Sampling", 3, 333.333333333333m, 333.333333333333m);
            Equal<decimal?>(333.333333333333m, r.Value); Equal("OOS", r.Status);
        });
        Run("EM exact equality is pass", () => Equal("PASS",
            EmResultCalculator.Calculate(1, "Active Air Sampling", 1000, 1m, 1m).Status));
        Run("EM missing volume blocked", () => Equal("Not Assessed",
            EmResultCalculator.Calculate(1, "Active Air Sampling", null, 1m, 1m).Status));
        Run("EM zero volume blocked", () => Equal("Not Assessed",
            EmResultCalculator.Calculate(1, "Active Air Sampling", 0, 1m, 1m).Status));
        Run("EM unknown method blocked", () => Equal("Not Assessed",
            EmResultCalculator.Calculate(1, "Air", 1000, 1m, 1m).Status));
        Run("EM no count is pending", () => Equal("Pending",
            EmResultCalculator.Calculate(null, "Settle Plate", null, 1m, 2m).Status));
        Run("EM negative throws", () => Throws<ArgumentOutOfRangeException>(() =>
            EmResultCalculator.Calculate(-1, "Settle Plate", null, 1m, 2m)));
        Run("EM direct count", () => Equal<decimal?>(5m, EmResultCalculator.CalculateValue(5,"Contact Plate",null)));
        Run("EM reversed limits blocked", () => Equal("Not Assessed",
            EmResultCalculator.Calculate(5,"Settle Plate",null,10m,1m).Status));
        Run("EM stored decimal integer is accepted exactly", () => {
            Equal(true, EmResultCalculator.TryReadStoredCount(1.000m,out int? count)); Equal<int?>(1,count);
        });
        Run("EM stored fractional count is not rounded", () =>
            Equal(false,EmResultCalculator.TryReadStoredCount(1.5m,out _)));
        Run("EM stored large count is not overflowed", () =>
            Equal(false,EmResultCalculator.TryReadStoredCount(2147483648m,out _)));
        Run("EM stored malformed count fails closed", () =>
            Throws<InvalidOperationException>(()=>EmResultCalculator.ReadStoredCount("invalid")));
        Run("EM aggregate OOS", () => Equal("OOS", EmResultCalculator.Aggregate(new[]{"PASS","OOS","Pending"})));
        Run("EM aggregate pending member", () => Equal("Partially Entered", EmResultCalculator.Aggregate(new[]{"PASS","Pending"})));
        Run("EM aggregate all pass", () => Equal("Results Entered", EmResultCalculator.Aggregate(new[]{"PASS","PASS"})));
        Run("EM aggregate invalid blocked", () => Throws<InvalidOperationException>(() =>
            EmResultCalculator.Aggregate(new[]{"OOS","Not Assessed"})));

        Run("F03 identical full snapshots", () => { var a=Snapshot(); Match(a,a.Copy()); });
        Run("F03 null snapshot blocked", () => Throws<DBConcurrencyException>(() => Match(null,Snapshot())));
        Run("F03 changed result blocked", () => Conflict(t => t.Rows[0]["Result"]=10m));
        Run("F03 changed version blocked", () => Conflict(t => t.Rows[0]["Version"]=new byte[]{2}));
        Run("F03 removed row blocked", () => Conflict(t => t.Rows.RemoveAt(0)));
        Run("F03 added row blocked", () => Conflict(t => t.Rows.Add(3,7,3m,new byte[]{1},"other")));
        Run("F03 duplicate identity blocked", () => Conflict(t => t.Rows[1]["Id"]=1));
        Run("F03 cross-owner blocked", () => Conflict(t => t.Rows[0]["Owner"]=8));
        Run("F03 schema missing blocked", () => Conflict(t => t.Columns.Remove("Result")));
        Run("F03 missing becomes zero is a change", () => {
            var a=Snapshot(); a.Rows[0]["Result"]=DBNull.Value;
            var b=a.Copy(); b.Rows[0]["Result"]=0m;
            Throws<DBConcurrencyException>(()=>Match(a,b));
        });
        Run("F03 row order is irrelevant", () => {
            var a=Snapshot(); var b=a.Clone(); b.ImportRow(a.Rows[1]);b.ImportRow(a.Rows[0]); Match(a,b);
        });
        Run("F04 notes-only still rejects concurrent value change", () => {
            var loaded=Snapshot(); var current=loaded.Copy();current.Rows[0]["Result"]=10m;
            Throws<DBConcurrencyException>(() => Match(loaded,current));
        });
        Run("F04 null and DBNull compare equal", () => Equal(true, ResultSnapshotGuard.Equivalent(null,DBNull.Value)));
        Run("F04 bytes compare by value", () => Equal(true, ResultSnapshotGuard.Equivalent(new byte[]{1,2},new byte[]{1,2})));
        Run("F04 grid missing row blocked", () => Throws<DBConcurrencyException>(() =>
            ResultSnapshotGuard.EnsureVisibleKeys(new[]{1},Snapshot(),"Id")));
        Run("F04 grid duplicates blocked", () => Throws<DBConcurrencyException>(() =>
            ResultSnapshotGuard.EnsureVisibleKeys(new[]{1,1},Snapshot(),"Id")));
        Run("F04 reordered complete grid valid", () => ResultSnapshotGuard.EnsureVisibleKeys(new[]{2,1},Snapshot(),"Id"));

        Run("F02 trend fractional excursion", () => {
            var t=Trend(1.25m,"OOS",1,800); EmTrendAssessmentService.Apply(t);
            Equal("Action",t.Rows[0]["Assessment"]); Equal(1.25m,t.Rows[0]["Result"]);
        });
        Run("F02 legacy rounded false pass is excluded", () => {
            var t=Trend(1m,"PASS",1,800); EmTrendAssessmentService.Apply(t);
            Equal("Not Assessed",t.Rows[0]["Assessment"]);Equal(DBNull.Value,t.Rows[0]["Result"]);
            Equal(1m,t.Rows[0]["StoredResult"]);Equal(1.25m,t.Rows[0]["RecalculatedResult"]);
        });
        Run("F09 reconciled limits assessed through shared effective input", () => {
            var t=Trend(1.25m,"OOS",1,800);t.Rows[0]["EvidenceSource"]="Signed Historical Reconciliation #1";
            EmTrendAssessmentService.Apply(t);Equal("Action",t.Rows[0]["Assessment"]);
        });
        Run("F09 missing evidence excluded", () => {
            var t=Trend(1m,"PASS",1,1000);t.Rows[0]["EvidenceComplete"]=0;
            EmTrendAssessmentService.Apply(t);Equal(DBNull.Value,t.Rows[0]["Result"]);
        });
        Run("F09 different stored decision excluded", () => {
            var t=Trend(1.25m,"PASS",1,800);EmTrendAssessmentService.Apply(t);
            Equal("Not Assessed",t.Rows[0]["Assessment"]);
        });
        Run("F09 compatible legacy retained without backfill", () => {
            var t=Trend(1m,"PASS",1,1000);t.Rows[0]["ResultCalculationVersion"]=DBNull.Value;
            EmTrendAssessmentService.Apply(t);Equal("Pass",t.Rows[0]["Assessment"]);
            Equal(DBNull.Value,t.Rows[0]["ResultCalculationVersion"]);
        });
        Run("F09 unknown calculation version excluded", () => {
            var t=Trend(1m,"PASS",1,1000);t.Rows[0]["ResultCalculationVersion"]=(short)2;
            EmTrendAssessmentService.Apply(t);Equal(DBNull.Value,t.Rows[0]["Result"]);
        });
        Run("F09 fractional stored count is excluded without rounding", () => {
            var t=Trend(2m,"OOS",1,1000);t.Rows[0]["TotalCount"]=1.5m;
            EmTrendAssessmentService.Apply(t);Equal(DBNull.Value,t.Rows[0]["Result"]);
            Equal("Not Assessed",t.Rows[0]["Assessment"]);
        });
        Run("F09 unentered result remains pending", () => {
            var t=Trend(1m,"PASS",1,1000);t.Rows[0]["StoredResult"]=DBNull.Value;t.Rows[0]["TotalCount"]=DBNull.Value;
            EmTrendAssessmentService.Apply(t);Equal("Pending",t.Rows[0]["Assessment"]);
        });
        RunMergedRegressionCases();
        Console.WriteLine($"Review regression: {passed} passed, {failed} failed.");
        return failed==0 ? 0 : 1;
    }

    private static DataTable Snapshot()
    {
        var t=new DataTable();
        t.Columns.Add("Id",typeof(int));t.Columns.Add("Owner",typeof(int));
        t.Columns.Add("Result",typeof(decimal));t.Columns.Add("Version",typeof(byte[]));
        t.Columns.Add("Remarks",typeof(string));
        t.Rows.Add(1,7,5m,new byte[]{1},"first");t.Rows.Add(2,7,6m,new byte[]{1},"second");
        return t;
    }
    private static void Match(DataTable? loaded,DataTable current) =>
        ResultSnapshotGuard.EnsureMatches(loaded,current,"Id","Owner",7);
    private static void Conflict(Action<DataTable> change)
    {
        var a=Snapshot();var b=a.Copy();change(b);
        Throws<DBConcurrencyException>(()=>Match(a,b));
    }
    private static DataTable Trend(decimal value,string status,int count,int volume)
    {
        var t=new DataTable();
        t.Columns.Add("StoredResult",typeof(decimal));t.Columns.Add("StoredStatus",typeof(string));
        t.Columns.Add("TotalCount",typeof(decimal));t.Columns.Add("Method",typeof(string));
        t.Columns.Add("AirVolumeLiters",typeof(int));t.Columns.Add("AlertLimit",typeof(decimal));
        t.Columns.Add("ActionLimit",typeof(decimal));t.Columns.Add("EvidenceComplete",typeof(int));
        t.Columns.Add("ResultCalculationVersion",typeof(short));t.Columns.Add("EvidenceSource",typeof(string));
        t.Rows.Add(value,status,count,"Active Air Sampling",volume,1m,1m,1,(short)1,"Native Frozen Snapshot");
        return t;
    }
}
