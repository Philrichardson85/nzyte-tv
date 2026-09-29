using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using NzyteTv.Core;

namespace NzyteTv.Media;

public interface IBroadcastPlanner
{
    BroadcastPlan CreatePlan(IReadOnlyList<string> playlistPaths, string libraryRoot);
}

public sealed class BroadcastPlanner : IBroadcastPlanner
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public BroadcastPlan CreatePlan(
        IReadOnlyList<string> playlistPaths,
        string libraryRoot)
    {
        ArgumentNullException.ThrowIfNull(playlistPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        if (playlistPaths.Count == 0)
        {
            throw new InvalidOperationException("At least one playlist file is required for broadcast.");
        }

        string root = Path.GetFullPath(libraryRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Library root not found: {root}");
        }

        var resolvedPlaylists = new List<string>(playlistPaths.Count);
        var playlistContentHashes = new List<string>(playlistPaths.Count);
        var resolvedItems = new List<BroadcastPlanItem>();
        var issues = new List<BroadcastPlanIssue>();
        int scheduledItemCount = 0;
        double scheduledDurationSeconds = 0;

        foreach (string playlistPath in playlistPaths)
        {
            string fullPlaylistPath = Path.GetFullPath(playlistPath);
            if (!File.Exists(fullPlaylistPath))
            {
                throw new FileNotFoundException($"Playlist file not found: {fullPlaylistPath}", fullPlaylistPath);
            }

            BroadcastPlaylistInput playlist = ReadPlaylist(fullPlaylistPath, out string contentHash);
            BroadcastPlaylistItemInput[] orderedItems = ValidateAndOrderItems(playlist, fullPlaylistPath);
            resolvedPlaylists.Add(fullPlaylistPath);
            playlistContentHashes.Add(contentHash);
            scheduledItemCount += orderedItems.Length;
            scheduledDurationSeconds += orderedItems.Sum(item => item.DurationSeconds);

            foreach (BroadcastPlaylistItemInput item in orderedItems)
            {
                ResolveItem(root, fullPlaylistPath, item, resolvedItems, issues);
            }
        }

        return new BroadcastPlan(
            root,
            resolvedPlaylists,
            resolvedItems,
            issues,
            scheduledItemCount,
            scheduledDurationSeconds)
        {
            PlaylistContentHashes = playlistContentHashes,
        };
    }

    private static BroadcastPlaylistInput ReadPlaylist(string playlistPath, out string contentHash)
    {
        try
        {
            string content = File.ReadAllText(playlistPath);
            contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
                .ToLowerInvariant();
            BroadcastPlaylistInput? playlist = JsonSerializer.Deserialize<BroadcastPlaylistInput>(
                content,
                ReadOptions);
            if (playlist is null)
            {
                throw new InvalidDataException($"Playlist JSON is empty: {playlistPath}");
            }

            if (playlist.SchemaVersion != PlaylistDocument.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported playlist schemaVersion {playlist.SchemaVersion} in '{playlistPath}'; " +
                    $"expected {PlaylistDocument.CurrentSchemaVersion}.");
            }

            if (playlist.Items is null)
            {
                throw new InvalidDataException($"Playlist items collection is missing: {playlistPath}");
            }

            if (playlist.Items.Count == 0)
            {
                throw new InvalidDataException($"Playlist contains no items: {playlistPath}");
            }

            return playlist;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Malformed playlist JSON in '{playlistPath}': {exception.Message}",
                exception);
        }
    }

    private static BroadcastPlaylistItemInput[] ValidateAndOrderItems(
        BroadcastPlaylistInput playlist,
        string playlistPath)
    {
        BroadcastPlaylistItemInput[] ordered = playlist.Items!
            .OrderBy(item => item.Sequence)
            .ToArray();
        if (ordered.GroupBy(item => item.Sequence).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException($"Playlist contains duplicate sequence values: {playlistPath}");
        }

        for (int index = 0; index < ordered.Length; index++)
        {
            int expected = index + 1;
            if (ordered[index].Sequence != expected)
            {
                throw new InvalidDataException(
                    $"Playlist sequence values must be contiguous starting at 1; expected {expected} in '{playlistPath}'.");
            }

            if (!double.IsFinite(ordered[index].DurationSeconds) || ordered[index].DurationSeconds <= 0)
            {
                throw new InvalidDataException(
                    $"Playlist item sequence {expected} has an invalid duration in '{playlistPath}'.");
            }
        }

        return ordered;
    }

    private static void ResolveItem(
        string libraryRoot,
        string playlistPath,
        BroadcastPlaylistItemInput item,
        ICollection<BroadcastPlanItem> resolvedItems,
        ICollection<BroadcastPlanIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(item.RelativePath))
        {
            issues.Add(new BroadcastPlanIssue(
                BroadcastPlanIssueKind.InvalidPath,
                playlistPath,
                item.Sequence,
                "relativePath is missing."));
            return;
        }

        string mediaPath;
        try
        {
            if (Path.IsPathRooted(item.RelativePath))
            {
                throw new InvalidOperationException();
            }

            mediaPath = Path.GetFullPath(Path.Combine(libraryRoot, item.RelativePath));
            string relative = Path.GetRelativePath(libraryRoot, mediaPath);
            if (Path.IsPathRooted(relative)
                || relative == ".."
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", GetPathComparison())
                || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", GetPathComparison()))
            {
                throw new InvalidOperationException();
            }
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            issues.Add(new BroadcastPlanIssue(
                BroadcastPlanIssueKind.InvalidPath,
                playlistPath,
                item.Sequence,
                "relativePath escapes or is invalid for the library root."));
            return;
        }

        if (!string.Equals(Path.GetExtension(mediaPath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new BroadcastPlanIssue(
                BroadcastPlanIssueKind.UnreadyAsset,
                playlistPath,
                item.Sequence,
                "Referenced media is not a normalized MP4 asset."));
            return;
        }

        if (!File.Exists(mediaPath))
        {
            issues.Add(new BroadcastPlanIssue(
                BroadcastPlanIssueKind.MissingFile,
                playlistPath,
                item.Sequence,
                $"Referenced media file is missing: {item.RelativePath}"));
            return;
        }

        if (!StaysWithinResolvedLibraryRoot(libraryRoot, mediaPath))
        {
            issues.Add(new BroadcastPlanIssue(
                BroadcastPlanIssueKind.InvalidPath,
                playlistPath,
                item.Sequence,
                "relativePath resolves through a filesystem link outside the library root."));
            return;
        }

        if (!File.Exists(SourceManifestStore.GetManifestPath(mediaPath)))
        {
            issues.Add(new BroadcastPlanIssue(
                BroadcastPlanIssueKind.UnreadyAsset,
                playlistPath,
                item.Sequence,
                $"Technical normalization manifest is missing: {item.RelativePath}"));
            return;
        }

        resolvedItems.Add(new BroadcastPlanItem(
            playlistPath,
            item.Sequence,
            item.AssetId ?? string.Empty,
            item.RelativePath,
            mediaPath,
            item.DurationSeconds,
            item.Title ?? string.Empty,
            item.Type ?? string.Empty));
    }

    private static bool StaysWithinResolvedLibraryRoot(string libraryRoot, string mediaPath)
    {
        try
        {
            string resolvedRoot = new DirectoryInfo(libraryRoot).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? libraryRoot;
            string relative = Path.GetRelativePath(libraryRoot, mediaPath);
            string current = resolvedRoot;
            foreach (string segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                FileSystemInfo info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }

            string resolvedRelative = Path.GetRelativePath(resolvedRoot, current);
            return !Path.IsPathRooted(resolvedRelative)
                && resolvedRelative != ".."
                && !resolvedRelative.StartsWith($"..{Path.DirectorySeparatorChar}", GetPathComparison())
                && !resolvedRelative.StartsWith($"..{Path.AltDirectorySeparatorChar}", GetPathComparison());
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed class BroadcastPlaylistInput
    {
        public int SchemaVersion { get; init; }

        public List<BroadcastPlaylistItemInput>? Items { get; init; }
    }

    private sealed class BroadcastPlaylistItemInput
    {
        public int Sequence { get; init; }

        public string? AssetId { get; init; }

        public string? RelativePath { get; init; }

        public double DurationSeconds { get; init; }

        public string? Title { get; init; }

        public string? Type { get; init; }
    }
}
