using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class MediaLibraryRefreshTests
{
    [Fact]
    public async Task Refresh_AcceptsNewPackagePublishesGenerationAndNeverChangesMedia()
    {
        using var fixture = new MediaPackageTestFixture();
        ExternalAssetMetadataGenerationStore store = await fixture.InitializeEmptyExternalAsync();
        (string source, string library) = await fixture.AddMembersAsync();
        ReadyPackagePreparationResult package = await fixture.PrepareAsync();
        byte[] sourceBefore = await File.ReadAllBytesAsync(source);
        byte[] libraryBefore = await File.ReadAllBytesAsync(library);
        var service = CreateService(fixture, store);

        MediaLibraryRefreshResult result = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(MediaLibraryRefreshStatus.Succeeded, result.Status);
        Assert.Equal(1, result.PackagesAccepted);
        Assert.Equal(1, result.AssetsNewlyEligible);
        Assert.Equal(2, result.MetadataRevisionAfter);
        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(source));
        Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(library));
        AssetMetadataGenerationPointer current = store.ReadCurrent();
        AssetMetadataGenerationInventory inventory = store.ReadInventory(Identity(current));
        Assert.Equal(2, current.Revision);
        Assert.Equal(package.PackageId, Assert.Single(inventory.Packages!).PackageId);
        Assert.Equal("test-song-music-video", Assert.Single(inventory.Assets!).AssetId);
    }

    [Fact]
    public async Task Refresh_RepeatedPackageIsAlreadyProcessedAndDoesNotCreateGeneration()
    {
        using var fixture = new MediaPackageTestFixture();
        ExternalAssetMetadataGenerationStore store = await fixture.InitializeEmptyExternalAsync();
        await fixture.AddMembersAsync();
        await fixture.PrepareAsync();
        var service = CreateService(fixture, store);
        MediaLibraryRefreshResult first = await service.RefreshAsync(CancellationToken.None);
        int generationCount = Directory.EnumerateDirectories(
            Path.Combine(fixture.MetadataRoot, "generations")).Count();

        MediaLibraryRefreshResult second = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(MediaLibraryRefreshStatus.NoChanges, second.Status);
        Assert.Equal(1, second.PackagesAlreadyProcessed);
        Assert.Equal(first.MetadataRevisionAfter, second.MetadataRevisionAfter);
        Assert.Equal(generationCount, Directory.EnumerateDirectories(
            Path.Combine(fixture.MetadataRoot, "generations")).Count());
    }

    [Fact]
    public async Task Refresh_RejectsPackageIdConflictAndNewIdForExistingPaths()
    {
        using var fixture = new MediaPackageTestFixture();
        ExternalAssetMetadataGenerationStore store = await fixture.InitializeEmptyExternalAsync();
        await fixture.AddMembersAsync();
        ReadyPackagePreparationResult original = await fixture.PrepareAsync();
        var service = CreateService(fixture, store);
        _ = await service.RefreshAsync(CancellationToken.None);
        string originalPath = Path.Combine(fixture.InboxRoot, original.ReadyFileName);
        ReadyMediaPackageManifest changed = original.Manifest with
        {
            PackageCreatedAt = original.Manifest.PackageCreatedAt.AddSeconds(1),
        };
        await File.WriteAllTextAsync(originalPath, ReadyMediaPackageSerializer.Serialize(changed));
        string newId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        ReadyMediaPackageManifest duplicatePath = original.Manifest with { PackageId = newId };
        await File.WriteAllTextAsync(
            Path.Combine(fixture.InboxRoot, newId + ReadyMediaPackageManifest.FileSuffix),
            ReadyMediaPackageSerializer.Serialize(duplicatePath));

        MediaLibraryRefreshResult result = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(MediaLibraryRefreshStatus.NoChanges, result.Status);
        Assert.Contains(result.Issues!, issue => issue.Code == MediaLibraryRefreshIssueCode.PackageIdConflict);
        Assert.Contains(result.Issues!, issue => issue.Code == MediaLibraryRefreshIssueCode.ExistingMediaConflict);
        Assert.Equal(2, store.ReadCurrent().Revision);
    }

    [Fact]
    public async Task Refresh_CarriesForwardPriorInventoryAndExactMetadata()
    {
        using var fixture = new MediaPackageTestFixture();
        ExternalAssetMetadataGenerationStore store = await fixture.InitializeEmptyExternalAsync();
        await fixture.AddMembersAsync(
            sourceMetadata: MediaPackageTestFixture.Metadata(),
            libraryMetadata: MediaPackageTestFixture.Metadata());
        await fixture.PrepareAsync();
        var service = CreateService(fixture, store);
        _ = await service.RefreshAsync(CancellationToken.None);
        AssetMetadataGenerationPointer firstPointer = store.ReadCurrent();
        IAssetMetadataSnapshot first = store.Pin(
            AssetMetadataTree.Library,
            fixture.LibraryRoot,
            Identity(firstPointer));
        byte[] firstBytes = first.Read("Music Videos/Test Song.mp4").JsonBytes;

        await fixture.AddMembersAsync("Music Videos/Other Song.mov");
        await fixture.PrepareAsync("Music Videos/Other Song.mov");
        MediaLibraryRefreshResult result = await service.RefreshAsync(CancellationToken.None);
        AssetMetadataGenerationPointer secondPointer = store.ReadCurrent();
        IAssetMetadataSnapshot second = store.Pin(
            AssetMetadataTree.Library,
            fixture.LibraryRoot,
            Identity(secondPointer));
        AssetMetadataGenerationInventory inventory = store.ReadInventory(Identity(secondPointer));

        Assert.Equal(MediaLibraryRefreshStatus.Succeeded, result.Status);
        Assert.Equal(2, inventory.Packages!.Count);
        Assert.Equal(2, inventory.Assets!.Count);
        Assert.Equal(firstBytes, second.Read("Music Videos/Test Song.mp4").JsonBytes);
    }

    [Fact]
    public async Task Refresh_UnknownSongAndConflictingMetadataRemainUnprocessed()
    {
        using var fixture = new MediaPackageTestFixture();
        ExternalAssetMetadataGenerationStore store = await fixture.InitializeEmptyExternalAsync();
        await fixture.AddMembersAsync("Music Videos/Unknown Record.mov");
        await fixture.PrepareAsync("Music Videos/Unknown Record.mov");
        await fixture.AddMembersAsync(
            "Music Videos/Test Song.mov",
            MediaPackageTestFixture.Metadata(),
            MediaPackageTestFixture.Metadata(
                assetId: "different",
                contentGroupId: "other-song",
                title: "Other Song"));
        await fixture.PrepareAsync("Music Videos/Test Song.mov");

        MediaLibraryRefreshResult result = await CreateService(fixture, store)
            .RefreshAsync(CancellationToken.None);

        Assert.Equal(MediaLibraryRefreshStatus.NoChanges, result.Status);
        Assert.Contains(result.Issues!, issue => issue.Code == MediaLibraryRefreshIssueCode.CatalogMatchRequired);
        Assert.Contains(result.Issues!, issue => issue.Code == MediaLibraryRefreshIssueCode.ConflictingProgrammingMetadata);
        Assert.Empty(store.ReadInventory(Identity(store.ReadCurrent())).Packages!);
    }

    [Fact]
    public async Task Refresh_AcceptsCatalogAliasAndDeterministicNonSongMetadata()
    {
        using var fixture = new MediaPackageTestFixture();
        ExternalAssetMetadataGenerationStore store = await fixture.InitializeEmptyExternalAsync();
        await fixture.AddMembersAsync("Music Videos/The Test Song.mov");
        await fixture.PrepareAsync("Music Videos/The Test Song.mov");
        await fixture.AddMembersAsync("Bumpers/Station Ident.mov");
        await fixture.PrepareAsync("Bumpers/Station Ident.mov");

        MediaLibraryRefreshResult result = await CreateService(fixture, store)
            .RefreshAsync(CancellationToken.None);
        AssetMetadataGenerationInventory inventory = store.ReadInventory(Identity(store.ReadCurrent()));

        Assert.Equal(MediaLibraryRefreshStatus.Succeeded, result.Status);
        Assert.Equal(2, result.PackagesAccepted);
        Assert.Equal(2, result.AssetsNewlyEligible);
        Assert.Contains(inventory.Assets!, asset => asset.ContentGroupId == "test-song");
        Assert.Contains(inventory.Assets!, asset => asset.ContentGroupId is null);
    }

    [Fact]
    public async Task Refresh_DuplicateAssetIdAcceptsOnlyOnePackageAndReportsFixedIssue()
    {
        using var fixture = new MediaPackageTestFixture();
        ExternalAssetMetadataGenerationStore store = await fixture.InitializeEmptyExternalAsync();
        AssetMetadata shared = MediaPackageTestFixture.Metadata(assetId: "shared-asset");
        await fixture.AddMembersAsync(sourceMetadata: shared, libraryMetadata: shared);
        await fixture.PrepareAsync();
        AssetMetadata other = MediaPackageTestFixture.Metadata(
            assetId: "shared-asset",
            contentGroupId: "other-song",
            title: "Other Song");
        await fixture.AddMembersAsync(
            "Music Videos/Other Song.mov",
            sourceMetadata: other,
            libraryMetadata: other);
        await fixture.PrepareAsync("Music Videos/Other Song.mov");

        MediaLibraryRefreshResult result = await CreateService(fixture, store)
            .RefreshAsync(CancellationToken.None);

        Assert.Equal(MediaLibraryRefreshStatus.SucceededWithWarnings, result.Status);
        Assert.Equal(1, result.PackagesAccepted);
        Assert.Equal(1, result.PackagesRejected);
        Assert.Contains(result.Issues!, issue => issue.Code == MediaLibraryRefreshIssueCode.DuplicateAssetId);
    }

    [Fact]
    public async Task Refresh_MalformedCatalogAndTooManyReadyFilesFailWithoutPublication()
    {
        using var malformed = new MediaPackageTestFixture();
        ExternalAssetMetadataGenerationStore malformedStore = await malformed.InitializeEmptyExternalAsync();
        await File.WriteAllTextAsync(malformed.CatalogPath, "{ malformed }");
        MediaLibraryRefreshResult malformedResult = await CreateService(malformed, malformedStore)
            .RefreshAsync(CancellationToken.None);
        Assert.Equal(MediaLibraryRefreshStatus.Failed, malformedResult.Status);
        Assert.Equal(1, malformedStore.ReadCurrent().Revision);

        using var crowded = new MediaPackageTestFixture();
        ExternalAssetMetadataGenerationStore crowdedStore = await crowded.InitializeEmptyExternalAsync();
        for (int index = 0; index <= MediaLibraryRefreshOptions.MaximumReadyManifests; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(crowded.InboxRoot, $"{index:x32}.ready.json"),
                "{}");
        }

        MediaLibraryRefreshResult crowdedResult = await CreateService(crowded, crowdedStore)
            .RefreshAsync(CancellationToken.None);
        Assert.Equal(MediaLibraryRefreshStatus.Failed, crowdedResult.Status);
        Assert.Equal(1, crowdedStore.ReadCurrent().Revision);
    }

    [Fact]
    public async Task Refresh_IgnoresTemporaryPartialAndUnrelatedInboxFiles()
    {
        using var fixture = new MediaPackageTestFixture();
        ExternalAssetMetadataGenerationStore store = await fixture.InitializeEmptyExternalAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.InboxRoot, "package.ready.json.partial"), "{}");
        await File.WriteAllTextAsync(Path.Combine(fixture.InboxRoot, "notes.txt"), "ignore");

        MediaLibraryRefreshResult result = await CreateService(fixture, store)
            .RefreshAsync(CancellationToken.None);

        Assert.Equal(MediaLibraryRefreshStatus.NoChanges, result.Status);
        Assert.Equal(0, result.PackagesObserved);
        Assert.Equal(1, store.ReadCurrent().Revision);
    }

    [Fact]
    public async Task Refresh_MissingMemberReturnsSanitizedIssueAndLeavesCurrentUnchanged()
    {
        using var fixture = new MediaPackageTestFixture();
        ExternalAssetMetadataGenerationStore store = await fixture.InitializeEmptyExternalAsync();
        (_, string library) = await fixture.AddMembersAsync();
        await fixture.PrepareAsync();
        File.Delete(library);

        MediaLibraryRefreshResult result = await CreateService(fixture, store)
            .RefreshAsync(CancellationToken.None);
        string json = MediaLibraryRefreshResultSerializer.Serialize(result);

        Assert.Equal(MediaLibraryRefreshStatus.NoChanges, result.Status);
        Assert.Contains(result.Issues!, issue => issue.Code == MediaLibraryRefreshIssueCode.MissingPackageMember);
        Assert.DoesNotContain(fixture.Root, json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, store.ReadCurrent().Revision);
    }

    [Fact]
    public async Task Refresh_PointerWriteFailureLeavesPriorGenerationCurrent()
    {
        using var fixture = new MediaPackageTestFixture();
        _ = await fixture.InitializeEmptyExternalAsync();
        await fixture.AddMembersAsync();
        await fixture.PrepareAsync();
        var failingStore = new ExternalAssetMetadataGenerationStore(
            fixture.MetadataRoot,
            new FailingPointerWriter());
        var service = CreateService(fixture, failingStore);

        MediaLibraryRefreshResult result = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(MediaLibraryRefreshStatus.Failed, result.Status);
        Assert.Equal(1, new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot).ReadCurrent().Revision);
        Assert.DoesNotContain(fixture.Root, MediaLibraryRefreshResultSerializer.Serialize(result));
    }

    [Fact]
    public async Task Refresh_OperationFinalizationFailureDoesNotMisreportPublishedGenerationAsFailed()
    {
        using var fixture = new MediaPackageTestFixture();
        ExternalAssetMetadataGenerationStore store = await fixture.InitializeEmptyExternalAsync();
        await fixture.AddMembersAsync();
        await fixture.PrepareAsync();
        var operationStore = new FinalWriteFailingOperationStore(fixture.MetadataRoot);
        var service = new MediaLibraryRefreshService(
            new MediaLibraryRefreshOptions(fixture.MediaRoot, fixture.MetadataRoot, fixture.InboxRoot),
            generationStore: store,
            operationStore: operationStore);

        MediaLibraryRefreshResult result = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(MediaLibraryRefreshStatus.SucceededWithWarnings, result.Status);
        Assert.Contains(result.Issues!, issue =>
            issue.Code == MediaLibraryRefreshIssueCode.OperationFinalizationInterrupted);
        Assert.Equal(2, store.ReadCurrent().Revision);
        IReadOnlyList<MediaLibraryRefreshResult> reconciled = await operationStore.Inner
            .ReconcileRunningAsync(store.ReadCurrent(), CancellationToken.None);
        Assert.Contains(reconciled, value => value.Status == MediaLibraryRefreshStatus.SucceededWithWarnings);
    }

    [Fact]
    public async Task Refresh_CatalogChangeDuringVerificationAbortsPublication()
    {
        using var fixture = new MediaPackageTestFixture();
        ExternalAssetMetadataGenerationStore store = await fixture.InitializeEmptyExternalAsync();
        await fixture.AddMembersAsync();
        await fixture.PrepareAsync();
        var hasher = new CatalogMutatingHasher(fixture.CatalogPath);
        var service = new MediaLibraryRefreshService(
            new MediaLibraryRefreshOptions(fixture.MediaRoot, fixture.MetadataRoot, fixture.InboxRoot),
            generationStore: store,
            packageVerifier: new ReadyMediaPackageVerifier(hasher));

        MediaLibraryRefreshResult result = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(MediaLibraryRefreshStatus.Failed, result.Status);
        Assert.Contains(result.Issues!, issue => issue.Code == MediaLibraryRefreshIssueCode.CatalogChanged);
        Assert.Equal(1, store.ReadCurrent().Revision);
    }

    [Fact]
    public async Task RefreshLock_IsCrossProcessExclusiveAndReleasedAfterDispose()
    {
        using var fixture = new MediaPackageTestFixture();
        var provider = new MediaMetadataRefreshLock();
        await using IAsyncDisposable first = await provider.TryAcquireAsync(
            fixture.MetadataRoot,
            CancellationToken.None);

        await Assert.ThrowsAsync<MediaMetadataRefreshBusyException>(async () =>
            await provider.TryAcquireAsync(fixture.MetadataRoot, CancellationToken.None));
        await first.DisposeAsync();
        await using IAsyncDisposable recovered = await provider.TryAcquireAsync(
            fixture.MetadataRoot,
            CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentRefresh_ExactlyOneWriterOwnsTheOperation()
    {
        using var fixture = new MediaPackageTestFixture();
        ExternalAssetMetadataGenerationStore store = await fixture.InitializeEmptyExternalAsync();
        await fixture.AddMembersAsync();
        await fixture.PrepareAsync();
        var hasher = new BlockingHasher();
        var firstService = new MediaLibraryRefreshService(
            new MediaLibraryRefreshOptions(fixture.MediaRoot, fixture.MetadataRoot, fixture.InboxRoot),
            generationStore: store,
            packageVerifier: new ReadyMediaPackageVerifier(hasher));
        MediaLibraryRefreshService secondService = CreateService(fixture, store);

        Task<MediaLibraryRefreshResult> first = firstService.RefreshAsync(CancellationToken.None);
        await hasher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<MediaMetadataRefreshBusyException>(
            () => secondService.RefreshAsync(CancellationToken.None));
        hasher.Release.SetResult();
        MediaLibraryRefreshResult completed = await first;

        Assert.Equal(MediaLibraryRefreshStatus.Succeeded, completed.Status);
        MediaLibraryRefreshResult later = await secondService.RefreshAsync(CancellationToken.None);
        Assert.Equal(MediaLibraryRefreshStatus.NoChanges, later.Status);
    }

    [Fact]
    public async Task OperationReconciliation_DistinguishesPublishedFromInterrupted()
    {
        using var fixture = new MediaPackageTestFixture();
        ExternalAssetMetadataGenerationStore store = await fixture.InitializeEmptyExternalAsync();
        var time = new FixedRollingTimeProvider(
            new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        var operations = new MediaRefreshOperationStore(fixture.MetadataRoot, timeProvider: time);
        await operations.WriteAsync(Running("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null, 1), CancellationToken.None);
        await operations.WriteAsync(Running(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "000000000001",
            1), CancellationToken.None);

        IReadOnlyList<MediaLibraryRefreshResult> reconciled = await operations.ReconcileRunningAsync(
            store.ReadCurrent(),
            CancellationToken.None);

        Assert.Contains(reconciled, value => value.Status == MediaLibraryRefreshStatus.Interrupted);
        Assert.Contains(reconciled, value => value.Status == MediaLibraryRefreshStatus.SucceededWithWarnings);
    }

    private static MediaLibraryRefreshService CreateService(
        MediaPackageTestFixture fixture,
        ExternalAssetMetadataGenerationStore store) => new(
        new MediaLibraryRefreshOptions(fixture.MediaRoot, fixture.MetadataRoot, fixture.InboxRoot),
        generationStore: store);

    private static AssetMetadataSnapshotIdentity Identity(AssetMetadataGenerationPointer pointer) => new(
        AssetMetadataStorageMode.ExternalGeneration,
        pointer.Revision,
        pointer.GenerationId);

    private static MediaLibraryRefreshResult Running(string operationId, string? generationId, long revision) => new()
    {
        OperationId = operationId,
        StartedAt = new DateTimeOffset(2026, 10, 9, 11, 0, 0, TimeSpan.Zero),
        Status = MediaLibraryRefreshStatus.Running,
        MetadataRevisionBefore = 1,
        MetadataRevisionAfter = revision,
        GenerationIdBefore = "000000000001",
        GenerationIdAfter = generationId,
        Issues = [],
    };

    private sealed class FailingPointerWriter : IAtomicTextFileWriter
    {
        public Task WriteAsync(string path, string content, CancellationToken cancellationToken) =>
            throw new IOException("simulated");
    }

    private sealed class CatalogMutatingHasher(string catalogPath) : IPackageFileHasher
    {
        private readonly PackageFileHasher _inner = new();
        private int _mutated;

        public async Task<PackageFileHash> HashStableAsync(
            string path,
            int maximumCapturedBytes,
            CancellationToken cancellationToken)
        {
            PackageFileHash result = await _inner.HashStableAsync(path, maximumCapturedBytes, cancellationToken);
            if (Interlocked.Exchange(ref _mutated, 1) == 0)
            {
                await File.AppendAllTextAsync(catalogPath, " ", cancellationToken);
            }

            return result;
        }
    }

    private sealed class FinalWriteFailingOperationStore : IMediaRefreshOperationStore
    {
        private int _writes;

        public FinalWriteFailingOperationStore(string root)
        {
            Inner = new MediaRefreshOperationStore(root);
        }

        public MediaRefreshOperationStore Inner { get; }

        public Task WriteAsync(MediaLibraryRefreshResult result, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _writes) >= 3)
            {
                throw new IOException("simulated finalization failure");
            }

            return Inner.WriteAsync(result, cancellationToken);
        }

        public IReadOnlyList<MediaLibraryRefreshResult> LoadAll() => Inner.LoadAll();

        public Task<IReadOnlyList<MediaLibraryRefreshResult>> ReconcileRunningAsync(
            AssetMetadataGenerationPointer? current,
            CancellationToken cancellationToken) => Inner.ReconcileRunningAsync(current, cancellationToken);
    }

    private sealed class BlockingHasher : IPackageFileHasher
    {
        private readonly PackageFileHasher _inner = new();
        private int _blocked;

        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PackageFileHash> HashStableAsync(
            string path,
            int maximumCapturedBytes,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                Entered.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return await _inner.HashStableAsync(path, maximumCapturedBytes, cancellationToken);
        }
    }
}
