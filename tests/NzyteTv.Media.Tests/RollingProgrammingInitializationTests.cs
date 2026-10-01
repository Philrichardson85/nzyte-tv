using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class RollingProgrammingInitializationTests
{
    [Fact]
    public async Task Initialize_EmptyGenesisCreatesVersionedPortableManifest()
    {
        using var fixture = new EmptyRollingFixture();
        RollingProgrammingPlanner planner = fixture.CreatePlanner(baseSeed: 777);

        RollingInitializationResult result = await planner.InitializeAsync(
            new RollingInitializationRequest(fixture.Root, BaseSeed: 1234),
            CancellationToken.None);

        Assert.True(result.Created);
        Assert.False(result.ImportedHistory);
        Assert.Equal(RollingProgrammingManifest.CurrentSchemaVersion, result.Manifest.SchemaVersion);
        Assert.Equal(1234, result.Manifest.BaseSeed);
        Assert.Equal(3, result.Manifest.TargetPreparedBlockCount);
        Assert.Equal(21600, result.Manifest.TargetBlockDurationSeconds);
        Assert.Empty(result.Manifest.Blocks!);
        Assert.Equal(result.Manifest.GenesisHistory, result.Manifest.HistoryHead);
        Assert.Equal(1, result.Manifest.NextSequence);
        Assert.StartsWith("history/", result.Manifest.GenesisHistory!.RelativePath, StringComparison.Ordinal);
        Assert.True(File.Exists(result.Paths.ManifestPath));
        Assert.True(File.Exists(RollingPathSafety.ResolveExistingFile(
            result.Paths.RollingRoot,
            result.Manifest.GenesisHistory.RelativePath)));
    }

    [Fact]
    public async Task Manifest_SchemaRoundTripsAndValidates()
    {
        using var fixture = new EmptyRollingFixture();
        RollingInitializationResult initialized = await fixture.CreatePlanner().InitializeAsync(
            new RollingInitializationRequest(fixture.Root, BaseSeed: 9),
            CancellationToken.None);

        string json = File.ReadAllText(initialized.Paths.ManifestPath);
        RollingProgrammingManifest roundTrip = RollingProgrammingJson.Deserialize<RollingProgrammingManifest>(
            json,
            "test manifest");

        RollingManifestValidator.Validate(roundTrip);
        Assert.Equal(initialized.Manifest.PlannerId, roundTrip.PlannerId);
        Assert.Equal(initialized.Manifest.GenesisHistory, roundTrip.GenesisHistory);
    }

    [Fact]
    public async Task Initialize_ImportsExactLogicalPlannedHistory()
    {
        using var fixture = new EmptyRollingFixture();
        string importPath = Path.Combine(fixture.Root, "planned-history.json");
        var history = new PlaylistHistoryDocument
        {
            ScheduleEndUtc = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
            Plays =
            [
                new PlaylistHistoryEntry("asset-a", "song-a", AssetTypes.MusicVideo,
                    new DateTimeOffset(2026, 9, 30, 11, 55, 0, TimeSpan.Zero), 300),
            ],
        };
        await new PlaylistHistoryStore().WriteAsync(importPath, history, CancellationToken.None);

        RollingInitializationResult result = await fixture.CreatePlanner().InitializeAsync(
            new RollingInitializationRequest(fixture.Root, importPath, 5),
            CancellationToken.None);
        string snapshotPath = RollingPathSafety.ResolveExistingFile(
            result.Paths.RollingRoot,
            result.Manifest.GenesisHistory!.RelativePath);
        PlaylistHistoryDocument imported = new PlaylistHistoryStore().Load(snapshotPath);

        Assert.True(result.ImportedHistory);
        Assert.Equal(history.ScheduleEndUtc, imported.ScheduleEndUtc);
        Assert.Equal(history.Plays, imported.Plays);
    }

    [Fact]
    public async Task Initialize_IsIdempotentForSameExplicitGenesisAndSeed()
    {
        using var fixture = new EmptyRollingFixture();
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        var request = new RollingInitializationRequest(fixture.Root, BaseSeed: 91);

        RollingInitializationResult first = await planner.InitializeAsync(request, CancellationToken.None);
        DateTime manifestWrite = File.GetLastWriteTimeUtc(first.Paths.ManifestPath);
        RollingInitializationResult second = await planner.InitializeAsync(request, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Manifest.PlannerId, second.Manifest.PlannerId);
        Assert.Equal(manifestWrite, File.GetLastWriteTimeUtc(first.Paths.ManifestPath));
    }

    [Fact]
    public async Task Initialize_OmittedBaseSeedIsGeneratedAndPersistedOnlyOnce()
    {
        using var fixture = new EmptyRollingFixture();
        int calls = 0;
        var planner = new RollingProgrammingPlanner(
            timeProvider: fixture.Time,
            idFactory: fixture.NextId,
            baseSeedFactory: () =>
            {
                calls++;
                return 7654321;
            });

        RollingInitializationResult first = await planner.InitializeAsync(
            new RollingInitializationRequest(fixture.Root),
            CancellationToken.None);
        RollingInitializationResult second = await planner.InitializeAsync(
            new RollingInitializationRequest(fixture.Root),
            CancellationToken.None);

        Assert.Equal(7654321, first.Manifest.BaseSeed);
        Assert.Equal(first.Manifest.BaseSeed, second.Manifest.BaseSeed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Initialize_RefusesSeedChangeWithoutOverwritingManifest()
    {
        using var fixture = new EmptyRollingFixture();
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        RollingInitializationResult first = await planner.InitializeAsync(
            new RollingInitializationRequest(fixture.Root, BaseSeed: 1),
            CancellationToken.None);
        string before = File.ReadAllText(first.Paths.ManifestPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => planner.InitializeAsync(
            new RollingInitializationRequest(fixture.Root, BaseSeed: 2),
            CancellationToken.None));

        Assert.Equal(before, File.ReadAllText(first.Paths.ManifestPath));
    }

    [Fact]
    public async Task Initialize_RefusesGenesisChangeWithoutOverwritingManifest()
    {
        using var fixture = new EmptyRollingFixture();
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        RollingInitializationResult first = await planner.InitializeAsync(
            new RollingInitializationRequest(fixture.Root, BaseSeed: 1),
            CancellationToken.None);
        string importPath = Path.Combine(fixture.Root, "other-history.json");
        await new PlaylistHistoryStore().WriteAsync(
            importPath,
            new PlaylistHistoryDocument
            {
                ScheduleEndUtc = fixture.Time.GetUtcNow(),
                Plays = [new PlaylistHistoryEntry("x", "song-x", AssetTypes.MusicVideo, fixture.Time.GetUtcNow())],
            },
            CancellationToken.None);
        string before = File.ReadAllText(first.Paths.ManifestPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => planner.InitializeAsync(
            new RollingInitializationRequest(fixture.Root, importPath, 1),
            CancellationToken.None));

        Assert.Equal(before, File.ReadAllText(first.Paths.ManifestPath));
    }

    [Fact]
    public async Task Initialize_ExplicitMissingHistoryIsRejectedRatherThanTreatedAsEmpty()
    {
        using var fixture = new EmptyRollingFixture();

        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.CreatePlanner().InitializeAsync(
            new RollingInitializationRequest(fixture.Root, Path.Combine(fixture.Root, "missing.json"), 1),
            CancellationToken.None));
    }

    [Fact]
    public async Task InitializedArtifactsContainNoDestinationOrRuntimeState()
    {
        using var fixture = new EmptyRollingFixture();
        RollingInitializationResult result = await fixture.CreatePlanner().InitializeAsync(
            new RollingInitializationRequest(fixture.Root, BaseSeed: 1),
            CancellationToken.None);

        string all = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(result.Paths.RollingRoot, "*.json", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.DoesNotContain("rtmp://", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rtmps://", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NZYTE_TV_RTMP_URL", all, StringComparison.Ordinal);
        Assert.DoesNotContain("stationPid", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resumeGlobalIndex", all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Initialize_RejectsSecretLikeImportedHistoryBeforeWritingRollingJson()
    {
        using var fixture = new EmptyRollingFixture();
        string importPath = Path.Combine(fixture.Root, "unsafe-history.json");
        await new PlaylistHistoryStore().WriteAsync(
            importPath,
            new PlaylistHistoryDocument
            {
                Plays =
                [
                    new PlaylistHistoryEntry(
                        "rtmps://example.invalid/live2/FAKE-SECRET",
                        null,
                        AssetTypes.Vlog,
                        fixture.Time.GetUtcNow()),
                ],
            },
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreatePlanner().InitializeAsync(
            new RollingInitializationRequest(fixture.Root, importPath, 1),
            CancellationToken.None));

        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(fixture.Root);
        Assert.False(File.Exists(paths.ManifestPath));
        Assert.Empty(Directory.EnumerateFiles(paths.RollingRoot, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void PlannerLock_RejectsConcurrentWriterButStaleFilenameDoesNotBlock()
    {
        using var fixture = new EmptyRollingFixture();
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(fixture.Root);
        paths.EnsureDirectories();
        File.WriteAllText(paths.PlannerLockPath, "stale non-authoritative marker");
        var provider = new RollingPlannerLockProvider();

        using IRollingPlannerLock first = provider.Acquire(paths.PlannerLockPath);
        Assert.Throws<InvalidOperationException>(() => provider.Acquire(paths.PlannerLockPath));
        first.Dispose();
        using IRollingPlannerLock afterRelease = provider.Acquire(paths.PlannerLockPath);
    }

    private sealed class EmptyRollingFixture : IDisposable
    {
        private int _nextId;

        public EmptyRollingFixture()
        {
            Root = Directory.CreateTempSubdirectory("nzytetv-rolling-init-").FullName;
        }

        public string Root { get; }

        public TimeProvider Time { get; } = new FixedTimeProvider(
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

        public string NextId() => (++_nextId).ToString("x32");

        public RollingProgrammingPlanner CreatePlanner(int baseSeed = 333) => new(
            timeProvider: Time,
            idFactory: NextId,
            baseSeedFactory: () => baseSeed);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
