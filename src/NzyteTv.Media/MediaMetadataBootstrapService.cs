using NzyteTv.Core;

namespace NzyteTv.Media;

public enum MediaMetadataBootstrapStatus
{
    PreviewReady,
    Published,
    Blocked,
}

public sealed record MediaMetadataBootstrapIssue(
    MediaLibraryRefreshIssueCode Code,
    string? RelativeIdentity = null);

public sealed record MediaMetadataBootstrapResult(
    MediaMetadataBootstrapStatus Status,
    int SourceRecords,
    int LibraryRecords,
    int Assets,
    IReadOnlyList<MediaMetadataBootstrapIssue> Issues,
    string? GenerationId = null,
    long? Revision = null);

public sealed record MediaMetadataBootstrapOptions(
    string MediaRoot,
    string ExternalMetadataRoot)
{
    public MediaMetadataBootstrapOptions Validate()
    {
        if (!Path.IsPathFullyQualified(MediaRoot)
            || !Path.IsPathFullyQualified(ExternalMetadataRoot))
        {
            throw new InvalidOperationException("Metadata bootstrap roots must be absolute trusted paths.");
        }

        string mediaRoot = Path.GetFullPath(MediaRoot);
        string externalRoot = Path.GetFullPath(ExternalMetadataRoot);
        _ = new MediaPackageStorageOptions(mediaRoot).Validate(requireInbox: false);
        Directory.CreateDirectory(externalRoot);
        MetadataPathSafety.EnsureNoReparsePoint(externalRoot, externalRoot);
        return this with
        {
            MediaRoot = mediaRoot,
            ExternalMetadataRoot = externalRoot,
        };
    }
}

public sealed class MediaMetadataBootstrapService
{
    private readonly MediaMetadataBootstrapOptions _options;
    private readonly ExternalAssetMetadataGenerationStore _generationStore;
    private readonly IMediaMetadataRefreshLock _refreshLock;
    private readonly IPackageFileHasher _hasher;
    private readonly ISourceManifestStore _sourceManifestStore;

    public MediaMetadataBootstrapService(
        MediaMetadataBootstrapOptions options,
        ExternalAssetMetadataGenerationStore? generationStore = null,
        IMediaMetadataRefreshLock? refreshLock = null,
        IPackageFileHasher? hasher = null,
        ISourceManifestStore? sourceManifestStore = null)
    {
        _options = options.Validate();
        _generationStore = generationStore
            ?? new ExternalAssetMetadataGenerationStore(_options.ExternalMetadataRoot);
        _refreshLock = refreshLock ?? new MediaMetadataRefreshLock();
        _hasher = hasher ?? new PackageFileHasher();
        _sourceManifestStore = sourceManifestStore ?? new SourceManifestStore();
    }

    public Task<MediaMetadataBootstrapResult> PreviewAsync(CancellationToken cancellationToken) =>
        RunAsync(publish: false, cancellationToken);

    public Task<MediaMetadataBootstrapResult> PublishAsync(CancellationToken cancellationToken) =>
        RunAsync(publish: true, cancellationToken);

    private async Task<MediaMetadataBootstrapResult> RunAsync(
        bool publish,
        CancellationToken cancellationToken)
    {
        await using IMediaMetadataRefreshLease held = await _refreshLock.TryAcquireAsync(
            _options.ExternalMetadataRoot,
            cancellationToken).ConfigureAwait(false);
        held.Consume(_options.ExternalMetadataRoot);
        string currentPath = Path.Combine(
            _options.ExternalMetadataRoot,
            ExternalAssetMetadataGenerationStore.CurrentFileName);
        if (File.Exists(currentPath))
        {
            throw new InvalidOperationException(
                "External metadata bootstrap is allowed only before current.json exists.");
        }

        BootstrapCandidate candidate = await AnalyzeAsync(cancellationToken).ConfigureAwait(false);
        if (candidate.Issues.Count > 0)
        {
            return new MediaMetadataBootstrapResult(
                MediaMetadataBootstrapStatus.Blocked,
                candidate.SourceCount,
                candidate.LibraryCount,
                candidate.Inventory.Assets!.Count,
                candidate.Issues);
        }

        if (!publish)
        {
            return new MediaMetadataBootstrapResult(
                MediaMetadataBootstrapStatus.PreviewReady,
                candidate.SourceCount,
                candidate.LibraryCount,
                candidate.Inventory.Assets!.Count,
                [],
                "000000000001",
                1);
        }

        candidate.Catalog.VerifyUnchanged(ProgrammingPaths.FromMediaRoot(_options.MediaRoot).CatalogPath);
        await _generationStore.CreateGenerationAsync(
            "000000000001",
            1,
            candidate.Records,
            candidate.Inventory,
            cancellationToken).ConfigureAwait(false);
        _ = _generationStore.ReadInventory(new AssetMetadataSnapshotIdentity(
            AssetMetadataStorageMode.ExternalGeneration,
            1,
            "000000000001"));
        candidate.Catalog.VerifyUnchanged(ProgrammingPaths.FromMediaRoot(_options.MediaRoot).CatalogPath);
        _ = await _generationStore.BootstrapCurrentAsync(
            "000000000001",
            cancellationToken).ConfigureAwait(false);
        return new MediaMetadataBootstrapResult(
            MediaMetadataBootstrapStatus.Published,
            candidate.SourceCount,
            candidate.LibraryCount,
            candidate.Inventory.Assets!.Count,
            [],
            "000000000001",
            1);
    }

    private async Task<BootstrapCandidate> AnalyzeAsync(CancellationToken cancellationToken)
    {
        MediaPackageStorageOptions media = new MediaPackageStorageOptions(_options.MediaRoot)
            .Validate(requireInbox: false);
        ProgrammingPaths programming = ProgrammingPaths.FromMediaRoot(_options.MediaRoot);
        StableSongCatalogSnapshot catalog = StableSongCatalogSnapshot.Load(programming.CatalogPath);
        IAssetMetadataSnapshot source = new AdjacentAssetMetadataRepository().Pin(
            AssetMetadataTree.Source,
            media.SourceRoot);
        IAssetMetadataSnapshot library = new AdjacentAssetMetadataRepository().Pin(
            AssetMetadataTree.Library,
            media.LibraryRoot);
        IReadOnlyList<string> sourcePaths = source.DiscoverRelativeMediaPaths();
        IReadOnlyList<string> libraryPaths = library.DiscoverRelativeMediaPaths();
        var librarySet = libraryPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pairedLibrary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<MediaMetadataBootstrapIssue>();
        var records = new List<AssetMetadataGenerationRecord>();
        var assets = new List<AssetMetadataInventoryAsset>();
        var assetIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (string sourceRelative in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourceMediaPath = MetadataPathSafety.ResolveRelativeMediaPath(
                media.SourceRoot,
                sourceRelative);
            string expectedLibrary = Path.GetRelativePath(
                    media.LibraryRoot,
                    LibraryPathPolicy.GetDestinationPath(
                        media.SourceRoot,
                        media.LibraryRoot,
                        sourceMediaPath))
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            if (!librarySet.Contains(expectedLibrary))
            {
                issues.Add(new MediaMetadataBootstrapIssue(
                    MediaLibraryRefreshIssueCode.BootstrapInconsistency,
                    sourceRelative));
                continue;
            }

            pairedLibrary.Add(expectedLibrary);
            try
            {
                AssetMetadataDocument sourceDocument = source.Read(sourceRelative);
                AssetMetadataDocument libraryDocument = library.Read(expectedLibrary);
                ValidateCatalogRelationship(sourceDocument.Metadata, catalog.Catalog);
                ValidateCatalogRelationship(libraryDocument.Metadata, catalog.Catalog);
                if (!string.Equals(
                        sourceDocument.Metadata.AssetId,
                        libraryDocument.Metadata.AssetId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        sourceDocument.Metadata.Type,
                        libraryDocument.Metadata.Type,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        sourceDocument.Metadata.ContentGroupId,
                        libraryDocument.Metadata.ContentGroupId,
                        StringComparison.Ordinal)
                    || !assetIds.Add(libraryDocument.Metadata.AssetId!))
                {
                    throw new InvalidDataException("Adjacent metadata identity is inconsistent.");
                }

                string sourcePath = MetadataPathSafety.ResolveRelativeMediaPath(media.SourceRoot, sourceRelative);
                string libraryPath = MetadataPathSafety.ResolveRelativeMediaPath(media.LibraryRoot, expectedLibrary);
                if (!File.Exists(sourcePath) || !File.Exists(libraryPath))
                {
                    throw new FileNotFoundException("An adjacent metadata record has no media member.");
                }

                string technicalPath = SourceManifestStore.GetManifestPath(libraryPath);
                MetadataPathSafety.EnsureNoReparsePoint(media.LibraryRoot, technicalPath);
                PackageFileHash technical = await _hasher.HashStableAsync(
                    technicalPath,
                    1024 * 1024,
                    cancellationToken).ConfigureAwait(false);
                SourceFingerprint expected = _sourceManifestStore.CreateFingerprint(media.SourceRoot, sourcePath);
                if (!_sourceManifestStore.Evaluate(libraryPath, expected).IsMatch)
                {
                    throw new InvalidDataException("The adjacent technical manifest is inconsistent.");
                }

                records.Add(new AssetMetadataGenerationRecord(
                    AssetMetadataTree.Source,
                    sourceRelative,
                    sourceDocument.Metadata,
                    sourceDocument.JsonBytes));
                records.Add(new AssetMetadataGenerationRecord(
                    AssetMetadataTree.Library,
                    expectedLibrary,
                    libraryDocument.Metadata,
                    libraryDocument.JsonBytes));
                assets.Add(new AssetMetadataInventoryAsset
                {
                    SourceRelativePath = sourceRelative,
                    LibraryRelativePath = expectedLibrary,
                    AssetId = libraryDocument.Metadata.AssetId,
                    ContentGroupId = libraryDocument.Metadata.ContentGroupId,
                    TechnicalManifestSha256 = technical.Sha256,
                    SourceMetadataSha256 = MediaPackageHash.Sha256(sourceDocument.JsonBytes),
                    LibraryMetadataSha256 = MediaPackageHash.Sha256(libraryDocument.JsonBytes),
                    Classification = libraryDocument.Metadata.Enabled
                        ? AssetMetadataInventoryClassification.PlaylistEligible
                        : AssetMetadataInventoryClassification.Disabled,
                });
            }
            catch (Exception exception) when (exception is
                FileNotFoundException or IOException or UnauthorizedAccessException or
                InvalidDataException or AssetMetadataValidationException)
            {
                issues.Add(new MediaMetadataBootstrapIssue(
                    MediaLibraryRefreshIssueCode.BootstrapInconsistency,
                    sourceRelative));
            }
        }

        foreach (string orphan in libraryPaths.Where(value => !pairedLibrary.Contains(value)))
        {
            issues.Add(new MediaMetadataBootstrapIssue(
                MediaLibraryRefreshIssueCode.BootstrapInconsistency,
                orphan));
        }

        var inventory = new AssetMetadataGenerationInventory
        {
            Revision = 1,
            GenerationId = "000000000001",
            Packages = [],
            Assets = assets,
        };
        if (issues.Count == 0)
        {
            inventory = AssetMetadataGenerationInventorySerializer.NormalizeAndValidate(inventory);
        }

        return new BootstrapCandidate(
            records,
            inventory,
            issues,
            sourcePaths.Count,
            libraryPaths.Count,
            catalog);
    }

    private static void ValidateCatalogRelationship(AssetMetadata metadata, SongCatalog catalog)
    {
        AssetMetadataValidator.ValidateStructure(metadata);
        if (!AssetMetadataValidator.HasValidSongRelationship(metadata, catalog))
        {
            throw new InvalidDataException("Adjacent song metadata is unresolved.");
        }
    }

    private sealed record BootstrapCandidate(
        IReadOnlyCollection<AssetMetadataGenerationRecord> Records,
        AssetMetadataGenerationInventory Inventory,
        IReadOnlyList<MediaMetadataBootstrapIssue> Issues,
        int SourceCount,
        int LibraryCount,
        StableSongCatalogSnapshot Catalog);
}
