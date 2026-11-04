using System;
using System.Collections.Generic;
using System.Linq;

namespace PharmaLIMS.Services
{
    /// <summary>
    /// Restrictive resource policy: only unequivocally visual/sensory water tests
    /// may be recorded without physical laboratory equipment. Unknown tests fail closed.
    /// </summary>
    public static class WaterTestResourcePolicy
    {
        private static readonly HashSet<string> NoInstrumentTests = new(StringComparer.OrdinalIgnoreCase)
        {
            "Appearance", "Appearance (Color & Clarity)", "Color & Clarity",
            "Colour & Clarity", "Description", "Odor", "Odour", "Taste",
            "Turbidity"
        };

        public static bool IsNoInstrumentRequired(string? testName)
        {
            if (string.IsNullOrWhiteSpace(testName)) return false;
            return NoInstrumentTests.Contains(testName.Trim());
        }

        public static IReadOnlyList<LabEquipmentChoice> FilterEquipment(
            string? testName, IEnumerable<LabEquipmentChoice> equipment)
        {
            var all = equipment.ToList();
            if (IsNoInstrumentRequired(testName) || WaterResourceEvidenceService.RequiresKit(testName))
                return Array.Empty<LabEquipmentChoice>();
            string name = (testName ?? string.Empty).Trim().ToUpperInvariant();
            string[] terms = name switch
            {
                "PH VALUE" or "PH" or "P.H" => new[] { "PH METER" },
                "CONDUCTIVITY" or "ELECTRICAL CONDUCTIVITY" => new[] { "CONDUCTIVITY" },
                "RESIDUE ON EVAPORATION" => new[] { "BALANCE", "OVEN", "HOT PLATE" },
                _ => Array.Empty<string>()
            };
            if (terms.Length == 0) return all;
            return all.Where(e => terms.Any(t =>
                (e.EquipmentName + " " + e.EquipmentType).Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        public static bool IsEquipmentSelectionValid(
            string? testName, int? equipmentId, IEnumerable<LabEquipmentChoice> choices)
        {
            if (IsNoInstrumentRequired(testName)) return !equipmentId.HasValue;
            if (!equipmentId.HasValue) return false;
            return FilterEquipment(testName, choices).Any(e =>
                e.EquipmentID == equipmentId.Value && e.IsAvailable);
        }
    }
}
