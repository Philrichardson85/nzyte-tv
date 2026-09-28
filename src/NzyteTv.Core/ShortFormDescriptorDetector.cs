using System.Globalization;
using System.Text.RegularExpressions;

namespace NzyteTv.Core;

public sealed record ShortFormDescriptor(string Subtype, int? SequenceNumber, int PrefixTokenCount);

public static partial class ShortFormDescriptorDetector
{
    private static readonly IReadOnlyDictionary<string, string> DescriptorSubtypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pov"] = "pov",
            ["lipsync"] = "lipsync",
            ["stock"] = "stock",
            ["micdrop"] = "mic-drop",
            ["thought"] = "thought",
            ["meme"] = "meme",
            ["ai"] = "ai-visual",
        };

    public static ShortFormDescriptor? Detect(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        string[] tokens = SongMatcher.Normalize(Path.GetFileNameWithoutExtension(fileName))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return null;
        }

        int descriptorIndex = tokens.Length >= 3
            && tokens[0] == "content"
            && tokens[1] == "template"
                ? 2
                : 0;
        if (descriptorIndex == 0 && tokens[0] == "bts")
        {
            return new ShortFormDescriptor("behind-the-scenes", null, 1);
        }

        string descriptorToken = tokens[descriptorIndex];
        if (descriptorToken == "lip" && descriptorIndex + 1 < tokens.Length && tokens[descriptorIndex + 1].StartsWith("sync", StringComparison.Ordinal))
        {
            descriptorToken = $"lipsync{tokens[descriptorIndex + 1][4..]}";
        }
        else if (descriptorToken == "mic" && descriptorIndex + 1 < tokens.Length && tokens[descriptorIndex + 1].StartsWith("drop", StringComparison.Ordinal))
        {
            descriptorToken = $"micdrop{tokens[descriptorIndex + 1][4..]}";
        }

        Match match = NumberedDescriptor().Match(descriptorToken);
        if (!match.Success || !DescriptorSubtypes.TryGetValue(match.Groups["name"].Value, out string? subtype))
        {
            return null;
        }

        int? sequence = match.Groups["number"].Success
            ? int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture)
            : null;
        return new ShortFormDescriptor(subtype, sequence, descriptorIndex + 1);
    }

    [GeneratedRegex("^(?<name>pov|lipsync|stock|micdrop|thought|meme|ai)(?<number>[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedDescriptor();
}
