using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record PlaylistLibrarySnapshot(
    IReadOnlyList<PlaylistAsset> EligibleAssets,
    IReadOnlyList<PlaylistExclusion> ExcludedAssets)
{
    public IReadOnlySet<string> KnownAssetIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
}

public interface IPlaylistLibraryLoader
{
    Task<PlaylistLibrarySnapshot> LoadAsync(
        string libraryRoot,
        SongCatalog catalog,
        CancellationToken cancellationToken);

    Task<PlaylistLibrarySnapshot> LoadAsync(
        string libraryRoot,
        SongCatalog catalog,
        IAssetMetadataSnapshot metadataSnapshot,
        CancellationToken cancellationToken);
}

public sealed class PlaylistLibraryLoader(
    IMediaAnalyzer mediaAnalyzer,
    IAssetMetadataStore? metadataStore = null,
    IAssetMetadataRepository? metadataRepository = null) : IPlaylistLibraryLoader
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".mkv",
    };

    private readonly IAssetMetadataRepository _metadataRepository = metadataRepository
        ?? new AdjacentAssetMetadataRepository(metadataStore);

    public async Task<PlaylistLibrarySnapshot> LoadAsync(
        string libraryRoot,
        SongCatalog catalog,
        CancellationToken cancellationToken)
    {
        IAssetMetadataSnapshot metadataSnapshot = _metadataRepository.Pin(
            AssetMetadataTree.Library,
            libraryRoot);
        return await LoadAsync(libraryRoot, catalog, metadataSnapshot, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PlaylistLibrarySnapshot> LoadAsync(
        string libraryRoot,
        SongCatalog catalog,
        IAssetMetadataSnapshot metadataSnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        SongCatalogValidator.Validate(catalog);
        ArgumentNullException.ThrowIfNull(metadataSnapshot);
        string root = Path.GetFullPath(libraryRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Normalized library root not found: {root}");
        }

        string[] candidates = DiscoverCandidateMediaPaths(
            root,
            metadataSnapshot.DiscoverRelativeMediaPaths(),
            includeAdjacentMetadataCandidates:
                metadataSnapshot.Identity.Mode == AssetMetadataStorageMode.Adjacent);
        var eligible = new List<PlaylistAsset>();
        var excluded = new List<PlaylistExclusion>();
        var knownAssetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string mediaPath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = ToRelativePath(root, mediaPath);
            bool mediaExists = File.Exists(mediaPath);
            bool metadataExists = metadataSnapshot.Exists(relativePath);
            bool technicalManifestExists = File.Exists(SourceManifestStore.GetManifestPath(mediaPath));
            AssetMetadata? metadata = null;
            if (metadataExists)
            {
                try
                {
                    metadata = metadataSnapshot.Read(relativePath).Metadata;
                }
                catch (Exception exception) when (exception is
                    InvalidDataException or AssetMetadataValidationException or IOException or UnauthorizedAccessException)
                {
                    excluded.Add(new PlaylistExclusion(
                        relativePath,
                        [$"Programming metadata is invalid: {exception.Message}"]));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(metadata.AssetId))
                {
                    knownAssetIds.Add(metadata.AssetId);
                }
            }

            AssetEligibilityResult eligibility = AssetEligibilityEvaluator.Evaluate(
                metadata,
                catalog,
                mediaExists,
                technicalManifestExists,
                metadataExists);
            if (!eligibility.IsPlaylistEligible)
            {
                excluded.Add(new PlaylistExclusion(relativePath, eligibility.Reasons));
                continue;
            }

            try
            {
                MediaDescription inspected = await mediaAnalyzer.InspectAsync(mediaPath, cancellationToken)
                    .ConfigureAwait(false);
                if (inspected.Duration is not TimeSpan duration || duration <= TimeSpan.Zero)
                {
                    excluded.Add(new PlaylistExclusion(
                        relativePath,
                        ["Media duration could not be determined."]));
                    continue;
                }

                eligible.Add(new PlaylistAsset(
                    metadata!.AssetId!,
                    metadata.ContentGroupId,
                    metadata.Title!,
                    metadata.Artist,
                    metadata.Type!,
                    metadata.Subtype,
                    relativePath,
                    duration.TotalSeconds,
                    metadata.RotationStartDate));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                FileNotFoundException or IOException or UnauthorizedAccessException or MediaProbeException or
                ProcessExecutionException or FfprobeDataException)
            {
                excluded.Add(new PlaylistExclusion(
                    relativePath,
                    [$"Media duration could not be determined: {exception.Message}"]));
            }
        }

        foreach (IGrouping<string, PlaylistAsset> duplicate in eligible
            .GroupBy(asset => asset.AssetId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToArray())
        {
            foreach (PlaylistAsset asset in duplicate)
            {
                excluded.Add(new PlaylistExclusion(
                    asset.RelativePath,
                    [$"Duplicate eligible assetId '{duplicate.Key}'."]));
                eligible.Remove(asset);
            }
        }

        return new PlaylistLibrarySnapshot(
            eligible.OrderBy(asset => asset.RelativePath, StringComparer.Ordinal).ToArray(),
            excluded.OrderBy(asset => asset.RelativePath, StringComparer.Ordinal).ToArray())
        {
            KnownAssetIds = knownAssetIds,
        };
    }

    internal static string[] DiscoverCandidateMediaPaths(
        string root,
        IReadOnlyList<string>? metadataRelativeMediaPaths = null,
        bool includeAdjacentMetadataCandidates = true)
    {
        var candidates = new HashSet<string>(GetPathComparer());
        var pending = new Stack<string>();
        pending.Push(root);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        while (pending.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*", options))
            {
                string fileName = Path.GetFileName(path);
                string? mediaPath = null;
                if (SupportedExtensions.Contains(Path.GetExtension(path)))
                {
                    mediaPath = path;
                }
                else if (includeAdjacentMetadataCandidates
                    && fileName.EndsWith(AssetMetadataStore.MetadataSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    mediaPath = path[..^AssetMetadataStore.MetadataSuffix.Length];
                }
                else if (fileName.EndsWith(SourceManifestStore.ManifestSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    mediaPath = path[..^SourceManifestStore.ManifestSuffix.Length];
                }

                if (mediaPath is not null && SupportedExtensions.Contains(Path.GetExtension(mediaPath)))
                {
                    candidates.Add(Path.GetFullPath(mediaPath));
                }
            }

            foreach (string child in Directory.EnumerateDirectories(directory, "*", options)
                .Where(path => !string.Equals(
                    Path.GetFileName(path),
                    "System Volume Information",
                    StringComparison.OrdinalIgnoreCase)))
            {
                pending.Push(child);
            }
        }

        if (metadataRelativeMediaPaths is not null)
        {
            foreach (string relativePath in metadataRelativeMediaPaths)
            {
                string mediaPath = MetadataPathSafety.ResolveRelativeMediaPath(root, relativePath);
                if (SupportedExtensions.Contains(Path.GetExtension(mediaPath)))
                {
                    candidates.Add(mediaPath);
                }
            }
        }

        return candidates.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ToRelativePath(string root, string mediaPath) =>
        Path.GetRelativePath(root, mediaPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
