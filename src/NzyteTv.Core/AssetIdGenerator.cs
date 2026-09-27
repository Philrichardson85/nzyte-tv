using System.Security.Cryptography;
using System.Text;

namespace NzyteTv.Core;

public static class AssetIdGenerator
{
    private static readonly HashSet<string> PerformanceDescriptors = new(StringComparer.Ordinal)
    {
        "official",
        "music",
        "video",
        "performance",
        "visualizer",
    };

    public static string Generate(
        string sourcePath,
        string sourceRelativePath,
        string type,
        SongCatalogEntry? song,
        ISet<string> reservedIds,
        ShortFormDescriptor? shortFormDescriptor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        string baseId = CreateBaseId(sourcePath, type, song, shortFormDescriptor);
        if (reservedIds.Add(baseId))
        {
            return baseId;
        }

        string normalizedRelativePath = sourceRelativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant();
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRelativePath)))
            [..8]
            .ToLowerInvariant();
        string candidate = $"{baseId}-{suffix}";
        int discriminator = 2;
        while (!reservedIds.Add(candidate))
        {
            candidate = $"{baseId}-{suffix}-{discriminator++}";
        }

        return candidate;
    }

    private static string CreateBaseId(
        string sourcePath,
        string type,
        SongCatalogEntry? song,
        ShortFormDescriptor? shortFormDescriptor)
    {
        if (song is not null)
        {
            if (type == AssetTypes.ShortForm && shortFormDescriptor is not null)
            {
                string sequence = shortFormDescriptor.SequenceNumber is int number
                    ? $"-{number:00}"
                    : string.Empty;
                return $"{song.ContentGroupId}-{shortFormDescriptor.Subtype}{sequence}";
            }

            string baseId = $"{song.ContentGroupId}-{type}";
            if (type != AssetTypes.Performance)
            {
                return baseId;
            }

            List<string> remaining = SongMatcher.Normalize(Path.GetFileNameWithoutExtension(sourcePath))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            RemoveSequence(remaining, SongMatcher.Normalize(song.Title!).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            RemoveSequence(remaining, SongMatcher.Normalize(song.Artist!).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            remaining.RemoveAll(PerformanceDescriptors.Contains);
            string detail = string.Join('-', remaining);
            return string.IsNullOrWhiteSpace(detail) ? baseId : $"{baseId}-{detail}";
        }

        string slug = string.Join('-', SongMatcher.Normalize(Path.GetFileNameWithoutExtension(sourcePath))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "asset";
        }

        return slug.EndsWith(type, StringComparison.Ordinal) ? slug : $"{slug}-{type}";
    }

    private static void RemoveSequence(List<string> values, IReadOnlyList<string> sequence)
    {
        if (sequence.Count == 0 || sequence.Count > values.Count)
        {
            return;
        }

        for (int index = 0; index <= values.Count - sequence.Count; index++)
        {
            if (sequence.Select((value, offset) => values[index + offset] == value).All(matches => matches))
            {
                values.RemoveRange(index, sequence.Count);
                return;
            }
        }
    }
}
