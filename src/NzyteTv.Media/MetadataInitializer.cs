using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed class MetadataInitializer
{
    private readonly ISongCatalogStore _catalogStore;
    private readonly IMetadataAssetDiscovery _discovery;
    private readonly IAssetMetadataStore _metadataStore;
    private readonly MetadataSynchronizer _synchronizer;

    public MetadataInitializer(
        ISongCatalogStore catalogStore,
        IMetadataAssetDiscovery discovery,
        IAssetMetadataStore metadataStore,
        MetadataSynchronizer synchronizer)
    {
        _catalogStore = catalogStore;
        _discovery = discovery;
        _metadataStore = metadataStore;
        _synchronizer = synchronizer;
    }

    public async Task<MetadataInitializationResult> InitializeAsync(
        string sourceRoot,
        string libraryRoot,
        string catalogPath,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(sourceRoot);
        SongCatalog catalog = _catalogStore.Load(catalogPath);
        IReadOnlyList<LibraryMediaFile> files = _discovery.Discover(source, libraryRoot);
        IReadOnlySet<string> collidingDestinations = MetadataSynchronizer.FindCollidingDestinationPaths(files);
        IReadOnlyList<string> orphans = _discovery.DiscoverOrphanedMetadata(
            source,
            files.Select(file => file.SourcePath).ToArray());
        var existing = LoadExistingMetadata(files);
        IReadOnlyDictionary<string, LoadedMetadata> orphanMetadata = LoadOrphanMetadata(orphans);
        var allMetadata = existing
            .Concat(orphanMetadata)
            .ToDictionary(item => item.Key, item => item.Value, GetPathComparer());
        HashSet<string> duplicateMetadataPaths = FindDuplicateAssetIds(allMetadata);
        var reservedIds = new HashSet<string>(
            allMetadata.Values
                .Where(value => value.Metadata?.AssetId is not null)
                .Select(value => value.Metadata!.AssetId!),
            StringComparer.Ordinal);

        var results = new List<MetadataAssetResult>();

        foreach (LibraryMediaFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string metadataPath = AssetMetadataStore.GetMetadataPath(file.SourcePath);
            if (duplicateMetadataPaths.Contains(metadataPath))
            {
                AssetMetadata? duplicate = existing[metadataPath].Metadata;
                results.Add(Error(file, duplicate, $"Duplicate assetId '{duplicate?.AssetId}' exists in source metadata."));
                continue;
            }

            LoadedMetadata loaded = existing[metadataPath];
            if (loaded.Error is not null)
            {
                results.Add(Error(file, null, loaded.Error));
                continue;
            }

            try
            {
                MetadataAssetResult result;
                if (loaded.Metadata is not null)
                {
                    result = await ProcessExistingAsync(
                        file,
                        loaded.Metadata,
                        catalog,
                        collidingDestinations.Contains(file.DestinationPath),
                        dryRun,
                        cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (AssetCategoryMap.TryDetect(source, file.SourcePath, out string? detectedType))
                {
                    ShortFormDescriptor? shortFormDescriptor = ShortFormDescriptorDetector.Detect(file.SourcePath);
                    string effectiveType = shortFormDescriptor is not null
                        && CanInferShortFormFromFileName(detectedType!)
                            ? AssetTypes.ShortForm
                            : detectedType!;
                    result = await CreateAsync(
                        file,
                        source,
                        effectiveType,
                        effectiveType == AssetTypes.ShortForm ? shortFormDescriptor : null,
                        catalog,
                        reservedIds,
                        collidingDestinations.Contains(file.DestinationPath),
                        dryRun,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = Error(
                        file,
                        null,
                        "The source directory does not map to a supported programming type.");
                }

                results.Add(result);
            }
            catch (Exception exception) when (exception is
                InvalidDataException or AssetMetadataValidationException or IOException or UnauthorizedAccessException)
            {
                results.Add(Error(file, loaded.Metadata, exception.Message));
            }
        }

        foreach (string orphanPath in orphans)
        {
            string missingSourcePath = orphanPath[..^AssetMetadataStore.MetadataSuffix.Length];
            LoadedMetadata loaded = orphanMetadata[orphanPath];
            if (loaded.Error is not null)
            {
                results.Add(new MetadataAssetResult(
                    missingSourcePath,
                    null,
                    null,
                    null,
                    null,
                    MetadataInitializationStatus.Error,
                    $"Orphaned metadata is invalid: {loaded.Error}"));
                continue;
            }

            AssetMetadata metadata = loaded.Metadata!;
            if (duplicateMetadataPaths.Contains(orphanPath))
            {
                results.Add(new MetadataAssetResult(
                    missingSourcePath,
                    null,
                    metadata.Type,
                    metadata.AssetId,
                    metadata.ContentGroupId,
                    MetadataInitializationStatus.Error,
                    $"Duplicate assetId '{metadata.AssetId}' exists in source metadata."));
                continue;
            }

            results.Add(new MetadataAssetResult(
                missingSourcePath,
                null,
                metadata.Type,
                metadata.AssetId,
                metadata.ContentGroupId,
                MetadataInitializationStatus.Orphaned,
                "Programming metadata exists, but its source media file is missing. Nothing was deleted.",
                Subtype: metadata.Subtype));
        }

        return new MetadataInitializationResult(results, dryRun);
    }

    private async Task<MetadataAssetResult> CreateAsync(
        LibraryMediaFile file,
        string sourceRoot,
        string type,
        ShortFormDescriptor? shortFormDescriptor,
        SongCatalog catalog,
        ISet<string> reservedIds,
        bool destinationCollision,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        SongMatchResult? match = AssetTypes.IsSongBased(type)
            ? SongMatcher.Match(file.SourcePath, catalog)
            : null;
        SongCatalogEntry? song = match?.Status == SongMatchStatus.Matched ? match.Match : null;
        string relativePath = Path.GetRelativePath(sourceRoot, file.SourcePath);
        string assetId = AssetIdGenerator.Generate(
            file.SourcePath,
            relativePath,
            type,
            song,
            reservedIds,
            shortFormDescriptor);
        var metadata = new AssetMetadata
        {
            AssetId = assetId,
            ContentGroupId = song?.ContentGroupId,
            Title = song?.Title ?? Path.GetFileNameWithoutExtension(file.SourcePath),
            Artist = song?.Artist,
            Type = type,
            Subtype = shortFormDescriptor?.Subtype,
            RotationStartDate = null,
            Enabled = true,
            SeriesId = null,
            EpisodeNumber = null,
            Tags = [],
        };

        if (!dryRun)
        {
            await _metadataStore.WriteAsync(file.SourcePath, metadata, cancellationToken).ConfigureAwait(false);
        }

        bool synchronized = !destinationCollision
            && await _synchronizer.SynchronizeFileIfPresentAsync(
                file,
                metadata,
                dryRun,
                cancellationToken).ConfigureAwait(false);
        MetadataInitializationStatus status = match?.Status switch
        {
            SongMatchStatus.Matched => MetadataInitializationStatus.Resolved,
            SongMatchStatus.Ambiguous => MetadataInitializationStatus.ReviewRequired,
            SongMatchStatus.Unresolved => MetadataInitializationStatus.Unresolved,
            _ => MetadataInitializationStatus.NonSong,
        };
        string reason = AddDestinationCollisionReason(
            match?.Reason ?? "Non-song programming does not require contentGroupId.",
            destinationCollision);
        return CreateResult(
            file,
            metadata,
            catalog,
            status,
            reason,
            metadataCreated: true,
            existingMetadataPreserved: false,
            synchronized,
            match?.Candidates,
            dryRun);
    }

    private async Task<MetadataAssetResult> ProcessExistingAsync(
        LibraryMediaFile file,
        AssetMetadata metadata,
        SongCatalog catalog,
        bool destinationCollision,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        MetadataInitializationStatus status;
        string reason;
        IReadOnlyList<SongCatalogEntry>? candidates = null;
        AssetMetadata effectiveMetadata = metadata;

        if (!AssetTypes.IsSongBased(metadata.Type))
        {
            status = MetadataInitializationStatus.Preserved;
            reason = "Existing non-song metadata is authoritative and was preserved.";
        }
        else if (!string.IsNullOrWhiteSpace(metadata.ContentGroupId)
            && catalog.FindByContentGroupId(metadata.ContentGroupId) is SongCatalogEntry resolvedSong)
        {
            effectiveMetadata = metadata.WithContentGroup(resolvedSong);
            if (!dryRun)
            {
                await _metadataStore.WriteAsync(file.SourcePath, effectiveMetadata, cancellationToken)
                    .ConfigureAwait(false);
            }

            status = MetadataInitializationStatus.Preserved;
            reason = "Existing resolved relationship and asset identity were preserved; title and artist use the canonical catalog values.";
        }
        else if (!string.IsNullOrWhiteSpace(metadata.ContentGroupId))
        {
            SongMatchResult match = SongMatcher.Match(file.SourcePath, catalog);
            status = MetadataInitializationStatus.ReviewRequired;
            reason = $"Existing contentGroupId '{metadata.ContentGroupId}' is not present in the catalog; manual review is required.";
            candidates = match.Candidates.Count > 0 ? match.Candidates : catalog.Songs!;
        }
        else
        {
            SongMatchResult match = SongMatcher.Match(file.SourcePath, catalog);
            candidates = match.Candidates;
            if (match.Status == SongMatchStatus.Matched)
            {
                effectiveMetadata = metadata.WithContentGroup(match.Match!);
                if (!dryRun)
                {
                    await _metadataStore.WriteAsync(file.SourcePath, effectiveMetadata, cancellationToken)
                        .ConfigureAwait(false);
                }

                status = MetadataInitializationStatus.Resolved;
                reason = "Previously unresolved metadata now has one unambiguous catalog match; asset identity and non-catalog fields were preserved, and title and artist use the canonical catalog values.";
            }
            else
            {
                status = match.Status == SongMatchStatus.Ambiguous
                    ? MetadataInitializationStatus.ReviewRequired
                    : MetadataInitializationStatus.Unresolved;
                reason = match.Reason;
            }
        }

        bool synchronized = !destinationCollision
            && await _synchronizer.SynchronizeFileIfPresentAsync(
                file,
                effectiveMetadata,
                dryRun,
                cancellationToken).ConfigureAwait(false);
        return CreateResult(
            file,
            effectiveMetadata,
            catalog,
            status,
            AddDestinationCollisionReason(reason, destinationCollision),
            metadataCreated: false,
            existingMetadataPreserved: true,
            synchronized,
            candidates,
            dryRun);
    }

    private static string AddDestinationCollisionReason(string reason, bool destinationCollision) =>
        destinationCollision
            ? $"{reason} Multiple source files map to the same library asset; library metadata synchronization was skipped."
            : reason;

    private static MetadataAssetResult CreateResult(
        LibraryMediaFile file,
        AssetMetadata metadata,
        SongCatalog catalog,
        MetadataInitializationStatus status,
        string reason,
        bool metadataCreated,
        bool existingMetadataPreserved,
        bool synchronized,
        IReadOnlyList<SongCatalogEntry>? candidates,
        bool dryRun)
    {
        bool libraryMetadataExists = File.Exists(AssetMetadataStore.GetMetadataPath(file.DestinationPath));
        AssetEligibilityResult eligibility = AssetEligibilityEvaluator.Evaluate(
            metadata,
            catalog,
            File.Exists(file.DestinationPath),
            File.Exists(SourceManifestStore.GetManifestPath(file.DestinationPath)),
            libraryMetadataExists || (!dryRun && synchronized));
        return new MetadataAssetResult(
            file.SourcePath,
            file.DestinationPath,
            metadata.Type,
            metadata.AssetId,
            metadata.ContentGroupId,
            status,
            reason,
            metadataCreated,
            existingMetadataPreserved,
            synchronized,
            eligibility,
            candidates,
            metadata.Subtype);
    }

    private static MetadataAssetResult Error(LibraryMediaFile file, AssetMetadata? metadata, string reason) => new(
        file.SourcePath,
        file.DestinationPath,
        metadata?.Type,
        metadata?.AssetId,
        metadata?.ContentGroupId,
        MetadataInitializationStatus.Error,
        reason,
        Subtype: metadata?.Subtype);

    private static bool CanInferShortFormFromFileName(string detectedType) =>
        detectedType == AssetTypes.Vlog || AssetTypes.IsSongBased(detectedType);

    private Dictionary<string, LoadedMetadata> LoadExistingMetadata(IReadOnlyList<LibraryMediaFile> files)
    {
        var loaded = new Dictionary<string, LoadedMetadata>(GetPathComparer());
        foreach (LibraryMediaFile file in files)
        {
            string metadataPath = AssetMetadataStore.GetMetadataPath(file.SourcePath);
            if (!File.Exists(metadataPath))
            {
                loaded[metadataPath] = new LoadedMetadata(null, null);
                continue;
            }

            try
            {
                loaded[metadataPath] = new LoadedMetadata(_metadataStore.Read(file.SourcePath), null);
            }
            catch (Exception exception) when (exception is
                InvalidDataException or AssetMetadataValidationException or IOException or UnauthorizedAccessException)
            {
                loaded[metadataPath] = new LoadedMetadata(null, exception.Message);
            }
        }

        return loaded;
    }

    private Dictionary<string, LoadedMetadata> LoadOrphanMetadata(IReadOnlyList<string> orphanPaths)
    {
        var loaded = new Dictionary<string, LoadedMetadata>(GetPathComparer());
        foreach (string metadataPath in orphanPaths)
        {
            string missingSourcePath = metadataPath[..^AssetMetadataStore.MetadataSuffix.Length];
            try
            {
                loaded[metadataPath] = new LoadedMetadata(_metadataStore.Read(missingSourcePath), null);
            }
            catch (Exception exception) when (exception is
                InvalidDataException or AssetMetadataValidationException or IOException or UnauthorizedAccessException)
            {
                loaded[metadataPath] = new LoadedMetadata(null, exception.Message);
            }
        }

        return loaded;
    }

    private static HashSet<string> FindDuplicateAssetIds(IReadOnlyDictionary<string, LoadedMetadata> existing)
    {
        var duplicates = new HashSet<string>(GetPathComparer());
        foreach (IGrouping<string, KeyValuePair<string, LoadedMetadata>> group in existing
            .Where(item => item.Value.Metadata?.AssetId is not null)
            .GroupBy(item => item.Value.Metadata!.AssetId!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            foreach (KeyValuePair<string, LoadedMetadata> item in group)
            {
                duplicates.Add(item.Key);
            }
        }

        return duplicates;
    }

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record LoadedMetadata(AssetMetadata? Metadata, string? Error);
}
