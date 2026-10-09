using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class PlaylistPlanningSnapshotTests
{
    [Fact]
    public async Task Capture_IsDeterministicForUnchangedInputsAndIgnoresMediaMtimeInInventoryIdentity()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        IPlaylistPlanningSnapshotService service = fixture.CreateSnapshotService();
        PlaylistPlanningSnapshotRequest request = CreateRequest(fixture);

        RollingPlanningInputSnapshot first = await service.CaptureAsync(request, CancellationToken.None);
        foreach (RollingAssetReadinessSnapshot asset in first.AssetReadiness!)
        {
            string mediaPath = Path.Combine(
                fixture.LibraryRoot,
                asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            File.SetLastWriteTimeUtc(mediaPath, asset.MediaLastWriteUtc.UtcDateTime.AddMinutes(1));
        }

        RollingPlanningInputSnapshot second = await service.CaptureAsync(request, CancellationToken.None);

        Assert.Equal(first.CatalogSnapshotHash, second.CatalogSnapshotHash);
        Assert.Equal(first.ProgrammingSnapshotHash, second.ProgrammingSnapshotHash);
        Assert.Equal(first.InventorySnapshotHash, second.InventorySnapshotHash);
        Assert.NotEqual(
            first.AssetReadiness.Select(asset => asset.MediaLastWriteUtc).ToArray(),
            second.AssetReadiness!.Select(asset => asset.MediaLastWriteUtc).ToArray());
    }

    [Fact]
    public async Task Capture_ChangesCatalogHashWhenExactCatalogChanges()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        IPlaylistPlanningSnapshotService service = fixture.CreateSnapshotService();
        RollingPlanningInputSnapshot before = await service.CaptureAsync(CreateRequest(fixture), CancellationToken.None);

        await fixture.AddSongAssetAsync("added-family", "added-asset", AssetTypes.MusicVideo, 900);
        RollingPlanningInputSnapshot after = await service.CaptureAsync(CreateRequest(fixture), CancellationToken.None);

        Assert.NotEqual(before.CatalogSnapshotHash, after.CatalogSnapshotHash);
        Assert.NotEqual(before.InventorySnapshotHash, after.InventorySnapshotHash);
    }

    [Fact]
    public async Task Capture_ChangesProgrammingHashButNotCatalogOrInventoryForPolicyOnlyChange()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        IPlaylistPlanningSnapshotService service = fixture.CreateSnapshotService();
        RollingPlanningInputSnapshot before = await service.CaptureAsync(CreateRequest(fixture), CancellationToken.None);
        ProgrammingConfiguration configuration = new ProgrammingConfigurationStore().Load(fixture.ProgrammingPath);
        await new ProgrammingConfigurationStore().WriteAsync(
            fixture.ProgrammingPath,
            configuration with { Revision = configuration.Revision + 1 },
            CancellationToken.None);

        RollingPlanningInputSnapshot after = await service.CaptureAsync(CreateRequest(fixture), CancellationToken.None);

        Assert.Equal(before.CatalogSnapshotHash, after.CatalogSnapshotHash);
        Assert.Equal(before.InventorySnapshotHash, after.InventorySnapshotHash);
        Assert.NotEqual(before.ProgrammingSnapshotHash, after.ProgrammingSnapshotHash);
    }

    [Fact]
    public async Task Generate_SameFrozenSnapshotProducesIdenticalScheduleAndHistory()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        IPlaylistPlanningSnapshotService service = fixture.CreateSnapshotService();
        RollingPlanningInputSnapshot snapshot = await service.CaptureAsync(CreateRequest(fixture), CancellationToken.None);

        PlaylistGenerationResult first = service.Generate(snapshot);
        PlaylistGenerationResult retry = service.Generate(snapshot);

        Assert.Equal(
            RollingProgrammingJson.SerializeCanonical(first.Playlist),
            RollingProgrammingJson.SerializeCanonical(retry.Playlist));
        Assert.Equal(
            RollingProgrammingJson.SerializeCanonical(first.UpdatedHistory),
            RollingProgrammingJson.SerializeCanonical(retry.UpdatedHistory));
    }

    [Fact]
    public async Task Generate_PreservesImportedHistoryScheduleStartAndContext()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        IPlaylistPlanningSnapshotService service = fixture.CreateSnapshotService();
        DateTimeOffset historyEnd = fixture.Time.GetUtcNow().AddHours(8);
        var history = new PlaylistHistoryDocument
        {
            ScheduleEndUtc = historyEnd,
            Plays =
            [
                new PlaylistHistoryEntry("asset-01", "song-01", AssetTypes.LyricVideo,
                    historyEnd.AddMinutes(-10), 1201),
                new PlaylistHistoryEntry("vlog-a", null, AssetTypes.Vlog,
                    historyEnd.AddMinutes(-5), 420),
                new PlaylistHistoryEntry("asset-short", "song-short", AssetTypes.ShortForm,
                    historyEnd.AddMinutes(-1), 30),
            ],
        };
        PlaylistPlanningSnapshotRequest request = CreateRequest(fixture) with { HistoryBefore = history };

        RollingPlanningInputSnapshot snapshot = await service.CaptureAsync(request, CancellationToken.None);
        PlaylistGenerationResult result = service.Generate(snapshot);

        Assert.Equal(historyEnd, snapshot.PlannedScheduleStartUtc);
        Assert.Equal(historyEnd, result.Playlist.ScheduleStartUtc);
        Assert.Equal(history.Plays, snapshot.HistoryBefore!.Plays);
        Assert.True(result.UpdatedHistory.ScheduleEndUtc > historyEnd);
    }

    [Fact]
    public async Task VerifyReadiness_RejectsSidecarMutationAfterSnapshot()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        IPlaylistPlanningSnapshotService service = fixture.CreateSnapshotService();
        RollingPlanningInputSnapshot snapshot = await service.CaptureAsync(CreateRequest(fixture), CancellationToken.None);
        PlaylistGenerationResult generated = service.Generate(snapshot);
        PlaylistItem selected = generated.Playlist.Items[0];
        string mediaPath = Path.Combine(
            fixture.LibraryRoot,
            selected.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        await File.AppendAllTextAsync(SourceManifestStore.GetManifestPath(mediaPath), "changed");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifySelectedReadinessAsync(
            snapshot,
            generated.Playlist,
            fixture.LibraryRoot,
            CancellationToken.None));
    }

    [Fact]
    public async Task Capture_IsPortableAcrossDifferentAbsoluteMediaRoots()
    {
        using RollingLibraryFixture firstFixture = await RollingLibraryFixture.CreateAsync(programming: true);
        using RollingLibraryFixture secondFixture = await RollingLibraryFixture.CreateAsync(programming: true);

        RollingPlanningInputSnapshot first = await firstFixture.CreateSnapshotService().CaptureAsync(
            CreateRequest(firstFixture),
            CancellationToken.None);
        RollingPlanningInputSnapshot second = await secondFixture.CreateSnapshotService().CaptureAsync(
            CreateRequest(secondFixture),
            CancellationToken.None);

        Assert.NotEqual(firstFixture.Root, secondFixture.Root);
        Assert.Equal(first.CatalogSnapshotHash, second.CatalogSnapshotHash);
        Assert.Equal(first.ProgrammingSnapshotHash, second.ProgrammingSnapshotHash);
        Assert.Equal(first.InventorySnapshotHash, second.InventorySnapshotHash);
        Assert.DoesNotContain(firstFixture.Root, RollingProgrammingJson.Serialize(first), StringComparison.Ordinal);
        Assert.DoesNotContain(secondFixture.Root, RollingProgrammingJson.Serialize(second), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_ExternalGenerationRecordsIdentityAndUsesMetadataContentHashes()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        string metadataRoot = Path.Combine(fixture.Root, "external-metadata");
        var repository = new ExternalAssetMetadataGenerationStore(metadataRoot);
        await CopyAdjacentGenerationAsync(fixture, repository, "000000000001", 1);
        await repository.PublishCurrentAsync("000000000001", 0, CancellationToken.None);
        IPlaylistPlanningSnapshotService service = CreateExternalSnapshotService(repository);

        RollingPlanningInputSnapshot snapshot = await service.CaptureAsync(
            CreateRequest(fixture),
            CancellationToken.None);

        Assert.Equal("000000000001", snapshot.AssetMetadataGenerationId);
        Assert.Equal(1, snapshot.AssetMetadataRevision);
        Assert.All(snapshot.AssetReadiness!, value => Assert.Matches("^[0-9a-f]{64}$", value.ProgrammingMetadataSha256));
        Assert.DoesNotContain(metadataRoot, RollingProgrammingJson.Serialize(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_PinsOldGenerationWhenCurrentPointerChangesAndLaterCaptureSeesNewGeneration()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        var repository = new ExternalAssetMetadataGenerationStore(Path.Combine(fixture.Root, "external-metadata"));
        await CopyAdjacentGenerationAsync(fixture, repository, "000000000001", 1);
        await repository.PublishCurrentAsync("000000000001", 0, CancellationToken.None);
        await CopyAdjacentGenerationAsync(
            fixture,
            repository,
            "000000000002",
            2,
            metadata => string.Equals(metadata.AssetId, "asset-01", StringComparison.Ordinal)
                ? CopyMetadata(metadata, "Generation Two Title")
                : metadata);
        var innerLoader = new PlaylistLibraryLoader(
            new ConstantAnalyzer(),
            metadataRepository: repository);
        var switchingLoader = new SwitchingLoader(innerLoader, repository, "000000000002", 1);
        var firstService = new PlaylistPlanningSnapshotService(
            new SongCatalogStore(),
            switchingLoader,
            new PlaylistGenerator(),
            metadataRepository: repository);

        RollingPlanningInputSnapshot first = await firstService.CaptureAsync(
            CreateRequest(fixture),
            CancellationToken.None);
        PlaylistGenerationResult generated = firstService.Generate(first);
        await firstService.VerifySelectedReadinessAsync(
            first,
            generated.Playlist,
            fixture.LibraryRoot,
            CancellationToken.None);
        RollingPlanningInputSnapshot later = await CreateExternalSnapshotService(repository).CaptureAsync(
            CreateRequest(fixture),
            CancellationToken.None);

        Assert.Equal("000000000001", first.AssetMetadataGenerationId);
        Assert.Equal(1, first.AssetMetadataRevision);
        Assert.Equal("asset-01", first.EligibleAssets!.Single(asset => asset.AssetId == "asset-01").Title);
        Assert.Equal("000000000002", later.AssetMetadataGenerationId);
        Assert.Equal(2, later.AssetMetadataRevision);
        Assert.Equal(
            "Generation Two Title",
            later.EligibleAssets!.Single(asset => asset.AssetId == "asset-01").Title);
    }

    [Fact]
    public async Task Capture_AdjacentSnapshotOmitsGenerationFieldsAndRemainsBackwardCompatible()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingPlanningInputSnapshot snapshot = await fixture.CreateSnapshotService().CaptureAsync(
            CreateRequest(fixture),
            CancellationToken.None);
        string json = RollingProgrammingJson.Serialize(snapshot);
        RollingPlanningInputSnapshot roundTrip = RollingProgrammingJson.Deserialize<RollingPlanningInputSnapshot>(
            json,
            "v0.7-compatible planning snapshot");

        Assert.Null(snapshot.AssetMetadataGenerationId);
        Assert.Null(snapshot.AssetMetadataRevision);
        Assert.DoesNotContain("assetMetadataGenerationId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("assetMetadataRevision", json, StringComparison.Ordinal);
        Assert.Null(roundTrip.AssetMetadataGenerationId);
        Assert.Null(roundTrip.AssetMetadataRevision);
        PlaylistPlanningSnapshotService.ValidateSnapshot(roundTrip, requireReadiness: true);
    }

    [Fact]
    public async Task LegacyAdjacentSnapshot_PreservesAuthorityAfterExternalCurrentAdvances()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        IPlaylistPlanningSnapshotService adjacentService = fixture.CreateSnapshotService();
        RollingPlanningInputSnapshot legacy = await adjacentService.CaptureAsync(
            CreateRequest(fixture),
            CancellationToken.None);
        PlaylistGenerationResult generated = adjacentService.Generate(legacy);
        await adjacentService.VerifySelectedReadinessAsync(
            legacy,
            generated.Playlist,
            fixture.LibraryRoot,
            CancellationToken.None);

        var repository = new ExternalAssetMetadataGenerationStore(
            Path.Combine(fixture.Root, "external-metadata"));
        await CopyAdjacentGenerationAsync(
            fixture,
            repository,
            "000000000001",
            1,
            metadata => CopyMetadata(metadata, "Generation One"));
        await repository.PublishCurrentAsync("000000000001", 0, CancellationToken.None);
        await CopyAdjacentGenerationAsync(
            fixture,
            repository,
            "000000000002",
            2,
            metadata => CopyMetadata(metadata, "Generation Two"));
        await repository.PublishCurrentAsync("000000000002", 1, CancellationToken.None);
        IPlaylistPlanningSnapshotService externalService = CreateExternalSnapshotService(repository);

        await externalService.VerifySelectedReadinessAsync(
            legacy,
            generated.Playlist,
            fixture.LibraryRoot,
            CancellationToken.None);

        PlaylistItem selected = generated.Playlist.Items[0];
        string selectedMediaPath = Path.Combine(
            fixture.LibraryRoot,
            selected.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Delete(AssetMetadataStore.GetMetadataPath(selectedMediaPath));
        await Assert.ThrowsAsync<InvalidDataException>(() => externalService.VerifySelectedReadinessAsync(
            legacy,
            generated.Playlist,
            fixture.LibraryRoot,
            CancellationToken.None));
    }

    [Fact]
    public async Task Capture_ExternalMetadataHashIsIndependentOfExternalRootPath()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        var firstRepository = new ExternalAssetMetadataGenerationStore(Path.Combine(fixture.Root, "metadata-a"));
        var secondRepository = new ExternalAssetMetadataGenerationStore(Path.Combine(fixture.Root, "metadata-b"));
        await CopyAdjacentGenerationAsync(fixture, firstRepository, "000000000001", 1);
        await CopyAdjacentGenerationAsync(fixture, secondRepository, "000000000001", 1);
        await firstRepository.PublishCurrentAsync("000000000001", 0, CancellationToken.None);
        await secondRepository.PublishCurrentAsync("000000000001", 0, CancellationToken.None);

        RollingPlanningInputSnapshot first = await CreateExternalSnapshotService(firstRepository).CaptureAsync(
            CreateRequest(fixture),
            CancellationToken.None);
        RollingPlanningInputSnapshot second = await CreateExternalSnapshotService(secondRepository).CaptureAsync(
            CreateRequest(fixture),
            CancellationToken.None);

        Assert.Equal(first.InventorySnapshotHash, second.InventorySnapshotHash);
        Assert.Equal(
            first.AssetReadiness!.Select(value => value.ProgrammingMetadataSha256),
            second.AssetReadiness!.Select(value => value.ProgrammingMetadataSha256));
    }

    private static IPlaylistPlanningSnapshotService CreateExternalSnapshotService(
        IAssetMetadataRepository repository) =>
        new PlaylistPlanningSnapshotService(
            new SongCatalogStore(),
            new PlaylistLibraryLoader(new ConstantAnalyzer(), metadataRepository: repository),
            new PlaylistGenerator(),
            metadataRepository: repository);

    private static async Task CopyAdjacentGenerationAsync(
        RollingLibraryFixture fixture,
        ExternalAssetMetadataGenerationStore repository,
        string generationId,
        long revision,
        Func<AssetMetadata, AssetMetadata>? transform = null)
    {
        IAssetMetadataSnapshot adjacent = new AdjacentAssetMetadataRepository().Pin(
            AssetMetadataTree.Library,
            fixture.LibraryRoot);
        AssetMetadataGenerationRecord[] records = adjacent.DiscoverRelativeMediaPaths()
            .Select(relative => new AssetMetadataGenerationRecord(
                AssetMetadataTree.Library,
                relative,
                transform?.Invoke(adjacent.Read(relative).Metadata) ?? adjacent.Read(relative).Metadata))
            .ToArray();
        await repository.CreateGenerationAsync(
            generationId,
            revision,
            records,
            CancellationToken.None);
    }

    private static AssetMetadata CopyMetadata(AssetMetadata value, string title) => new()
    {
        SchemaVersion = value.SchemaVersion,
        AssetId = value.AssetId,
        ContentGroupId = value.ContentGroupId,
        Title = title,
        Artist = value.Artist,
        Type = value.Type,
        Subtype = value.Subtype,
        RotationStartDate = value.RotationStartDate,
        Enabled = value.Enabled,
        SeriesId = value.SeriesId,
        EpisodeNumber = value.EpisodeNumber,
        Tags = value.Tags is null ? null : [.. value.Tags],
    };

    private sealed class ConstantAnalyzer : IMediaAnalyzer
    {
        public Task<MediaDescription> InspectAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaDescription(
                filePath,
                TimeSpan.FromMinutes(4),
                "mov,mp4",
                1,
                null,
                null,
                []));

        public Task<MediaDescription> AnalyzeForVerificationAsync(
            string filePath,
            CancellationToken cancellationToken) => InspectAsync(filePath, cancellationToken);
    }

    private sealed class SwitchingLoader(
        IPlaylistLibraryLoader inner,
        ExternalAssetMetadataGenerationStore repository,
        string generationId,
        long expectedRevision) : IPlaylistLibraryLoader
    {
        private int _switched;

        public Task<PlaylistLibrarySnapshot> LoadAsync(
            string libraryRoot,
            SongCatalog catalog,
            CancellationToken cancellationToken) =>
            inner.LoadAsync(libraryRoot, catalog, cancellationToken);

        public async Task<PlaylistLibrarySnapshot> LoadAsync(
            string libraryRoot,
            SongCatalog catalog,
            IAssetMetadataSnapshot metadataSnapshot,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _switched, 1) == 0)
            {
                await repository.PublishCurrentAsync(
                    generationId,
                    expectedRevision,
                    cancellationToken);
            }

            return await inner.LoadAsync(
                libraryRoot,
                catalog,
                metadataSnapshot,
                cancellationToken);
        }
    }

    private static PlaylistPlanningSnapshotRequest CreateRequest(RollingLibraryFixture fixture) => new(
        fixture.LibraryRoot,
        fixture.CatalogPath,
        PlaylistHistoryDocument.Empty,
        TimeSpan.FromHours(6),
        Seed: 20260930,
        GeneratedAtUtc: fixture.Time.GetUtcNow(),
        Sequence: 1,
        CaptureReadiness: true,
        HistoryBeforeHash: new string('a', 64));
}
