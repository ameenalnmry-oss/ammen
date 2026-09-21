using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace PharmaLIMS.Services
{
    internal static class PrmNumericSpecificationEvaluator
    {
        // Result entry remains deliberately stricter than specification parsing:
        // users enter plain invariant decimals only. Specification limits may also
        // contain controlled scientific/power-of-ten notation such as 1e-3 or 10^3.
        private const string LimitTokenPattern = @"[+]?(?:(?:10\s*\^\s*[+-]?\d+)|(?:(?:\d+(?:\.\d+)?|\.\d+)(?:[eE][+-]?\d+)?))";
        private static readonly RegexOptions RuleOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        private static readonly Regex OperatorRegex = new Regex(
            @"(?<ge>\bNLT\b|\bNOT\s+LESS\s+THAN\b|>=|\u2265|\bMIN(?:IMUM)?\b)" +
            @"|(?<le>\bNMT\b|\bNOT\s+MORE\s+THAN\b|<=|\u2264|\bMAX(?:IMUM)?\b)" +
            @"|(?<lt>\bLESS\s+THAN\b|(?<!<)<(?![=]))" +
            @"|(?<gt>\bGREATER\s+THAN\b|\bMORE\s+THAN\b|(?<!>)>(?![=]))" +
            @"|(?<eq>\bEQUAL(?:S)?\b|\bEXACT\b|\bTARGET\b|(?<![<>])=(?!=))",
            RuleOptions);

        internal static bool TryParseControlledDecimal(string? text, out decimal value)
        {
            value = 0m;
            string normalized = (text ?? string.Empty).Trim();
            if (normalized.Length == 0 || normalized.Contains(','))
                return false;

            return decimal.TryParse(
                normalized,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out value);
        }

        internal static string Evaluate(
            decimal value,
            string? specification,
            string? specificationLimit,
            string? testCode,
            string? testName)
        {
            if (value < 0m)
                return "Check Required";

            string text = (specification ?? string.Empty).Trim();
            string fallbackName = (testName ?? string.Empty).Trim();

            if (text.Length > 4096 || fallbackName.Length > 4096)
                return "Check Required";

            // Do not replace an unrecognized acceptance expression with a nicer
            // test name. Only an absent/identity-only specification may fall back.
            if ((text.Length == 0 || StripTestLabel(text).Length == 0) &&
                ContainsKnownRuleSyntax(fallbackName))
                text = fallbackName;

            decimal? structuredLimit = null;
            if (TryParseControlledDecimal(specificationLimit, out decimal explicitLimit) && explicitLimit >= 0m)
                structuredLimit = explicitLimit;
            else if (!string.IsNullOrWhiteSpace(specificationLimit))
                return "Check Required";

            ConstraintParseResult parsed = ParseAcceptanceConstraints(text, testCode, fallbackName);
            if (parsed.IsInvalid)
                return "Check Required";

            if (parsed.Constraints.Count == 0)
            {
                if (structuredLimit.HasValue && IsMicrobialCountUpperLimitTest(testCode, fallbackName))
                    return value <= structuredLimit.Value ? "Conforms" : "Does Not Conform";

                return "Check Required";
            }

            if (!TryGetInterval(parsed.Constraints, out decimal? lower, out bool lowerInclusive,
                    out decimal? upper, out bool upperInclusive))
                return "Check Required";

            // Structured TAMC/TYMC limits mean inclusive NMT upper bounds.
            // Compare the canonical intersection, including open/closed bounds,
            // not merely a single <= token. Redundant weaker bounds are harmless.
            if (structuredLimit.HasValue && IsMicrobialCountUpperLimitTest(testCode, fallbackName) &&
                (!upper.HasValue || upper.Value != structuredLimit.Value || !upperInclusive))
                return "Check Required";

            foreach (RuleConstraint constraint in parsed.Constraints)
            {
                if (!EvaluateConstraint(value, constraint))
                    return "Does Not Conform";
            }

            return "Conforms";
        }

        private static ConstraintParseResult ParseAcceptanceConstraints(string text, string? testCode, string? testName)
        {
            if (string.IsNullOrWhiteSpace(text))
                return ConstraintParseResult.Empty;

            string[] clauses = Regex.Split(text, @"[;\r\n]+", RuleOptions)
                .Select(clause => clause.Trim())
                .Where(clause => clause.Length > 0)
                .ToArray();
            if (clauses.Length == 0)
                return ConstraintParseResult.Empty;

            TestIdentity target = ResolveTestIdentity(testCode, testName);
            List<string> ruleClauses = clauses
                .Where(clause => !IsPureContextClause(clause))
                .Where(clause => StripTestLabel(clause).Length > 0)
                .ToList();

            if (target != TestIdentity.None && ruleClauses.Count > 0)
            {
                List<string> exact = ruleClauses
                    .Where(clause => ResolveTestIdentity(null, clause) == target)
                    .ToList();
                List<string> unlabelled = ruleClauses
                    .Where(clause => ResolveTestIdentity(null, clause) == TestIdentity.None)
                    .ToList();

                if (exact.Count > 0)
                {
                    // Keep unlabelled acceptance constraints together with the clause
                    // explicitly labelled for this test. This supports controlled forms
                    // such as `TAMC NMT 105; NLT 95` without borrowing another test's rule.
                    ruleClauses = exact.Concat(unlabelled).ToList();
                }
                else if (unlabelled.Count > 0)
                {
                    ruleClauses = unlabelled;
                }
                else
                {
                    // Every recognized acceptance clause is explicitly labelled for a
                    // different microbiology test. Applying another test's limit would
                    // be a false conformity decision, so fail closed.
                    return ConstraintParseResult.Invalid;
                }
            }

            if (ruleClauses.Count == 0)
                return ConstraintParseResult.Empty;

            List<RuleConstraint> all = new List<RuleConstraint>();
            foreach (string clause in ruleClauses)
            {
                ClauseParseResult parsedClause = ParseClause(clause);
                if (parsedClause.IsInvalid)
                    return ConstraintParseResult.Invalid;
                all.AddRange(parsedClause.Constraints);
            }

            if (all.Count == 0)
                return ConstraintParseResult.Invalid;

            return new ConstraintParseResult(all, false);
        }

        private const string UnitPattern =
            @"(?:CFU(?:\s*/\s*(?:\d+\s*)?(?:g|mL|L|m3|m\^3|cm2|cm\^2|plate|swab|glove)(?:\s+or\s+(?:g|mL))?)|%|pH)";

        private static string StripTestLabel(string text)
        {
            return Regex.Replace(text.Trim(),
                @"^(?:TAMC\b|TYMC\b|TOTAL\s+AEROBIC\s+MICROBIAL(?:\s+COUNT)?\b|" +
                @"TOTAL\s+(?:COMBINED\s+)?YEAST(?:S)?\s+AND\s+MO(?:U)?LD(?:S)?(?:\s+COUNT)?\b)\s*[:=]?\s*",
                string.Empty, RuleOptions).Trim();
        }

        private static ClauseParseResult ParseClause(string clause)
        {
            string remaining = StripTestLabel(clause);
            if (remaining.Length == 0)
                return ClauseParseResult.Empty;

            List<RuleConstraint> constraints = new List<RuleConstraint>();
            while (remaining.Length > 0)
            {
                RuleConstraint constraint;
                int consumed;
                Match range = Regex.Match(remaining,
                    @"^(?:BETWEEN\s+(?<first>" + LimitTokenPattern + @")(?![\d.,eE^])\s+(?:AND|TO)\s+(?<second>" + LimitTokenPattern + @")" +
                    @"|(?:RANGE\s*[:=]?\s*)?(?<first>" + LimitTokenPattern + @")(?![\d.,eE^])\s*(?:TO|[-\u2013\u2014])\s*(?<second>" + LimitTokenPattern + @"))(?![\d.,eE^])",
                    RuleOptions);
                if (range.Success)
                {
                    if (!TryParseRange(range, out constraint))
                        return ClauseParseResult.Invalid;
                    consumed = range.Length;
                }
                else
                {
                    Match op = OperatorRegex.Match(remaining);
                    if (!op.Success || op.Index != 0)
                        return ClauseParseResult.Invalid;
                    string separator = char.IsLetter(op.Value[0]) ? @"[:=]?\s*" : string.Empty;
                    Match numeric = Regex.Match(remaining[op.Length..],
                        @"^\s*" + separator + @"(?<value>" + LimitTokenPattern + @")(?![\d.,eE^])", RuleOptions);
                    if (!numeric.Success ||
                        !TryParseSpecificationLimitToken(numeric.Groups["value"].Value, out decimal limit))
                        return ClauseParseResult.Invalid;
                    RuleKind kind = op.Groups["ge"].Success ? RuleKind.GreaterOrEqual
                        : op.Groups["le"].Success ? RuleKind.LessOrEqual
                        : op.Groups["lt"].Success ? RuleKind.LessThan
                        : op.Groups["gt"].Success ? RuleKind.GreaterThan : RuleKind.Equal;
                    constraint = new RuleConstraint(kind, limit, null);
                    consumed = op.Length + numeric.Length;
                }

                // Each character is consumed exactly once. No second regex pass
                // may reinterpret '=' after NMT/RANGE as an equality predicate.
                remaining = remaining[consumed..].TrimStart();
                Match unit = Regex.Match(remaining, @"^" + UnitPattern + @"(?![\w/])", RuleOptions);
                if (unit.Success)
                    remaining = remaining[unit.Length..].TrimStart();

                // Permit an explicitly equivalent numeric annotation, not arbitrary
                // parenthetical prose or another hidden acceptance rule.
                if (remaining.StartsWith("(", StringComparison.Ordinal))
                {
                    Match annotation = Regex.Match(remaining,
                        @"^\(\s*(?<value>" + LimitTokenPattern + @")(?:\s+" + UnitPattern + @")?\s*\)",
                        RuleOptions);
                    if (!annotation.Success || constraint.Kind == RuleKind.Range ||
                        !TryParseSpecificationLimitToken(annotation.Groups["value"].Value, out decimal annotated) ||
                        annotated != constraint.First)
                        return ClauseParseResult.Invalid;
                    remaining = remaining[annotation.Length..].TrimStart();
                }

                constraints.Add(constraint);
                if (remaining.Length == 0) break;

                Match connector = Regex.Match(remaining, @"^AND\b\s*", RuleOptions);
                if (!connector.Success) return ClauseParseResult.Invalid;
                remaining = remaining[connector.Length..].TrimStart();
                if (remaining.Length == 0) return ClauseParseResult.Invalid;
            }
            return constraints.Count == 0 ? ClauseParseResult.Invalid : new ClauseParseResult(constraints, false);
        }

        private static bool TryGetInterval(
            IEnumerable<RuleConstraint> constraints,
            out decimal? lower, out bool lowerInclusive, out decimal? upper, out bool upperInclusive)
        {
            decimal? low = null, high = null;
            bool lowClosed = true, highClosed = true;
            void Lower(decimal value, bool inclusive)
            {
                if (!low.HasValue || value > low.Value) { low = value; lowClosed = inclusive; }
                else if (value == low.Value) lowClosed &= inclusive;
            }
            void Upper(decimal value, bool inclusive)
            {
                if (!high.HasValue || value < high.Value) { high = value; highClosed = inclusive; }
                else if (value == high.Value) highClosed &= inclusive;
            }
            foreach (RuleConstraint c in constraints)
            {
                if (!c.First.HasValue) continue;
                switch (c.Kind)
                {
                    case RuleKind.GreaterOrEqual: Lower(c.First.Value, true); break;
                    case RuleKind.GreaterThan: Lower(c.First.Value, false); break;
                    case RuleKind.LessOrEqual: Upper(c.First.Value, true); break;
                    case RuleKind.LessThan: Upper(c.First.Value, false); break;
                    case RuleKind.Equal: Lower(c.First.Value, true); Upper(c.First.Value, true); break;
                    case RuleKind.Range:
                        Lower(c.First.Value, true);
                        if (c.Second.HasValue) Upper(c.Second.Value, true);
                        break;
                }
            }
            lower = low; lowerInclusive = lowClosed; upper = high; upperInclusive = highClosed;
            return !low.HasValue || !high.HasValue ||
                low.Value < high.Value || (low.Value == high.Value && lowClosed && highClosed);
        }

        private static bool TryParseRange(Match match, out RuleConstraint constraint)
        {
            constraint = default;
            if (!TryParseSpecificationLimitToken(match.Groups["first"].Value, out decimal first) ||
                !TryParseSpecificationLimitToken(match.Groups["second"].Value, out decimal second))
            {
                return false;
            }

            if (first > second) return false;
            constraint = new RuleConstraint(RuleKind.Range, first, second);
            return true;
        }

        private static bool TryParseSpecificationLimitToken(string? text, out decimal value)
        {
            value = 0m;
            string normalized = (text ?? string.Empty).Trim();
            if (normalized.Length == 0 || normalized.Contains(','))
                return false;

            Match power = Regex.Match(normalized, @"^10\s*\^\s*(?<exponent>[+-]?\d+)$", RuleOptions);
            if (power.Success)
            {
                if (!int.TryParse(power.Groups["exponent"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int exponent) ||
                    exponent < -28 || exponent > 28)
                {
                    return false;
                }

                decimal result = 1m;
                if (exponent >= 0)
                {
                    for (int i = 0; i < exponent; i++)
                        result *= 10m;
                }
                else
                {
                    for (int i = 0; i > exponent; i--)
                        result /= 10m;
                }

                value = result;
                return true;
            }

            if (!decimal.TryParse(
                normalized,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out value))
                return false;
            string coefficient = Regex.Split(normalized, "[eE]")[0];
            return value != 0m || !Regex.IsMatch(coefficient, "[1-9]", RuleOptions);
        }

        private static bool EvaluateConstraint(decimal value, RuleConstraint constraint)
        {
            if (!constraint.First.HasValue)
                return false;

            decimal limit = constraint.First.Value;
            return constraint.Kind switch
            {
                RuleKind.Range => constraint.Second.HasValue && value >= limit && value <= constraint.Second.Value,
                RuleKind.GreaterOrEqual => value >= limit,
                RuleKind.LessOrEqual => value <= limit,
                RuleKind.GreaterThan => value > limit,
                RuleKind.LessThan => value < limit,
                RuleKind.Equal => value == limit,
                _ => false
            };
        }

        private static bool ContainsKnownRuleSyntax(string? text)
        {
            string value = text ?? string.Empty;
            return OperatorRegex.IsMatch(value) ||
                   Regex.IsMatch(value, @"\b(?:BETWEEN|RANGE)\b", RuleOptions) ||
                   Regex.IsMatch(
                       value,
                       @"^\s*" + LimitTokenPattern + @"(?![\d.,eE^])\s*(?:\bTO\b|[-\u2013\u2014])\s*" + LimitTokenPattern + @"(?![\d.,eE^])",
                       RuleOptions);
        }

        private static bool IsPureContextClause(string clause)
        {
            return Regex.IsMatch(clause,
                @"^(?:INCUBATION|INCUBATE(?:D)?|TEMPERATURE|TEMP|GROWTH\s+CONDITIONS?)\s*" +
                @"(?:(?:RANGE\s*[:=]?\s*)?(?:\d+(?:\.\d+)?)\s*(?:(?:TO|[-\u2013\u2014])\s*\d+(?:\.\d+)?)?|" +
                @"BETWEEN\s+\d+(?:\.\d+)?\s+AND\s+\d+(?:\.\d+)?)" +
                @"(?:\s*(?:°?\s*C|CELSIUS|DEGREES?\s+CELSIUS))?" +
                @"(?:\s+FOR\s+\d+(?:\.\d+)?\s+(?:HOURS?|DAYS?))?\s*$",
                RuleOptions);
        }

        private static TestIdentity ResolveTestIdentity(string? testCode, string? text)
        {
            string code = (testCode ?? string.Empty).Trim();
            string value = (text ?? string.Empty).Trim();

            if (code.Equals("TAMC", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(value, @"\bTAMC\b|TOTAL\s+AEROBIC\s+MICROBIAL", RuleOptions))
            {
                return TestIdentity.Tamc;
            }

            if (code.Equals("TYMC", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(value, @"\bTYMC\b|YEAST(?:S)?\s+AND\s+MO(?:U)?LD|TOTAL\s+(?:COMBINED\s+)?YEAST", RuleOptions))
            {
                return TestIdentity.Tymc;
            }

            return TestIdentity.None;
        }

        private static bool IsMicrobialCountUpperLimitTest(string? testCode, string? testName)
        {
            string code = (testCode ?? string.Empty).Trim();
            string name = (testName ?? string.Empty).Trim();
            return code.Equals("TAMC", StringComparison.OrdinalIgnoreCase) ||
                   code.Equals("TYMC", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Total Aerobic Microbial Count", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Total Yeast and Mold Count", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Total Yeast and Mould Count", StringComparison.OrdinalIgnoreCase);
        }

        private enum TestIdentity
        {
            None,
            Tamc,
            Tymc
        }

        private enum RuleKind
        {
            Range,
            GreaterOrEqual,
            LessOrEqual,
            GreaterThan,
            LessThan,
            Equal
        }

        private readonly struct RuleConstraint
        {
            internal RuleConstraint(RuleKind kind, decimal? first, decimal? second)
            {
                Kind = kind;
                First = first;
                Second = second;
            }

            internal RuleKind Kind { get; }
            internal decimal? First { get; }
            internal decimal? Second { get; }
        }

        private readonly struct ConstraintParseResult
        {
            internal static ConstraintParseResult Empty => new(Array.Empty<RuleConstraint>(), false);
            internal static ConstraintParseResult Invalid => new(Array.Empty<RuleConstraint>(), true);

            internal ConstraintParseResult(IReadOnlyList<RuleConstraint> constraints, bool isInvalid)
            {
                Constraints = constraints;
                IsInvalid = isInvalid;
            }

            internal IReadOnlyList<RuleConstraint> Constraints { get; }
            internal bool IsInvalid { get; }
        }

        private readonly struct ClauseParseResult
        {
            internal static ClauseParseResult Empty => new(Array.Empty<RuleConstraint>(), false);
            internal static ClauseParseResult Invalid => new(Array.Empty<RuleConstraint>(), true);

            internal ClauseParseResult(IReadOnlyList<RuleConstraint> constraints, bool isInvalid)
            {
                Constraints = constraints;
                IsInvalid = isInvalid;
            }

            internal IReadOnlyList<RuleConstraint> Constraints { get; }
            internal bool IsInvalid { get; }
        }
    }
}
