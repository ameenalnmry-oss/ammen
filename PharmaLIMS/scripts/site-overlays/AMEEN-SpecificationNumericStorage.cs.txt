using System;
namespace PharmaLIMS.Services;
internal static class SpecificationNumericStorage
{
    internal static void EnsureExact(decimal? value)
    {
        if (value.HasValue && (value.Value < 0m || value.Value > 999999999999999.999m || decimal.Round(value.Value, 3) != value.Value))
            throw new InvalidOperationException("Numeric specification limits must fit decimal(18,3) exactly, without rounding.");
    }
}
