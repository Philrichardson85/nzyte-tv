using System.Text;
using System.Text.RegularExpressions;

namespace NzyteTv.Operations.Spotlight;

internal static partial class OperationsTextSanitizer
{
    private const int MaximumLength = 160;
    [GeneratedRegex(@"(?i)^\s*(?:rtmp|rtmps)://")]
    private static partial Regex RtmpPattern();
    [GeneratedRegex(@"(?i)^\s*(?:[a-z]:[\\/]|/(?:[^/\s]+/)+|\\\\|//|\.\.?[\\/])")]
    private static partial Regex PathPattern();

    public static string Required(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumLength
            || RtmpPattern().IsMatch(value) || PathPattern().IsMatch(value))
        {
            throw new OperationsValidationException();
        }
        var result = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (!char.IsControl(character)) result.Append(character);
        }
        string sanitized = result.ToString().Trim();
        return sanitized.Length == 0 ? throw new OperationsValidationException() : sanitized;
    }
}
