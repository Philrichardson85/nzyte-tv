using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class RollingExternalMetadataActivationTests
{
    [Fact]
    public async Task Cutover_PreservesLegacyAdjacentAuthorityAndPinsNewExternalSnapshots()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner adjacentPlanner = fixture.CreatePlanner();
        await fixture.InitializeAsync(adjacentPlanner);
        RollingMaintainResult beforeCutover = await adjacentPlanner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        RollingCommittedBlock legacyBlock = Assert.Single(beforeCutover.Manifest.Blocks!);
        Assert.Null(fixture.ReadInput(legacyBlock).AssetMetadataGenerationId);
        Assert.Null(fixture.ReadInput(legacyBlock).AssetMetadataRevision);

        string metadataRoot = Path.Combine(fixture.Root, "external-metadata");
        var configuration = new RollingStationConfiguration
        {
            StationConfigPath = Path.Combine(fixture.Root, "station.json"),
            PlannerId = beforeCutover.Manifest.PlannerId!,
            RollingStatePath = Path.Combine(fixture.Root, "rolling-state.json"),
            AssetMetadataStorage = new RollingAssetMetadataStorageConfiguration
            {
                Mode = RollingAssetMetadataStorageMode.ExternalGeneration,
                ExternalRoot = metadataRoot,
            },
        };
        IAssetMetadataRepository repository =
            RollingStationMetadataRepositoryFactory.Create(configuration);
        var externalStore = Assert.IsType<ExternalAssetMetadataGenerationStore>(repository);
        await CreateGenerationAsync(fixture, externalStore, "000000000001", 1);
        await externalStore.PublishCurrentAsync("000000000001", 0, CancellationToken.None);

        var maintainer = new RollingBlockMaintainer(
            mediaToolLocator: new FixedMediaToolLocator(),
            snapshotServiceFactory: _ => fixture.CreateSnapshotService(repository),
            metadataRepository: repository);
        RollingMaintainResult firstExternal = await maintainer.EnsureCommittedThroughAsync(
            fixture.Root,
            requiredHighestSequence: 2,
            CancellationToken.None);
        Assert.Null(fixture.ReadInput(firstExternal.Manifest.Blocks![0]).AssetMetadataGenerationId);
        Assert.Equal(
            "000000000001",
            fixture.ReadInput(firstExternal.Manifest.Blocks[1]).AssetMetadataGenerationId);
        Assert.Equal(1, fixture.ReadInput(firstExternal.Manifest.Blocks[1]).AssetMetadataRevision);

        await CreateGenerationAsync(fixture, externalStore, "000000000002", 2);
        await externalStore.PublishCurrentAsync("000000000002", 1, CancellationToken.None);
        var resolver = new RollingCommittedBlockResolver(metadataRepository: repository);
        ResolvedRollingCommittedBlock resolvedLegacy = resolver.ResolveManifestBlock(
            firstExternal.Paths,
            firstExternal.Manifest,
            1,
            fixture.LibraryRoot);
        ResolvedRollingCommittedBlock resolvedFirstExternal = resolver.ResolveManifestBlock(
            firstExternal.Paths,
            firstExternal.Manifest,
            2,
            fixture.LibraryRoot);
        Assert.Null(resolvedLegacy.InputSnapshot.AssetMetadataGenerationId);
        Assert.Equal("000000000001", resolvedFirstExternal.InputSnapshot.AssetMetadataGenerationId);

        RollingMaintainResult secondExternal = await maintainer.EnsureCommittedThroughAsync(
            fixture.Root,
            requiredHighestSequence: 3,
            CancellationToken.None);
        Assert.Equal(
            "000000000002",
            fixture.ReadInput(secondExternal.Manifest.Blocks![2]).AssetMetadataGenerationId);
        Assert.Equal(2, fixture.ReadInput(secondExternal.Manifest.Blocks[2]).AssetMetadataRevision);
    }

    [Fact]
    public async Task ExplicitExternalMode_MissingCurrentPointerFailsClosedWithoutAdjacentFallback()
    {
        using var root = new TemporaryDirectory();
        string libraryRoot = Directory.CreateDirectory(Path.Combine(root.Path, "library")).FullName;
        string mediaPath = Path.Combine(libraryRoot, "Music Videos", "example.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
        File.WriteAllText(mediaPath, "media");
        await new AssetMetadataStore().WriteAsync(
            mediaPath,
            new AssetMetadata
            {
                AssetId = "example",
                Title = "Example",
                Type = AssetTypes.MusicVideo,
                Enabled = true,
                Tags = [],
            },
            CancellationToken.None);
        var configuration = new RollingStationConfiguration
        {
            AssetMetadataStorage = new RollingAssetMetadataStorageConfiguration
            {
                Mode = RollingAssetMetadataStorageMode.ExternalGeneration,
                ExternalRoot = Path.Combine(root.Path, "external"),
            },
        };

        IAssetMetadataRepository repository =
            RollingStationMetadataRepositoryFactory.Create(configuration);

        Assert.Throws<FileNotFoundException>(() =>
            repository.Pin(AssetMetadataTree.Library, libraryRoot));
    }

    private static async Task CreateGenerationAsync(
        RollingLibraryFixture fixture,
        ExternalAssetMetadataGenerationStore store,
        string generationId,
        long revision)
    {
        var metadataStore = new AssetMetadataStore();
        AssetMetadataGenerationRecord[] records = Directory.EnumerateFiles(
                fixture.LibraryRoot,
                "*.mp4",
                SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new AssetMetadataGenerationRecord(
                AssetMetadataTree.Library,
                Path.GetRelativePath(fixture.LibraryRoot, path).Replace('\\', '/'),
                metadataStore.Read(path),
                File.ReadAllBytes(AssetMetadataStore.GetMetadataPath(path))))
            .ToArray();
        await store.CreateGenerationAsync(
            generationId,
            revision,
            records,
            CancellationToken.None);
    }

    private sealed class FixedMediaToolLocator : IMediaToolLocator
    {
        public Task<MediaToolPaths> LocateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MediaToolPaths("ffmpeg", "ffprobe"));

        public Task<string> LocateFfmpegAsync(CancellationToken cancellationToken) =>
            Task.FromResult("ffmpeg");

        public Task<string> LocateFfprobeAsync(CancellationToken cancellationToken) =>
            Task.FromResult("ffprobe");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() =>
            Path = Directory.CreateTempSubdirectory("nzytetv-external-activation-").FullName;

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
