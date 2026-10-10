using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record MediaLibraryRefreshOptions(
    string MediaRoot,
    string ExternalMetadataRoot,
    string? InboxRoot = null)
{
    public const int MaximumReadyManifests = 256;

    public MediaPackageStorageOptions PackageStorage => new(MediaRoot, InboxRoot);

    public string CatalogPath => ProgrammingPaths.FromMediaRoot(MediaRoot).CatalogPath;

    public MediaLibraryRefreshOptions Validate()
    {
        if (!Path.IsPathFullyQualified(MediaRoot)
            || !Path.IsPathFullyQualified(ExternalMetadataRoot)
            || (InboxRoot is not null && !Path.IsPathFullyQualified(InboxRoot)))
        {
            throw new InvalidOperationException("Media refresh roots must be absolute trusted paths.");
        }

        MediaPackageStorageOptions package = PackageStorage.Validate(requireInbox: true);
        string external = Path.GetFullPath(ExternalMetadataRoot);
        if (!Directory.Exists(external))
        {
            throw new DirectoryNotFoundException("The external metadata root is unavailable.");
        }
        if (PathsOverlap(package.MediaRoot, external))
        {
            throw new InvalidOperationException(
                "The external metadata root must not overlap the read-only media root.");
        }

        MetadataPathSafety.EnsureNoReparsePoint(external, external);
        return this with
        {
            MediaRoot = package.MediaRoot,
            InboxRoot = package.InboxRoot,
            ExternalMetadataRoot = external,
        };
    }

    private static bool PathsOverlap(string first, string second) =>
        IsSameOrChild(first, second) || IsSameOrChild(second, first);

    private static bool IsSameOrChild(string parent, string candidate)
    {
        string relative = Path.GetRelativePath(parent, candidate);
        return string.Equals(relative, ".", StringComparison.Ordinal)
            || (!Path.IsPathFullyQualified(relative)
                && !string.Equals(relative, "..", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }
}

public interface IMediaLibraryRefreshService
{
    Task<MediaLibraryRefreshResult> RefreshAsync(CancellationToken cancellationToken);
}

public sealed class MediaLibraryRefreshService : IMediaLibraryRefreshService
{
    private readonly MediaLibraryRefreshOptions _options;
    private readonly ExternalAssetMetadataGenerationStore _generationStore;
    private readonly ReadyMediaPackageVerifier _packageVerifier;
    private readonly MediaPackageMetadataResolver _metadataResolver;
    private readonly IMediaMetadataRefreshLock _refreshLock;
    private readonly IMediaRefreshOperationStore _operationStore;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _operationIdFactory;

    public MediaLibraryRefreshService(
        MediaLibraryRefreshOptions options,
        ExternalAssetMetadataGenerationStore? generationStore = null,
        ReadyMediaPackageVerifier? packageVerifier = null,
        MediaPackageMetadataResolver? metadataResolver = null,
        IMediaMetadataRefreshLock? refreshLock = null,
        IMediaRefreshOperationStore? operationStore = null,
        TimeProvider? timeProvider = null,
        Func<string>? operationIdFactory = null)
    {
        _options = options.Validate();
        _generationStore = generationStore
            ?? new ExternalAssetMetadataGenerationStore(_options.ExternalMetadataRoot);
        _packageVerifier = packageVerifier ?? new ReadyMediaPackageVerifier();
        _metadataResolver = metadataResolver ?? new MediaPackageMetadataResolver();
        _refreshLock = refreshLock ?? new MediaMetadataRefreshLock();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _operationStore = operationStore
            ?? new MediaRefreshOperationStore(_options.ExternalMetadataRoot, timeProvider: _timeProvider);
        _operationIdFactory = operationIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    }

    public async Task<MediaLibraryRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        IMediaMetadataRefreshLease lease = await _refreshLock.TryAcquireAsync(
            _options.ExternalMetadataRoot,
            cancellationToken).ConfigureAwait(false);
        return await RefreshWithLeaseAsync(lease, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MediaLibraryRefreshResult> RefreshWithLeaseAsync(
        IMediaMetadataRefreshLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.Consume(_options.ExternalMetadataRoot);
        await using IAsyncDisposable held = lease;
        AssetMetadataGenerationPointer current = _generationStore.ReadCurrent();
        _ = await _operationStore.ReconcileRunningAsync(current, cancellationToken).ConfigureAwait(false);
        var state = new RefreshState(
            _operationIdFactory(),
            _timeProvider.GetUtcNow(),
            current);
        await _operationStore.WriteAsync(state.ToRunning(), cancellationToken).ConfigureAwait(false);

        try
        {
            return await RefreshCoreAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            MediaLibraryRefreshResult interrupted = state.ToCompleted(
                MediaLibraryRefreshStatus.Interrupted,
                _timeProvider.GetUtcNow(),
                errorCount: 1);
            await TryWriteAsync(interrupted).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (IsExpectedRefreshFailure(exception))
        {
            MediaLibraryRefreshIssueCode code = exception is CatalogChangedDuringRefreshException
                ? MediaLibraryRefreshIssueCode.CatalogChanged
                : MediaLibraryRefreshIssueCode.RefreshFailed;
            state.Issues.Add(new MediaLibraryRefreshIssue(code));
            MediaLibraryRefreshResult failed = state.ToCompleted(
                MediaLibraryRefreshStatus.Failed,
                _timeProvider.GetUtcNow(),
                errorCount: Math.Max(1, state.Issues.Count));
            await TryWriteAsync(failed).ConfigureAwait(false);
            return failed;
        }
    }

    private async Task<MediaLibraryRefreshResult> RefreshCoreAsync(
        RefreshState state,
        CancellationToken cancellationToken)
    {
        var identity = new AssetMetadataSnapshotIdentity(
            AssetMetadataStorageMode.ExternalGeneration,
            state.Current.Revision,
            state.Current.GenerationId);
        AssetMetadataGenerationInventory currentInventory = _generationStore.ReadInventory(identity);
        IAssetMetadataSnapshot sourceSnapshot = _generationStore.Pin(
            AssetMetadataTree.Source,
            _options.PackageStorage.SourceRoot,
            identity);
        IAssetMetadataSnapshot librarySnapshot = _generationStore.Pin(
            AssetMetadataTree.Library,
            _options.PackageStorage.LibraryRoot,
            identity);
        AssetMetadataGenerationRecord[] preserved = ReadPreservedRecords(sourceSnapshot, librarySnapshot);
        state.MetadataRecordsPreserved = preserved.Length;
        StableSongCatalogSnapshot catalog = StableSongCatalogSnapshot.Load(_options.CatalogPath);
        var reservedAssetIds = new HashSet<string>(
            currentInventory.Assets!.Select(value => value.AssetId!),
            StringComparer.Ordinal);
        var accepted = new List<AcceptedPackage>();
        var seenPackageIds = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, AssetMetadataInventoryPackage> existingPackages = currentInventory.Packages!
            .ToDictionary(value => value.PackageId!, StringComparer.Ordinal);
        var existingSources = currentInventory.Assets!
            .Select(value => value.SourceRelativePath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingLibraries = currentInventory.Assets!
            .Select(value => value.LibraryRelativePath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string readyPath in DiscoverReadyManifests())
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.PackagesObserved++;
            ReadyMediaPackageManifest manifest;
            string digest;
            try
            {
                string fileName = Path.GetFileName(readyPath);
                string packageIdFromName = fileName[..^ReadyMediaPackageManifest.FileSuffix.Length];
                ReadyMediaPackageValidator.ValidateFileName(fileName, packageIdFromName);
                MetadataPathSafety.EnsureNoReparsePoint(_options.PackageStorage.EffectiveInboxRoot, readyPath);
                var file = new FileInfo(readyPath);
                if (!file.Exists || file.Length is <= 0 or > ReadyMediaPackageManifest.MaximumManifestBytes)
                {
                    throw new InvalidDataException("The READY manifest size is invalid.");
                }

                manifest = ReadyMediaPackageSerializer.Deserialize(File.ReadAllBytes(readyPath));
                ReadyMediaPackageValidator.ValidateFileName(fileName, manifest.PackageId!);
                digest = ReadyMediaPackageSerializer.CalculateDigest(manifest);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Reject(state, MediaLibraryRefreshIssueCode.InvalidReadyManifest);
                continue;
            }

            if (!seenPackageIds.Add(manifest.PackageId!))
            {
                Reject(state, MediaLibraryRefreshIssueCode.DuplicatePackageId, manifest);
                continue;
            }

            if (existingPackages.TryGetValue(manifest.PackageId!, out AssetMetadataInventoryPackage? existing))
            {
                if (string.Equals(existing.ManifestDigest, digest, StringComparison.Ordinal))
                {
                    state.PackagesAlreadyProcessed++;
                    state.SkippedPackages++;
                }
                else
                {
                    Reject(state, MediaLibraryRefreshIssueCode.PackageIdConflict, manifest);
                }

                continue;
            }

            if (existingSources.Contains(manifest.SourceRelativePath!)
                || existingLibraries.Contains(manifest.LibraryRelativePath!)
                || accepted.Any(value => string.Equals(
                        value.Manifest.SourceRelativePath,
                        manifest.SourceRelativePath,
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        value.Manifest.LibraryRelativePath,
                        manifest.LibraryRelativePath,
                        StringComparison.OrdinalIgnoreCase)))
            {
                Reject(state, MediaLibraryRefreshIssueCode.ExistingMediaConflict, manifest);
                continue;
            }

            try
            {
                VerifiedReadyMediaPackage verified = await _packageVerifier.VerifyAsync(
                    _options.PackageStorage,
                    manifest,
                    cancellationToken).ConfigureAwait(false);
                ResolvedPackageMetadata metadata = _metadataResolver.Resolve(
                    verified,
                    _options.PackageStorage.SourceRoot,
                    catalog.Catalog,
                    reservedAssetIds);
                accepted.Add(new AcceptedPackage(manifest, digest, metadata));
            }
            catch (MediaPackageMetadataResolutionException exception)
            {
                Reject(state, exception.Code, manifest);
                if (exception.Code is MediaLibraryRefreshIssueCode.CatalogMatchRequired
                    or MediaLibraryRefreshIssueCode.AmbiguousCatalogMatch)
                {
                    state.UnresolvedAssets++;
                }
            }
            catch (MediaPackageVerificationException exception)
            {
                Reject(state, exception.Code, manifest);
                state.IncompletePackages++;
            }
            catch (PackageMemberChangedException)
            {
                Reject(state, MediaLibraryRefreshIssueCode.PackageMemberChanged, manifest);
                state.IncompletePackages++;
            }
            catch (FileNotFoundException)
            {
                Reject(state, MediaLibraryRefreshIssueCode.MissingPackageMember, manifest);
                state.IncompletePackages++;
            }
            catch (Exception exception) when (exception is
                InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Reject(state, MediaLibraryRefreshIssueCode.PackageMemberMismatch, manifest);
                state.IncompletePackages++;
            }
        }

        if (accepted.Count == 0)
        {
            state.WarningCount = state.Issues.Count;
            state.MarkNoChanges();
            MediaLibraryRefreshResult noChanges = state.ToCompleted(
                MediaLibraryRefreshStatus.NoChanges,
                _timeProvider.GetUtcNow());
            await _operationStore.WriteAsync(noChanges, cancellationToken).ConfigureAwait(false);
            return noChanges;
        }

        catalog.VerifyUnchanged(_options.CatalogPath);
        long revision = checked(state.Current.Revision + 1);
        string generationId = _generationStore.AllocateNextGenerationId();
        AssetMetadataGenerationRecord[] newRecords = accepted
            .SelectMany(value => new[]
            {
                new AssetMetadataGenerationRecord(
                    AssetMetadataTree.Source,
                    value.Manifest.SourceRelativePath!,
                    value.Metadata.Metadata,
                    value.Metadata.SourceJsonBytes),
                new AssetMetadataGenerationRecord(
                    AssetMetadataTree.Library,
                    value.Manifest.LibraryRelativePath!,
                    value.Metadata.Metadata,
                    value.Metadata.LibraryJsonBytes),
            })
            .ToArray();
        AssetMetadataGenerationInventory inventory = BuildInventory(
            currentInventory,
            accepted,
            generationId,
            revision);
        state.ApplyAccepted(accepted, generationId, revision);
        await _generationStore.CreateGenerationAsync(
            generationId,
            revision,
            [.. preserved, .. newRecords],
            inventory,
            cancellationToken).ConfigureAwait(false);
        _ = _generationStore.ReadInventory(new AssetMetadataSnapshotIdentity(
            AssetMetadataStorageMode.ExternalGeneration,
            revision,
            generationId));
        catalog.VerifyUnchanged(_options.CatalogPath);
        await _operationStore.WriteAsync(state.ToRunning(), cancellationToken).ConfigureAwait(false);
        _ = await _generationStore.PublishCurrentAsync(
            generationId,
            state.Current.Revision,
            cancellationToken).ConfigureAwait(false);

        state.WarningCount = state.Issues.Count;
        MediaLibraryRefreshStatus status = state.Issues.Count == 0
            ? MediaLibraryRefreshStatus.Succeeded
            : MediaLibraryRefreshStatus.SucceededWithWarnings;
        MediaLibraryRefreshResult completed = state.ToCompleted(status, _timeProvider.GetUtcNow());
        try
        {
            await _operationStore.WriteAsync(completed, cancellationToken).ConfigureAwait(false);
            return completed;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException)
        {
            state.Issues.Add(new MediaLibraryRefreshIssue(
                MediaLibraryRefreshIssueCode.OperationFinalizationInterrupted));
            state.WarningCount = state.Issues.Count;
            MediaLibraryRefreshResult finalizedWithWarning = state.ToCompleted(
                MediaLibraryRefreshStatus.SucceededWithWarnings,
                _timeProvider.GetUtcNow());
            await TryWriteAsync(finalizedWithWarning).ConfigureAwait(false);
            return finalizedWithWarning;
        }
    }

    private IReadOnlyList<string> DiscoverReadyManifests()
    {
        string inbox = _options.PackageStorage.EffectiveInboxRoot;
        MetadataPathSafety.EnsureNoReparsePoint(inbox, inbox);
        string[] files = Directory.EnumerateFiles(
                inbox,
                $"*{ReadyMediaPackageManifest.FileSuffix}",
                SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Take(MediaLibraryRefreshOptions.MaximumReadyManifests + 1)
            .ToArray();
        if (files.Length > MediaLibraryRefreshOptions.MaximumReadyManifests)
        {
            throw new InvalidDataException("The READY inbox contains too many manifests for one refresh.");
        }

        return files;
    }

    private static AssetMetadataGenerationRecord[] ReadPreservedRecords(
        IAssetMetadataSnapshot source,
        IAssetMetadataSnapshot library) =>
        source.DiscoverRelativeMediaPaths()
            .Select(relative => ToRecord(AssetMetadataTree.Source, relative, source.Read(relative)))
            .Concat(library.DiscoverRelativeMediaPaths()
                .Select(relative => ToRecord(AssetMetadataTree.Library, relative, library.Read(relative))))
            .ToArray();

    private static AssetMetadataGenerationRecord ToRecord(
        AssetMetadataTree tree,
        string relative,
        AssetMetadataDocument document) =>
        new(tree, relative, document.Metadata, document.JsonBytes);

    private static AssetMetadataGenerationInventory BuildInventory(
        AssetMetadataGenerationInventory current,
        IReadOnlyCollection<AcceptedPackage> accepted,
        string generationId,
        long revision)
    {
        AssetMetadataInventoryPackage[] packages = accepted.Select(value => new AssetMetadataInventoryPackage
        {
            PackageId = value.Manifest.PackageId,
            ManifestDigest = value.ManifestDigest,
            SourceRelativePath = value.Manifest.SourceRelativePath,
            LibraryRelativePath = value.Manifest.LibraryRelativePath,
            SourceSha256 = value.Manifest.Source!.Sha256,
            LibrarySha256 = value.Manifest.Library!.Sha256,
            TechnicalManifestSha256 = value.Manifest.TechnicalManifest!.Sha256,
            SourceProgrammingMetadataSha256 = value.Manifest.SourceProgrammingMetadata!.Sha256,
            LibraryProgrammingMetadataSha256 = value.Manifest.LibraryProgrammingMetadata!.Sha256,
        }).ToArray();
        AssetMetadataInventoryAsset[] assets = accepted.Select(value => new AssetMetadataInventoryAsset
        {
            PackageId = value.Manifest.PackageId,
            SourceRelativePath = value.Manifest.SourceRelativePath,
            LibraryRelativePath = value.Manifest.LibraryRelativePath,
            AssetId = value.Metadata.Metadata.AssetId,
            ContentGroupId = value.Metadata.Metadata.ContentGroupId,
            TechnicalManifestSha256 = value.Manifest.TechnicalManifest!.Sha256,
            SourceMetadataSha256 = MediaPackageHash.Sha256(value.Metadata.SourceJsonBytes),
            LibraryMetadataSha256 = MediaPackageHash.Sha256(value.Metadata.LibraryJsonBytes),
            Classification = value.Metadata.Classification,
        }).ToArray();
        return AssetMetadataGenerationInventorySerializer.NormalizeAndValidate(new AssetMetadataGenerationInventory
        {
            Revision = revision,
            GenerationId = generationId,
            Packages = [.. current.Packages!, .. packages],
            Assets = [.. current.Assets!, .. assets],
        });
    }

    private static void Reject(
        RefreshState state,
        MediaLibraryRefreshIssueCode code,
        ReadyMediaPackageManifest? manifest = null)
    {
        state.PackagesRejected++;
        state.Issues.Add(new MediaLibraryRefreshIssue(
            code,
            manifest?.PackageId,
            manifest?.LibraryRelativePath));
    }

    private async Task TryWriteAsync(MediaLibraryRefreshResult result)
    {
        try
        {
            await _operationStore.WriteAsync(result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
        }
    }

    private static bool IsExpectedRefreshFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        InvalidDataException or
        InvalidOperationException or
        AssetMetadataValidationException or
        CatalogValidationException;

    private sealed record AcceptedPackage(
        ReadyMediaPackageManifest Manifest,
        string ManifestDigest,
        ResolvedPackageMetadata Metadata);

    private sealed class RefreshState(
        string operationId,
        DateTimeOffset startedAt,
        AssetMetadataGenerationPointer current)
    {
        public string OperationId { get; } = operationId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public AssetMetadataGenerationPointer Current { get; } = current;
        public List<MediaLibraryRefreshIssue> Issues { get; } = [];
        public string? GenerationIdAfter { get; private set; }
        public long RevisionAfter { get; private set; } = current.Revision;
        public int PackagesObserved { get; set; }
        public int PackagesAccepted { get; private set; }
        public int PackagesAlreadyProcessed { get; set; }
        public int PackagesRejected { get; set; }
        public int MetadataRecordsPreserved { get; set; }
        public int UnresolvedAssets { get; set; }
        public int IncompletePackages { get; set; }
        public int SkippedPackages { get; set; }
        public int WarningCount { get; set; }
        public int AssetsNewlyEligible { get; private set; }

        public void ApplyAccepted(
            IReadOnlyCollection<AcceptedPackage> accepted,
            string generationId,
            long revision)
        {
            PackagesAccepted = accepted.Count;
            AssetsNewlyEligible = accepted.Count(value =>
                value.Metadata.Classification == AssetMetadataInventoryClassification.PlaylistEligible);
            GenerationIdAfter = generationId;
            RevisionAfter = revision;
        }

        public void MarkNoChanges() => GenerationIdAfter = Current.GenerationId;

        public MediaLibraryRefreshResult ToRunning() => Create(MediaLibraryRefreshStatus.Running, null, 0);

        public MediaLibraryRefreshResult ToCompleted(
            MediaLibraryRefreshStatus status,
            DateTimeOffset completedAt,
            int errorCount = 0) => Create(status, completedAt, errorCount);

        private MediaLibraryRefreshResult Create(
            MediaLibraryRefreshStatus status,
            DateTimeOffset? completedAt,
            int errorCount) => new()
            {
                OperationId = OperationId,
                StartedAt = StartedAt,
                CompletedAt = completedAt,
                Status = status,
                MetadataRevisionBefore = Current.Revision,
                MetadataRevisionAfter = RevisionAfter,
                GenerationIdBefore = Current.GenerationId,
                GenerationIdAfter = GenerationIdAfter,
                PackagesObserved = PackagesObserved,
                PackagesAccepted = PackagesAccepted,
                PackagesAlreadyProcessed = PackagesAlreadyProcessed,
                PackagesRejected = PackagesRejected,
                NewSourceAssets = PackagesAccepted,
                NewLibraryAssets = PackagesAccepted,
                MetadataRecordsCreated = checked(PackagesAccepted * 2),
                MetadataRecordsPreserved = MetadataRecordsPreserved,
                AssetsNewlyEligible = AssetsNewlyEligible,
                UnresolvedAssets = UnresolvedAssets,
                IncompletePackages = IncompletePackages,
                SkippedPackages = SkippedPackages,
                WarningCount = WarningCount,
                ErrorCount = errorCount,
                Issues = Issues.ToArray(),
            };
    }
}
