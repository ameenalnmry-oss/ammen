using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace PharmaLIMS.Services;

/// <summary>Invariant, ungrouped numbers; never silently round during parsing.</summary>
internal static class ControlledNumericValue
{
    internal static bool TryParse(string? text, out decimal value, bool allowExponent = true)
    {
        value = 0m;
        string input = (text ?? string.Empty).Trim();
        if (input.Length == 0 || input.Length > 128 ||
            !Regex.IsMatch(input, @"^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant))
            return false;
        int exponentAt = input.IndexOfAny(new[] { 'e', 'E' });
        if (!allowExponent && exponentAt >= 0) return false;
        int exponent = 0;
        if (exponentAt >= 0 && (!int.TryParse(input[(exponentAt + 1)..], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent) || exponent < -1000 || exponent > 1000))
            return false;
        string mantissa = exponentAt < 0 ? input : input[..exponentAt];
        int dot = mantissa.IndexOf('.');
        int scale = (dot < 0 ? 0 : mantissa.Length - dot - 1) - exponent;
        string digits = mantissa.TrimStart('+', '-').Replace(".", string.Empty);
        BigInteger coefficient = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        if (coefficient.IsZero) { value = 0m; return true; }
        while (scale > 0 && coefficient % 10 == 0) { coefficient /= 10; scale--; }
        if (scale > 28) return false;
        if (scale < 0) { coefficient *= BigInteger.Pow(10, -scale); scale = 0; }
        if (coefficient > ((BigInteger.One << 96) - 1)) return false;
        value = new decimal((int)(uint)(coefficient & uint.MaxValue),
            (int)(uint)((coefficient >> 32) & uint.MaxValue),
            (int)(uint)((coefficient >> 64) & uint.MaxValue), input[0] == '-', (byte)scale);
        return true;
    }
}
