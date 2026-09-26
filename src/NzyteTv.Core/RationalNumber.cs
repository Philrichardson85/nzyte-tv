using System.Globalization;

namespace NzyteTv.Core;

public static class RationalNumber
{
    public static bool TryParse(string? value, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                && double.IsFinite(result);
        }

        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator)
            || denominator == 0)
        {
            return false;
        }

        result = numerator / denominator;
        return double.IsFinite(result);
    }
}
