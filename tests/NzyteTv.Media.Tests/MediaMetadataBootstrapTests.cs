using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class MediaMetadataBootstrapTests
{
    [Fact]
    public async Task PreviewThenPublish_PreservesAdjacentBytesAndMediaAndCreatesRevisionOne()
    {
        using var fixture = new MediaPackageTestFixture();
        AssetMetadata metadata = MediaPackageTestFixture.Metadata();
        (string source, string library) = await fixture.AddMembersAsync(
            sourceMetadata: metadata,
            libraryMetadata: metadata);
        string sourceSidecar = AssetMetadataStore.GetMetadataPath(source);
        string librarySidecar = AssetMetadataStore.GetMetadataPath(library);
        byte[] sourceBefore = await File.ReadAllBytesAsync(source);
        byte[] libraryBefore = await File.ReadAllBytesAsync(library);
        byte[] sourceMetadataBefore = await File.ReadAllBytesAsync(sourceSidecar);
        byte[] libraryMetadataBefore = await File.ReadAllBytesAsync(librarySidecar);
        var service = new MediaMetadataBootstrapService(new MediaMetadataBootstrapOptions(
            fixture.MediaRoot,
            fixture.MetadataRoot));

        MediaMetadataBootstrapResult preview = await service.PreviewAsync(CancellationToken.None);

        Assert.Equal(MediaMetadataBootstrapStatus.PreviewReady, preview.Status);
        Assert.Equal(1, preview.SourceRecords);
        Assert.Equal(1, preview.LibraryRecords);
        Assert.Equal(1, preview.Assets);
        Assert.Empty(preview.Issues);
        Assert.False(File.Exists(Path.Combine(fixture.MetadataRoot, "current.json")));

        MediaMetadataBootstrapResult published = await service.PublishAsync(CancellationToken.None);

        Assert.Equal(MediaMetadataBootstrapStatus.Published, published.Status);
        Assert.Equal("000000000001", published.GenerationId);
        Assert.Equal(1, published.Revision);
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        AssetMetadataGenerationPointer current = store.ReadCurrent();
        AssetMetadataGenerationInventory inventory = store.ReadInventory(Identity(current));
        Assert.Single(inventory.Assets!);
        Assert.Empty(inventory.Packages!);
        Assert.Equal(sourceMetadataBefore, store.Pin(
            AssetMetadataTree.Source,
            fixture.SourceRoot,
            Identity(current)).Read("Music Videos/Test Song.mov").JsonBytes);
        Assert.Equal(libraryMetadataBefore, store.Pin(
            AssetMetadataTree.Library,
            fixture.LibraryRoot,
            Identity(current)).Read("Music Videos/Test Song.mp4").JsonBytes);
        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(source));
        Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(library));
        Assert.Equal(sourceMetadataBefore, await File.ReadAllBytesAsync(sourceSidecar));
        Assert.Equal(libraryMetadataBefore, await File.ReadAllBytesAsync(librarySidecar));

        await fixture.AddMembersAsync("Music Videos/Other Song.mov");
        await fixture.PrepareAsync("Music Videos/Other Song.mov");
        MediaLibraryRefreshResult refresh = await new MediaLibraryRefreshService(
            new MediaLibraryRefreshOptions(
                fixture.MediaRoot,
                fixture.MetadataRoot,
                fixture.InboxRoot),
            generationStore: store).RefreshAsync(CancellationToken.None);
        Assert.Equal(MediaLibraryRefreshStatus.Succeeded, refresh.Status);
        Assert.Equal(2, store.ReadCurrent().Revision);
        IAssetMetadataSnapshot legacyAdjacent = store.Pin(
            AssetMetadataTree.Library,
            fixture.LibraryRoot,
            new AssetMetadataSnapshotIdentity(AssetMetadataStorageMode.Adjacent));
        Assert.Equal(metadata.AssetId, legacyAdjacent.Read("Music Videos/Test Song.mp4").Metadata.AssetId);
        Assert.Equal(libraryMetadataBefore, await File.ReadAllBytesAsync(librarySidecar));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PublishAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Preview_AcceptsBlurredBackgroundTechnicalManifest()
    {
        using var fixture = new MediaPackageTestFixture();
        AssetMetadata metadata = MediaPackageTestFixture.Metadata();
        await fixture.AddMembersAsync(
            sourceMetadata: metadata,
            libraryMetadata: metadata,
            normalizationOptions: new NormalizationOptions
            {
                VerticalLayout = VerticalLayoutMode.BlurredBackground,
            });

        MediaMetadataBootstrapResult preview = await Service(fixture)
            .PreviewAsync(CancellationToken.None);

        Assert.Equal(MediaMetadataBootstrapStatus.PreviewReady, preview.Status);
        Assert.Equal(1, preview.SourceRecords);
        Assert.Equal(1, preview.LibraryRecords);
        Assert.Equal(1, preview.Assets);
        Assert.Empty(preview.Issues);
        Assert.False(File.Exists(Path.Combine(fixture.MetadataRoot, "current.json")));
    }

    [Fact]
    public async Task Preview_BlocksUnsupportedTechnicalManifestVerticalLayout()
    {
        using var fixture = new MediaPackageTestFixture();
        AssetMetadata metadata = MediaPackageTestFixture.Metadata();
        (string source, string library) = await fixture.AddMembersAsync(
            sourceMetadata: metadata,
            libraryMetadata: metadata);
        var store = new SourceManifestStore();
        SourceFingerprint fingerprint = store.CreateFingerprint(fixture.SourceRoot, source);
        await store.WriteAsync(
            library,
            fingerprint with { VerticalLayout = "unsupported-layout" },
            CancellationToken.None);

        MediaMetadataBootstrapResult preview = await Service(fixture)
            .PreviewAsync(CancellationToken.None);

        Assert.Equal(MediaMetadataBootstrapStatus.Blocked, preview.Status);
        Assert.Equal(1, preview.SourceRecords);
        Assert.Equal(1, preview.LibraryRecords);
        Assert.Equal(0, preview.Assets);
        Assert.Single(preview.Issues);
        Assert.False(File.Exists(Path.Combine(fixture.MetadataRoot, "current.json")));
    }

    [Fact]
    public async Task Preview_BlocksStaleSourceFingerprintWithBlurredBackgroundLayout()
    {
        using var fixture = new MediaPackageTestFixture();
        AssetMetadata metadata = MediaPackageTestFixture.Metadata();
        (string source, _) = await fixture.AddMembersAsync(
            sourceMetadata: metadata,
            libraryMetadata: metadata,
            normalizationOptions: new NormalizationOptions
            {
                VerticalLayout = VerticalLayoutMode.BlurredBackground,
            });
        await File.AppendAllTextAsync(source, "changed-after-normalization");

        MediaMetadataBootstrapResult preview = await Service(fixture)
            .PreviewAsync(CancellationToken.None);

        Assert.Equal(MediaMetadataBootstrapStatus.Blocked, preview.Status);
        Assert.Equal(1, preview.SourceRecords);
        Assert.Equal(1, preview.LibraryRecords);
        Assert.Equal(0, preview.Assets);
        Assert.Single(preview.Issues);
        Assert.False(File.Exists(Path.Combine(fixture.MetadataRoot, "current.json")));
    }

    [Fact]
    public async Task Bootstrap_BlocksMalformedMissingAndDuplicateAdjacentMetadata()
    {
        using var malformed = new MediaPackageTestFixture();
        (string malformedSource, _) = await malformed.AddMembersAsync(
            sourceMetadata: MediaPackageTestFixture.Metadata(),
            libraryMetadata: MediaPackageTestFixture.Metadata());
        await File.WriteAllTextAsync(AssetMetadataStore.GetMetadataPath(malformedSource), "{ malformed }");
        MediaMetadataBootstrapResult malformedResult = await Service(malformed)
            .PublishAsync(CancellationToken.None);
        Assert.Equal(MediaMetadataBootstrapStatus.Blocked, malformedResult.Status);
        Assert.False(File.Exists(Path.Combine(malformed.MetadataRoot, "current.json")));

        using var missing = new MediaPackageTestFixture();
        await missing.AddMembersAsync(sourceMetadata: MediaPackageTestFixture.Metadata());
        MediaMetadataBootstrapResult missingResult = await Service(missing)
            .PublishAsync(CancellationToken.None);
        Assert.Equal(MediaMetadataBootstrapStatus.Blocked, missingResult.Status);

        using var duplicate = new MediaPackageTestFixture();
        AssetMetadata first = MediaPackageTestFixture.Metadata(assetId: "duplicate");
        AssetMetadata second = MediaPackageTestFixture.Metadata(
            assetId: "duplicate",
            contentGroupId: "other-song",
            title: "Other Song");
        await duplicate.AddMembersAsync(sourceMetadata: first, libraryMetadata: first);
        await duplicate.AddMembersAsync(
            "Music Videos/Other Song.mov",
            sourceMetadata: second,
            libraryMetadata: second);
        MediaMetadataBootstrapResult duplicateResult = await Service(duplicate)
            .PublishAsync(CancellationToken.None);
        Assert.Equal(MediaMetadataBootstrapStatus.Blocked, duplicateResult.Status);
        Assert.False(File.Exists(Path.Combine(duplicate.MetadataRoot, "current.json")));
    }

    [Fact]
    public async Task BootstrapPointer_ConcurrentAttemptsHaveExactlyOneWinner()
    {
        using var fixture = new MediaPackageTestFixture();
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        var inventory = new AssetMetadataGenerationInventory
        {
            Revision = 1,
            GenerationId = "000000000001",
            Packages = [],
            Assets = [],
        };
        await store.CreateGenerationAsync(
            "000000000001",
            1,
            [],
            inventory,
            CancellationToken.None);

        Task<AssetMetadataGenerationPointer>[] attempts =
        [
            store.BootstrapCurrentAsync("000000000001", CancellationToken.None),
            store.BootstrapCurrentAsync("000000000001", CancellationToken.None),
        ];
        try
        {
            await Task.WhenAll(attempts);
        }
        catch (AssetMetadataGenerationConflictException)
        {
        }

        Task<AssetMetadataGenerationPointer> succeeded = Assert.Single(
            attempts,
            task => task.Status == TaskStatus.RanToCompletion);
        Task<AssetMetadataGenerationPointer> failed = Assert.Single(
            attempts,
            task => task.IsFaulted);
        Assert.True(succeeded.IsCompletedSuccessfully);
        Assert.IsType<AssetMetadataGenerationConflictException>(
            failed.Exception!.GetBaseException());
        Assert.Equal(1, store.ReadCurrent().Revision);
    }

    [Fact]
    public async Task InventoryRead_FailsClosedWhenGenerationMetadataNoLongerMatchesEvidence()
    {
        using var fixture = new MediaPackageTestFixture();
        AssetMetadata metadata = MediaPackageTestFixture.Metadata();
        await fixture.AddMembersAsync(sourceMetadata: metadata, libraryMetadata: metadata);
        await Service(fixture).PublishAsync(CancellationToken.None);
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        AssetMetadataGenerationPointer current = store.ReadCurrent();
        string externalLibraryMetadata = Path.Combine(
            fixture.MetadataRoot,
            "generations",
            current.GenerationId!,
            "library",
            "Music Videos",
            "Test Song.mp4.nzytetv.meta.json");
        await File.AppendAllTextAsync(externalLibraryMetadata, " ");

        Assert.Throws<InvalidDataException>(() => store.ReadInventory(Identity(current)));
    }

    private static MediaMetadataBootstrapService Service(MediaPackageTestFixture fixture) => new(
        new MediaMetadataBootstrapOptions(fixture.MediaRoot, fixture.MetadataRoot));

    private static AssetMetadataSnapshotIdentity Identity(AssetMetadataGenerationPointer pointer) => new(
        AssetMetadataStorageMode.ExternalGeneration,
        pointer.Revision,
        pointer.GenerationId);
}
