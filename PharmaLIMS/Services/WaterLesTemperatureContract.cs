namespace PharmaLIMS.Services;
internal static class WaterLesTemperatureContract
{
    internal static bool TryParse(string? text, out decimal value) =>
        ControlledNumericValue.TryParse(text, out value, allowExponent: false) && value >= 24m && value <= 26m && decimal.Round(value,2)==value;
    internal static decimal Parse(string? text)
    {
        if (!TryParse(text,out decimal value)) throw new System.InvalidOperationException("Conductivity LES requires 24–26 C using '.', without grouping or precision beyond 0.01 C.");
        return value;
    }
}
