using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed class MetadataReviewer
{
    private readonly ISongCatalogStore _catalogStore;
    private readonly IMetadataAssetDiscovery _discovery;
    private readonly IAssetMetadataStore _metadataStore;
    private readonly MetadataSynchronizer _synchronizer;
    private readonly IMetadataReviewPrompt _prompt;

    public MetadataReviewer(
        ISongCatalogStore catalogStore,
        IMetadataAssetDiscovery discovery,
        IAssetMetadataStore metadataStore,
        MetadataSynchronizer synchronizer,
        IMetadataReviewPrompt prompt)
    {
        _catalogStore = catalogStore;
        _discovery = discovery;
        _metadataStore = metadataStore;
        _synchronizer = synchronizer;
        _prompt = prompt;
    }

    public async Task<MetadataReviewResult> ReviewAsync(
        string sourceRoot,
        string libraryRoot,
        string catalogPath,
        CancellationToken cancellationToken)
    {
        SongCatalog catalog = _catalogStore.Load(catalogPath);
        IReadOnlyList<LibraryMediaFile> files = _discovery.Discover(sourceRoot, libraryRoot);
        IReadOnlySet<string> collidingDestinations = MetadataSynchronizer.FindCollidingDestinationPaths(files);
        var results = new List<MetadataReviewFileResult>();
        foreach (LibraryMediaFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(AssetMetadataStore.GetMetadataPath(file.SourcePath)))
            {
                continue;
            }

            AssetMetadata metadata = _metadataStore.Read(file.SourcePath);
            if (!AssetTypes.IsSongBased(metadata.Type)
                || (!string.IsNullOrWhiteSpace(metadata.ContentGroupId)
                    && catalog.FindByContentGroupId(metadata.ContentGroupId) is not null))
            {
                continue;
            }

            SongMatchResult match = SongMatcher.Match(file.SourcePath, catalog);
            IReadOnlyList<SongCatalogEntry> candidates = match.Candidates.Count > 0
                ? match.Candidates
                : catalog.Songs!.OrderBy(song => song.Title, StringComparer.OrdinalIgnoreCase).ToArray();
            string reason = !string.IsNullOrWhiteSpace(metadata.ContentGroupId)
                ? $"Referenced contentGroupId '{metadata.ContentGroupId}' is not in the catalog."
                : match.Reason;
            var request = new MetadataReviewRequest(
                file.SourcePath,
                metadata,
                match.DetectedTitle,
                candidates,
                reason);
            string? selection = await _prompt.SelectContentGroupAsync(request, cancellationToken).ConfigureAwait(false);
            if (selection is null)
            {
                results.Add(new MetadataReviewFileResult(
                    file.SourcePath,
                    metadata.AssetId!,
                    metadata.ContentGroupId,
                    Resolved: false,
                    LibraryMetadataSynchronized: false,
                    "Left unresolved by user."));
                continue;
            }

            SongCatalogEntry selected = candidates.FirstOrDefault(song =>
                string.Equals(song.ContentGroupId, selection, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Review selection '{selection}' was not one of the offered catalog entries.");
            AssetMetadata resolved = metadata.WithContentGroup(selected);
            await _metadataStore.WriteAsync(file.SourcePath, resolved, cancellationToken).ConfigureAwait(false);
            bool destinationCollision = collidingDestinations.Contains(file.DestinationPath);
            bool synchronized = !destinationCollision
                && await _synchronizer.SynchronizeFileIfPresentAsync(
                    file,
                    resolved,
                    dryRun: false,
                    cancellationToken).ConfigureAwait(false);
            results.Add(new MetadataReviewFileResult(
                file.SourcePath,
                resolved.AssetId!,
                resolved.ContentGroupId,
                Resolved: true,
                synchronized,
                destinationCollision
                    ? "Explicit catalog relationship saved; assetId and user-entered metadata were preserved. Multiple sources map to the same library asset, so library synchronization was skipped."
                    : "Explicit catalog relationship saved; assetId and user-entered metadata were preserved."));
        }

        return new MetadataReviewResult(results);
    }
}
