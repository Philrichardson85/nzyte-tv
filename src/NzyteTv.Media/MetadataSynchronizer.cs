using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed class MetadataSynchronizer
{
    private readonly IMetadataAssetDiscovery _discovery;
    private readonly IAssetMetadataStore _metadataStore;

    public MetadataSynchronizer(IMetadataAssetDiscovery discovery, IAssetMetadataStore metadataStore)
    {
        _discovery = discovery;
        _metadataStore = metadataStore;
    }

    public async Task<MetadataSyncResult> SynchronizeAsync(
        string sourceRoot,
        string libraryRoot,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LibraryMediaFile> files = _discovery.Discover(sourceRoot, libraryRoot);
        IReadOnlySet<string> collidingDestinations = FindCollidingDestinationPaths(files);
        var results = new List<MetadataSyncFileResult>(files.Count);
        foreach (LibraryMediaFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (collidingDestinations.Contains(file.DestinationPath))
            {
                results.Add(new MetadataSyncFileResult(
                    file.SourcePath,
                    file.DestinationPath,
                    MetadataSyncStatus.Error,
                    "Multiple source files map to this library asset; programming metadata was not synchronized."));
                continue;
            }

            if (!File.Exists(AssetMetadataStore.GetMetadataPath(file.SourcePath)))
            {
                results.Add(new MetadataSyncFileResult(
                    file.SourcePath,
                    file.DestinationPath,
                    MetadataSyncStatus.MissingSourceMetadata,
                    "Source programming metadata is missing."));
                continue;
            }

            if (!File.Exists(file.DestinationPath))
            {
                results.Add(new MetadataSyncFileResult(
                    file.SourcePath,
                    file.DestinationPath,
                    MetadataSyncStatus.MissingLibraryAsset,
                    "Normalized library asset is missing; nothing was deleted or encoded."));
                continue;
            }

            try
            {
                AssetMetadata metadata = _metadataStore.Read(file.SourcePath);
                bool changed = !dryRun
                    && await _metadataStore.WriteAsync(file.DestinationPath, metadata, cancellationToken).ConfigureAwait(false);
                results.Add(new MetadataSyncFileResult(
                    file.SourcePath,
                    file.DestinationPath,
                    dryRun || changed ? MetadataSyncStatus.Synchronized : MetadataSyncStatus.Unchanged,
                    dryRun ? "Would synchronize programming metadata." : changed
                        ? "Programming metadata synchronized."
                        : "Library programming metadata is already current."));
            }
            catch (Exception exception) when (exception is
                InvalidDataException or AssetMetadataValidationException or IOException or UnauthorizedAccessException)
            {
                results.Add(new MetadataSyncFileResult(
                    file.SourcePath,
                    file.DestinationPath,
                    MetadataSyncStatus.Error,
                    exception.Message));
            }
        }

        return new MetadataSyncResult(results);
    }

    public static IReadOnlySet<string> FindCollidingDestinationPaths(
        IReadOnlyCollection<LibraryMediaFile> files)
    {
        StringComparer pathComparer = GetPathComparer();
        return files
            .GroupBy(file => file.DestinationPath, pathComparer)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(pathComparer);
    }

    public async Task<bool> SynchronizeFileIfPresentAsync(
        LibraryMediaFile file,
        AssetMetadata metadata,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(file.DestinationPath))
        {
            return false;
        }

        if (!dryRun)
        {
            await _metadataStore.WriteAsync(file.DestinationPath, metadata, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
