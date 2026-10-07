namespace PharmaLIMS.Services;

internal static class MediaGrowthPromotionEvaluator
{
    private const decimal MaximumCount = 9999999999999999.99m;
    private const decimal MaximumRecovery = 9999999.99m;

    internal static bool TryCalculate(string? controlText, string? testText, out decimal recovery)
    {
        recovery = 0m;
        if (!ControlledNumericValue.TryParse(controlText, out decimal control, allowExponent: false) ||
            !ControlledNumericValue.TryParse(testText, out decimal test, allowExponent: false) ||
            control <= 0m || test < 0m || control > MaximumCount || test > MaximumCount ||
            decimal.Round(control, 2) != control || decimal.Round(test, 2) != test)
            return false;
        recovery = test * 100m / control;
        return decimal.Round(recovery, 2) <= MaximumRecovery;
    }

    internal static bool Passes(string? controlText, string? testText, decimal? minimum, decimal? maximum)
    {
        if (!minimum.HasValue || !maximum.HasValue || minimum < 0m || maximum < minimum || maximum > MaximumRecovery ||
            !TryCalculate(controlText, testText, out _)) return false;
        ControlledNumericValue.TryParse(controlText, out decimal control, allowExponent: false);
        ControlledNumericValue.TryParse(testText, out decimal test, allowExponent: false);
        // Compare before division or display rounding; the stored original counts
        // remain the evidence behind the formatted recovery percentage.
        return test * 100m >= minimum.Value * control && test * 100m <= maximum.Value * control;
    }
}
