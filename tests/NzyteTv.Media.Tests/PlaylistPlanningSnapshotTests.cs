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
