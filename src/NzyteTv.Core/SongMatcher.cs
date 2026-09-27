using System.Text;

namespace NzyteTv.Core;

public enum SongMatchStatus
{
    Matched,
    Ambiguous,
    Unresolved,
}

public sealed record SongMatchResult(
    SongMatchStatus Status,
    SongCatalogEntry? Match,
    IReadOnlyList<SongCatalogEntry> Candidates,
    string DetectedTitle,
    string Reason);

public static class SongMatcher
{
    private static readonly string[][] SuffixNoise =
    [
        ["official", "music", "video"],
        ["official", "video"],
        ["lyric", "video"],
        ["performance"],
        ["visualizer"],
    ];

    public static SongMatchResult Match(string fileName, SongCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        SongCatalogValidator.Validate(catalog);

        string detectedTitle = GetDetectedTitle(fileName);
        List<MatchEvidence> exactTitles = FindExactCanonicalMatches(detectedTitle, catalog);
        List<MatchEvidence> exactAliases = FindExactAliasMatches(detectedTitle, catalog);

        if (exactTitles.Count > 0)
        {
            List<SongCatalogEntry> exactCandidates = DistinctSongs([.. exactTitles, .. exactAliases]);
            if (exactCandidates.Count > 1)
            {
                return Ambiguous(
                    exactCandidates,
                    detectedTitle,
                    "The detected title exactly matches a canonical title and/or alias on multiple catalog entries.");
            }

            return Matched(exactCandidates[0], "The detected title exactly matches one canonical catalog title.");
        }

        if (exactAliases.Count > 0)
        {
            List<SongCatalogEntry> exactCandidates = DistinctSongs(exactAliases);
            if (exactCandidates.Count > 1)
            {
                return Ambiguous(
                    exactCandidates,
                    detectedTitle,
                    "The detected title exactly matches the same catalog alias on multiple entries.");
            }

            return Matched(exactCandidates[0], "The detected title exactly matches one catalog alias.");
        }

        List<MatchEvidence> contained = FindContainedMatches(detectedTitle, catalog);
        List<MatchEvidence> mostSpecific = RemoveMatchesContainedWithinLongerMatches(contained);
        List<SongCatalogEntry> candidates = DistinctSongs(mostSpecific);
        if (candidates.Count == 1)
        {
            MatchEvidence evidence = mostSpecific.First(item =>
                string.Equals(item.Song.ContentGroupId, candidates[0].ContentGroupId, StringComparison.Ordinal));
            string source = evidence.IsAlias ? "catalog alias" : "canonical catalog title";
            return Matched(candidates[0], $"One boundary-safe {source} occurs as a distinct filename token sequence.");
        }

        if (candidates.Count > 1)
        {
            return Ambiguous(
                candidates,
                detectedTitle,
                "Multiple distinct boundary-safe catalog titles or aliases remain plausible after preferring overlapping longer matches.");
        }

        return new SongMatchResult(
            SongMatchStatus.Unresolved,
            null,
            [],
            detectedTitle,
            "No exact or boundary-safe song catalog title or alias match was found.");
    }

    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool needsSpace = false;
        foreach (char character in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character))
            {
                if (needsSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(character));
                needsSpace = false;
            }
            else
            {
                needsSpace = true;
            }
        }

        return builder.ToString();
    }

    public static string GetDetectedTitle(string fileName)
    {
        var tokens = Normalize(Path.GetFileNameWithoutExtension(fileName))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        ShortFormDescriptor? shortForm = ShortFormDescriptorDetector.Detect(fileName);
        if (shortForm is not null && shortForm.PrefixTokenCount <= tokens.Count)
        {
            tokens.RemoveRange(0, shortForm.PrefixTokenCount);
        }
        else if (tokens.Count >= 2 && tokens[0] == "content" && tokens[1] == "template")
        {
            tokens.RemoveRange(0, 2);
        }

        bool removed;
        do
        {
            removed = false;
            foreach (string[] suffix in SuffixNoise.OrderByDescending(value => value.Length))
            {
                if (EndsWith(tokens, suffix))
                {
                    tokens.RemoveRange(tokens.Count - suffix.Length, suffix.Length);
                    removed = true;
                    break;
                }
            }

            if (!removed
                && tokens.Count >= 2
                && tokens[^2] == "content"
                && tokens[^1].All(char.IsDigit))
            {
                tokens.RemoveRange(tokens.Count - 2, 2);
                removed = true;
            }
        }
        while (removed);

        return string.Join(' ', tokens);
    }

    private static List<MatchEvidence> FindExactCanonicalMatches(string detectedTitle, SongCatalog catalog) =>
        catalog.Songs!
            .Where(song => string.Equals(Normalize(song.Title!), detectedTitle, StringComparison.Ordinal))
            .Select(song => new MatchEvidence(song, IsAlias: false, 0, TokenCount(detectedTitle)))
            .ToList();

    private static List<MatchEvidence> FindExactAliasMatches(string detectedTitle, SongCatalog catalog) =>
        catalog.Songs!
            .SelectMany(song => song.Aliases?.Select(alias => (Song: song, Alias: alias)) ?? [])
            .Where(item => string.Equals(Normalize(item.Alias), detectedTitle, StringComparison.Ordinal))
            .Select(item => new MatchEvidence(item.Song, IsAlias: true, 0, TokenCount(detectedTitle)))
            .ToList();

    private static List<MatchEvidence> FindContainedMatches(string detectedTitle, SongCatalog catalog)
    {
        string[] fileTokens = detectedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = new List<MatchEvidence>();
        foreach (SongCatalogEntry song in catalog.Songs!)
        {
            AddOccurrences(matches, song, Normalize(song.Title!), isAlias: false, fileTokens);
            foreach (string alias in song.Aliases ?? [])
            {
                AddOccurrences(matches, song, Normalize(alias), isAlias: true, fileTokens);
            }
        }

        return matches;
    }

    private static void AddOccurrences(
        ICollection<MatchEvidence> matches,
        SongCatalogEntry song,
        string normalizedLabel,
        bool isAlias,
        IReadOnlyList<string> fileTokens)
    {
        string[] labelTokens = normalizedLabel.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (labelTokens.Length == 0 || labelTokens.Length > fileTokens.Count)
        {
            return;
        }

        for (int index = 0; index <= fileTokens.Count - labelTokens.Length; index++)
        {
            bool equal = true;
            for (int offset = 0; offset < labelTokens.Length; offset++)
            {
                if (!string.Equals(fileTokens[index + offset], labelTokens[offset], StringComparison.Ordinal))
                {
                    equal = false;
                    break;
                }
            }

            if (equal)
            {
                matches.Add(new MatchEvidence(song, isAlias, index, labelTokens.Length));
            }
        }
    }

    private static List<MatchEvidence> RemoveMatchesContainedWithinLongerMatches(
        IReadOnlyList<MatchEvidence> matches) =>
        matches.Where(match => !matches.Any(other =>
                other.Length > match.Length
                && other.Start <= match.Start
                && other.Start + other.Length >= match.Start + match.Length))
            .ToList();

    private static List<SongCatalogEntry> DistinctSongs(IEnumerable<MatchEvidence> matches) =>
        matches.Select(match => match.Song)
            .DistinctBy(song => song.ContentGroupId, StringComparer.Ordinal)
            .ToList();

    private static int TokenCount(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool EndsWith(IReadOnlyList<string> values, IReadOnlyList<string> suffix)
    {
        if (suffix.Count > values.Count)
        {
            return false;
        }

        int start = values.Count - suffix.Count;
        for (int index = 0; index < suffix.Count; index++)
        {
            if (!string.Equals(values[start + index], suffix[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static SongMatchResult Matched(SongCatalogEntry match, string reason) => new(
        SongMatchStatus.Matched,
        match,
        [match],
        match.Title!,
        reason);

    private static SongMatchResult Ambiguous(
        IReadOnlyList<SongCatalogEntry> candidates,
        string detectedTitle,
        string reason) => new(
        SongMatchStatus.Ambiguous,
        null,
        candidates,
        detectedTitle,
        reason);

    private sealed record MatchEvidence(
        SongCatalogEntry Song,
        bool IsAlias,
        int Start,
        int Length);
}
