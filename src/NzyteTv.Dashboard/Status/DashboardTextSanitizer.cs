using System.Text;
using System.Text.RegularExpressions;
using NzyteTv.Media;

namespace NzyteTv.Dashboard.Status;

public static partial class DashboardTextSanitizer
{
    public const int MaximumLength = 160;

    [GeneratedRegex(@"(?i)^\s*[a-z]:[\\/]")]
    private static partial Regex WindowsPathPattern();

    [GeneratedRegex(@"^\s*/(?:[^/\s]+/)+[^\s]*")]
    private static partial Regex UnixPathPattern();

    [GeneratedRegex(@"^\s*(?:\\\\|//)[^\s]+")]
    private static partial Regex NetworkPathPattern();

    [GeneratedRegex(@"^\s*\.\.?[\\/]")]
    private static partial Regex RelativePathPattern();

    [GeneratedRegex(@"(?i)^\s*(?:ffmpeg|ffprobe|systemctl|journalctl)(?:\.exe)?(?:\s|$)")]
    private static partial Regex CommandPattern();

    [GeneratedRegex(@"^\s*--?[a-zA-Z][a-zA-Z0-9-]*(?:\s|=)")]
    private static partial Regex OptionPattern();

    public static string? Sanitize(string? value, int maximumLength = MaximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (maximumLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        string redacted = StationSecretRedactor.RedactRtmpUrls(value) ?? string.Empty;
        if (WindowsPathPattern().IsMatch(redacted)
            || UnixPathPattern().IsMatch(redacted)
            || NetworkPathPattern().IsMatch(redacted)
            || RelativePathPattern().IsMatch(redacted)
            || CommandPattern().IsMatch(redacted)
            || OptionPattern().IsMatch(redacted))
        {
            return null;
        }

        var safe = new StringBuilder(Math.Min(redacted.Length, maximumLength));
        foreach (char character in redacted)
        {
            if (char.IsControl(character) || character is '<' or '>')
            {
                continue;
            }

            if (safe.Length >= maximumLength)
            {
                break;
            }

            safe.Append(character);
        }

        string result = safe.ToString().Trim();
        if (result.Length > 0 && char.IsHighSurrogate(result[^1]))
        {
            result = result[..^1];
        }

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
