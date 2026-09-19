using System;
using System.Data;
using PharmaLIMS.Services;

// Ported v261 expectations exercise the merged production services, not legacy copies.
internal static partial class Program
{
    private sealed record NumericCase(string Name, decimal Value, string Specification,
        string StructuredLimit, string TestCode, string TestName, string Expected);
    private sealed record InterpretationCase(string Name, string ResultType,
        string Specification, string Result, string Expected);
    private static void RunMergedRegressionCases()
    {
        NumericCase[] numericCases =
        [
            new("BARE_RANGE_INSIDE", 100m, "95-105", "", "", "", "Conforms"),
            new("BARE_RANGE_OUTSIDE", 90m, "95-105", "", "", "", "Does Not Conform"),
            new("STRICT_BOUNDARY", 100m, "LESS THAN 100", "", "", "", "Does Not Conform"),
            new("INCLUSIVE_BOUNDARY", 100m, "NMT 100", "", "", "", "Conforms"),
            new("COMPOUND_AND", 110m, "NLT 95 AND NMT 105", "", "", "", "Does Not Conform"),
            new("INCUBATION_IS_CONTEXT", 5m, "NMT 10; INCUBATION RANGE 30-35", "", "TAMC", "", "Conforms"),
            new("SCIENTIFIC_LIMIT", 0.0005m, "NMT 1e-3", "", "", "", "Conforms"),
            new("POWER_OF_TEN_LIMIT", 500m, "NMT 10^3", "", "", "", "Conforms"),
            new("UNSUPPORTED_OR", 5m, "NMT 10 OR NLT 20", "", "", "", "Check Required"),
            new("OTHER_TEST_IDENTITY", 5m, "TAMC NMT 10", "", "TYMC", "", "Check Required"),
            new("UNKNOWN_SPECIFICATION", 5m, "Refer to approved method", "", "", "", "Check Required"),
            new("STRUCTURED_COUNT_ONLY", 50m, "", "100", "TAMC", "", "Conforms"),
            new("SINGLE_INCLUSIVE_CONFLICT", 75m, "NMT 100", "50", "TAMC", "", "Check Required"),
            new("NEGATIVE_RESULT", -1m, "NMT 100", "", "", "", "Check Required"),

            // F05: consume the whole expression, and do not parse separators twice.
            new("F05_UNCONSUMED_MULTIPLICATION", 2m, "NLT 1 * 10^3", "", "", "", "Check Required"),
            new("F05_UNRECOGNIZED_ACCEPTANCE_CLAUSE", 90m, "NMT 100; AT LEAST 95", "", "", "", "Check Required"),
            new("F05_NMT_EQUALS_SEPARATOR", 50m, "NMT = 100", "", "", "", "Conforms"),
            new("F05_RANGE_EQUALS_SEPARATOR", 100m, "RANGE = 95-105", "", "", "", "Conforms"),

            // F06: controlled text and structured limits must not disagree silently.
            new("STRICT_STRUCTURED_CONFLICT", 500m, "LESS THAN 1000", "100", "TAMC", "", "Check Required"),
            new("MULTIPLE_UPPER_STRUCTURED_CONFLICT", 75m, "NMT 100 AND NMT 200", "50", "TAMC", "", "Check Required"),
            new("ANNOTATED_POWER_EQUIVALENT", 999m, "NMT 10^3 CFU/g (1000 CFU/g)", "1000", "TAMC", "", "Conforms"),
            new("ANNOTATED_POWER_CONFLICT", 999m, "NMT 10^3 CFU/g (100 CFU/g)", "", "TAMC", "", "Check Required"),
            new("MATCHING_MULTIPLE_UPPER", 100m, "NMT 100 AND NMT 100", "100", "TAMC", "", "Conforms"),
            new("REVERSED_RANGE", 100m, "RANGE 105-95", "", "", "", "Check Required"),
            new("NONZERO_LIMIT_UNDERFLOW", 0m, "NLT 1e-29", "", "", "", "Check Required"),
            new("SYMBOLIC_AND", 106m, ">=95 AND <=105", "", "", "", "Does Not Conform"),
            new("WRONG_TEXT_NOT_REPLACED_BY_NAME", 5m, "AT LEAST 10", "", "TAMC", "TAMC NMT 100", "Check Required")];
        InterpretationCase[] interpretationCases =
        [
            new("EMPTY_RESULT", "Numeric", "NMT 100", "", "Not Tested"),
            new("ABSENCE_NEGATIVE", "Qualitative", "Absent", "Absent", "Conforms"),
            new("ABSENCE_POSITIVE", "Qualitative", "Absent", "Present", "Does Not Conform"),
            new("AMBIGUOUS_THOUSANDS_SEPARATOR", "Numeric", "NMT 2000", "1,000", "Check Required")];
        foreach (var t in numericCases)
            Run("v261 port: " + t.Name, () => Equal(t.Expected, PrmNumericSpecificationEvaluator.Evaluate(
                t.Value, t.Specification, t.StructuredLimit, t.TestCode, t.TestName)));
        foreach (var t in interpretationCases)
            Run("v261 port: " + t.Name, () => Equal(t.Expected, PrmResultInterpretationEvaluator.Evaluate(
                t.ResultType, t.Specification, t.Result)));
        void CheckPorted(string name, Func<bool> condition) => Run("v261 port: " + name, () => Equal(true, condition()));
        CheckPorted("WATER_DISPLAY_PRESERVES_BOUNDARY", () => WaterResultValueContract.FormatNumeric(2.0001m) == "2.0001");
        CheckPorted("WATER_DISPLAY_REMOVES_ONLY_ZEROS", () => WaterResultValueContract.FormatNumeric(2.0000m) == "2");
        CheckPorted("WATER_DISPLAY_PRESERVES_LEGACY_PRECISION", () => WaterResultValueContract.FormatNumeric(2.00001m) == "2.00001");
        CheckPorted("WATER_DISPLAY_MINIMUM_DECIMAL", () => WaterResultValueContract.FormatNumeric(0.0000000000000000000000000001m) == "0.0000000000000000000000000001");
        CheckPorted("WATER_WRITE_FOUR_DECIMALS", () => WaterResultValueContract.IsExactlyRepresentable(2.0001m));
        CheckPorted("WATER_WRITE_EXCESS_PRECISION_REJECTED", () => !WaterResultValueContract.IsExactlyRepresentable(2.00001m));
        CheckPorted("WATER_WRITE_MAXIMUM_ACCEPTED", () => WaterResultValueContract.IsExactlyRepresentable(99999999999999.9999m));
        CheckPorted("WATER_WRITE_OVERFLOW_REJECTED", () => !WaterResultValueContract.IsExactlyRepresentable(100000000000000m));
        CheckPorted("WATER_WRITE_ZERO_ACCEPTED", () => WaterResultValueContract.IsExactlyRepresentable(0m));

        Run("v262 water numeric storage exact", () => Equal(true, WaterResultValueContract.StoredValueMatches(2.0001m, 2.0001m)));
        Run("v262 water text storage exact", () => Equal(true, WaterResultValueContract.StoredValueMatches(2.0001m, "2.0001")));
        Run("v262 water rounded storage rejected", () => Equal(false, WaterResultValueContract.StoredValueMatches(2.0001m, "2.000")));
        Run("v262 water missing storage rejected", () => Equal(false, WaterResultValueContract.StoredValueMatches(0m, DBNull.Value)));
        Run("v262 water malformed storage rejected", () => Equal(false, WaterResultValueContract.StoredValueMatches(1m, "not a number")));
        Run("v262 water culture comma rejected", () => Equal(false, WaterResultValueContract.StoredValueMatches(2.0001m, "2,0001")));
        Run("v262 water notes legacy value preserves precision", () => Equal(true, WaterResultValueContract.StoredValueMatches(2.00001m, "2.00001")));
        Run("v262 minimum negative write accepted", () => Equal(true, WaterResultValueContract.IsExactlyRepresentable(-99999999999999.9999m)));
        Run("v262 negative overflow rejected", () => Equal(false, WaterResultValueContract.IsExactlyRepresentable(-100000000000000m)));
        Run("v262 EM stored value and decision verified", () => Equal(true,
            EmResultCalculator.StoredEvidenceMatches(EmResultCalculator.Calculate(1,"Active Air Sampling",800,1m,1m),1.25m,"OOS",(short)1)));
        Run("v262 EM truncated stored value rejected", () => Equal(false,
            EmResultCalculator.StoredEvidenceMatches(EmResultCalculator.Calculate(1,"Active Air Sampling",800,1m,1m),1m,"OOS",(short)1)));
        Run("v262 EM stored decision mismatch rejected", () => Equal(false,
            EmResultCalculator.StoredEvidenceMatches(EmResultCalculator.Calculate(1,"Active Air Sampling",800,1m,1m),1.25m,"PASS",(short)1)));
        Run("v262 EM unknown stored version rejected", () => Equal(false,
            EmResultCalculator.StoredEvidenceMatches(EmResultCalculator.Calculate(1,"Active Air Sampling",800,1m,1m),1.25m,"OOS",(short)2)));
        Run("v262 EM pending null value verifies", () => Equal(true,
            EmResultCalculator.StoredEvidenceMatches(EmResultCalculator.Calculate(null,"Settle Plate",null,1m,2m),DBNull.Value,"Pending",(short)1)));
        Run("v262 EM pending must not retain value", () => Equal(false,
            EmResultCalculator.StoredEvidenceMatches(EmResultCalculator.Calculate(null,"Settle Plate",null,1m,2m),0m,"Pending",(short)1)));
        Run("v261 port: EM action equality is alert", () => Equal("Alert",EmResultCalculator.Calculate(20,"Active Air Sampling",1000,10m,20m).Status));
        Run("v261 port: scaled boundary remains OOS", () => Equal("OOS",EmResultCalculator.Calculate(1,"Active Air Sampling",999,0m,1.001m).Status));
        Run("v262 new precision replaces old three-place EM storage", () => Equal<decimal?>(1.001001001001m,EmResultCalculator.CalculateValue(1,"Active Air Sampling",999)));
    }
}
