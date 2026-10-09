using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class RollingProgrammingPlannerTests
{
    [Fact]
    public async Task Maintain_ProducesExactlyThreeImmutableContiguousBlocks()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);

        RollingMaintainResult result = await planner.MaintainAsync(fixture.Root, CancellationToken.None);

        Assert.True(result.TargetSatisfied);
        Assert.Equal(3, result.GeneratedBlockCount);
        Assert.Equal(0, result.AdoptedBlockCount);
        IReadOnlyList<RollingCommittedBlock> blocks = result.Manifest.Blocks!;
        Assert.Equal([1L, 2L, 3L], blocks.Select(block => block.Sequence));
        Assert.Equal(4, result.Manifest.NextSequence);
        Assert.Equal(blocks[^1].HistoryAfter, result.Manifest.HistoryHead);
        Assert.All(blocks, block =>
        {
            Assert.Equal(21600, block.TargetDurationSeconds);
            Assert.True(block.ActualDurationSeconds >= block.TargetDurationSeconds);
            Assert.True(block.ItemCount > 0);
            Assert.True(File.Exists(RollingPathSafety.ResolveExistingFile(
                result.Paths.RollingRoot,
                block.PlaylistPath!)));
        });
        Assert.Null(blocks[0].ParentBlockId);
        Assert.Equal(blocks[0].BlockId, blocks[1].ParentBlockId);
        Assert.Equal(blocks[1].BlockId, blocks[2].ParentBlockId);
        Assert.True(planner.Validate(fixture.Root).IsValid);
    }

    [Fact]
    public async Task Maintain_TestDurationProducesThreeFullyCommittedResolvableBlocks()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        RollingInitializationResult initialized = await fixture.InitializeAsync(
            planner,
            TimeSpan.FromMinutes(4));

        RollingMaintainResult result = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None);

        Assert.Equal(240, initialized.Manifest.TargetBlockDurationSeconds);
        Assert.Equal(3, result.GeneratedBlockCount);
        IReadOnlyList<RollingCommittedBlock> blocks = result.Manifest.Blocks!;
        Assert.Equal([1L, 2L, 3L], blocks.Select(block => block.Sequence));
        var store = new RollingPlanStore();
        var resolver = new RollingCommittedBlockResolver();
        for (int index = 0; index < blocks.Count; index++)
        {
            RollingCommittedBlock block = blocks[index];
            ResolvedRollingCommittedBlock resolved = resolver.ResolveManifestBlock(
                result.Paths,
                result.Manifest,
                block.Sequence,
                fixture.LibraryRoot);
            RollingPlanningInputSnapshot input = fixture.ReadInput(block);
            RollingBlockDescriptor descriptor = store.Read<RollingBlockDescriptor>(
                RollingPathSafety.ResolveExistingFile(result.Paths.RollingRoot, block.DescriptorPath!),
                $"test rolling block {block.Sequence} descriptor");

            Assert.Equal(240, block.TargetDurationSeconds);
            Assert.Equal(240, input.TargetDurationSeconds);
            Assert.Equal(240, descriptor.Block!.TargetDurationSeconds);
            Assert.Equal(240, resolved.Playlist.TargetDurationSeconds);
            Assert.True(resolved.Playlist.ActualDurationSeconds >= 240);
            Assert.Equal(block.ActualDurationSeconds, resolved.Playlist.ActualDurationSeconds);
            Assert.True(resolved.BroadcastPlan.IsReady);
            Assert.Equal(block.ItemCount, resolved.BroadcastPlan.Items.Count);
            Assert.Equal(
                block.BlockId,
                RollingBlockIdentity.Calculate(new RollingBlockIdentityInput(
                    result.Manifest.PlannerId!,
                    block.Sequence,
                    block.ParentBlockId,
                    block.Seed,
                    block.TargetDurationSeconds,
                    resolved.Playlist,
                    block.HistoryBefore!.Sha256,
                    block.HistoryAfter!.Sha256,
                    block.CatalogSnapshotHash!,
                    block.ProgrammingSnapshotHash!,
                    block.InventorySnapshotHash!,
                    block.PlannerAlgorithmVersion!)));

            if (index > 0)
            {
                RollingCommittedBlock previous = blocks[index - 1];
                Assert.Equal(previous.HistoryAfter, block.HistoryBefore);
                Assert.Equal(
                    fixture.ReadHistory(previous.HistoryAfter!).Plays,
                    input.HistoryBefore!.Plays);
            }
        }

        Assert.Equal(blocks[^1].HistoryAfter, result.Manifest.HistoryHead);
        Assert.True(planner.Validate(fixture.Root).IsValid);
        string artifacts = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(result.Paths.RollingRoot, "*.json", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.DoesNotContain("rtmp://", artifacts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rtmps://", artifacts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NZYTE_TV_RTMP_URL", artifacts, StringComparison.Ordinal);
        Assert.DoesNotContain("resumeGlobalIndex", artifacts, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Maintain_CarriesExactPlannedHistoryAcrossEveryBlockBoundary()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult result = await planner.MaintainAsync(fixture.Root, CancellationToken.None);

        for (int index = 0; index < result.Manifest.Blocks!.Count - 1; index++)
        {
            RollingCommittedBlock current = result.Manifest.Blocks[index];
            RollingCommittedBlock next = result.Manifest.Blocks[index + 1];
            Assert.Equal(current.HistoryAfter, next.HistoryBefore);

            PlaylistHistoryDocument after = fixture.ReadHistory(current.HistoryAfter!);
            RollingPlanningInputSnapshot nextInput = fixture.ReadInput(next);
            Assert.Equal(after.ScheduleEndUtc, nextInput.HistoryBefore!.ScheduleEndUtc);
            Assert.Equal(after.Plays, nextInput.HistoryBefore.Plays);
        }
    }

    [Fact]
    public async Task Maintain_HistoryBoundaryCarriesRepetitionPacingAndCadenceEntryTypes()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult result = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 2);
        PlaylistHistoryDocument boundary = fixture.ReadHistory(result.Manifest.Blocks![0].HistoryAfter!);
        RollingPlanningInputSnapshot next = fixture.ReadInput(result.Manifest.Blocks[1]);

        Assert.Equal(boundary.Plays, next.HistoryBefore!.Plays);
        Assert.Contains(boundary.Plays, play => AssetTypes.IsSongBased(play.Type));
        Assert.Contains(boundary.Plays, play => play.Type == AssetTypes.ShortForm);
        Assert.Contains(boundary.Plays, play => play.Type == AssetTypes.Vlog);
        Assert.Contains(boundary.Plays, play => play.Type == AssetTypes.Bumper);
        Assert.Contains(boundary.Plays, play => play.Type == AssetTypes.Promo);
        Assert.True(boundary.Plays.Count(play => !string.IsNullOrWhiteSpace(play.ContentGroupId)) >= 3);
    }

    [Fact]
    public async Task Maintain_DerivesDeterministicPerSequenceSeeds()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        RollingProgrammingManifest initialized = (await fixture.InitializeAsync(planner)).Manifest;

        RollingMaintainResult result = await planner.MaintainAsync(fixture.Root, CancellationToken.None);

        IReadOnlyList<RollingCommittedBlock> blocks = result.Manifest.Blocks!;
        Assert.Equal(
            blocks.Select(block => block.Sequence)
                .Select(sequence => RollingProgrammingSeed.Derive(
                    initialized.PlannerId!,
                    initialized.BaseSeed,
                    sequence)),
            blocks.Select(block => block.Seed));
        Assert.Equal(3, blocks.Select(block => block.Seed).Distinct().Count());
    }

    [Fact]
    public async Task Maintain_AfterTargetIsByteAndMtimeNoOpForCommittedArtifacts()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult first = await planner.MaintainAsync(fixture.Root, CancellationToken.None);
        Dictionary<string, (byte[] Bytes, DateTime Write)> before = Directory
            .EnumerateFiles(first.Paths.RollingRoot, "*.json", SearchOption.AllDirectories)
            .ToDictionary(
                path => path,
                path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)),
                GetPathComparer());

        RollingMaintainResult second = await planner.MaintainAsync(fixture.Root, CancellationToken.None);

        Assert.Equal(0, second.GeneratedBlockCount);
        Assert.Equal(0, second.AdoptedBlockCount);
        Assert.Equal(before.Keys.Order(), Directory
            .EnumerateFiles(first.Paths.RollingRoot, "*.json", SearchOption.AllDirectories).Order());
        foreach ((string path, (byte[] bytes, DateTime write)) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(write, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public async Task Maintain_TargetBelowCommittedCount_ReconcilesAndSucceedsWithoutRewriting()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult extended = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 4);
        Dictionary<string, (byte[] Bytes, DateTime Write)> before = Directory
            .EnumerateFiles(extended.Paths.RollingRoot, "*.json", SearchOption.AllDirectories)
            .ToDictionary(
                path => path,
                path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)),
                GetPathComparer());

        RollingMaintainResult result = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 2);

        Assert.True(result.TargetSatisfied);
        Assert.Equal(4, result.Manifest.Blocks!.Count);
        Assert.Equal(0, result.GeneratedBlockCount);
        Assert.Equal(0, result.AdoptedBlockCount);
        Assert.Equal(before.Keys.Order(), Directory
            .EnumerateFiles(extended.Paths.RollingRoot, "*.json", SearchOption.AllDirectories).Order());
        foreach ((string path, (byte[] bytes, DateTime write)) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(write, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public async Task Maintain_DefaultThreeBlockTarget_SucceedsAfterAutomaticExtension()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 4);

        RollingMaintainResult result = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None);

        Assert.True(result.TargetSatisfied);
        Assert.Equal(4, result.Manifest.Blocks!.Count);
        Assert.Equal(0, result.GeneratedBlockCount);
    }

    [Fact]
    public async Task Maintain_NegativeTarget_IsRejected()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: -1));
    }

    [Fact]
    public async Task Maintain_CommittedBlocksStayImmutableAndFourthUsesLatestProgrammingRevision()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult first = await planner.MaintainAsync(fixture.Root, CancellationToken.None);
        Dictionary<string, string> committed = fixture.CaptureCommittedJson(first.Manifest);
        ProgrammingConfiguration configuration = new ProgrammingConfigurationStore().Load(fixture.ProgrammingPath);
        await new ProgrammingConfigurationStore().WriteAsync(
            fixture.ProgrammingPath,
            configuration with { Revision = configuration.Revision + 1 },
            CancellationToken.None);

        RollingMaintainResult extended = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 4);

        Assert.Equal(1, extended.GeneratedBlockCount);
        Assert.Equal(configuration.Revision + 1, extended.Manifest.Blocks![3].ProgrammingRevision);
        Assert.Equal(committed, fixture.CaptureCommittedJson(first.Manifest));
    }

    [Fact]
    public async Task Maintain_FourthSnapshotSeesNewEligibleAssetWithoutRewritingEarlierBlocks()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult first = await planner.MaintainAsync(fixture.Root, CancellationToken.None);
        Dictionary<string, string> committed = fixture.CaptureCommittedJson(first.Manifest);
        await fixture.AddSongAssetAsync(
            "new-song",
            "new-song-video",
            AssetTypes.MusicVideo,
            900);

        RollingMaintainResult extended = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 4);
        RollingPlanningInputSnapshot fourth = fixture.ReadInput(extended.Manifest.Blocks![3]);

        Assert.Contains(fourth.EligibleAssets!, asset => asset.AssetId == "new-song-video");
        Assert.Equal(committed, fixture.CaptureCommittedJson(first.Manifest));
    }

    [Fact]
    public async Task Maintain_SnapshotsExactCatalogProgrammingAndSortedInventory()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult result = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        RollingPlanningInputSnapshot input = fixture.ReadInput(result.Manifest.Blocks!.Single());

        Assert.Equal(File.ReadAllText(fixture.CatalogPath), input.CatalogJson);
        Assert.Equal(File.ReadAllText(fixture.ProgrammingPath), input.ProgrammingJson);
        Assert.Equal(RollingProgrammingJson.Sha256File(fixture.CatalogPath), input.CatalogSnapshotHash);
        Assert.Equal(RollingProgrammingJson.Sha256File(fixture.ProgrammingPath), input.ProgrammingSnapshotHash);
        IReadOnlyList<PlaylistAsset> eligible = input.EligibleAssets!;
        Assert.Equal(
            eligible.OrderBy(asset => asset.RelativePath, StringComparer.Ordinal)
                .Select(asset => asset.RelativePath),
            eligible.Select(asset => asset.RelativePath));
        Assert.Equal(eligible.Count, input.AssetReadiness!.Count);
    }

    [Fact]
    public async Task Maintain_WithoutProgrammingConfigurationUsesExplicitLegacyMarker()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: false);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);

        RollingMaintainResult result = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        RollingPlanningInputSnapshot input = fixture.ReadInput(result.Manifest.Blocks!.Single());

        Assert.Null(input.ProgrammingConfiguration);
        Assert.Null(input.ProgrammingJson);
        Assert.Equal(
            RollingProgrammingPolicy.LegacyProgrammingSnapshotMarker,
            input.ProgrammingSnapshotHash);
        Assert.Null(result.Manifest.Blocks![0].ProgrammingRevision);
    }

    [Fact]
    public async Task Validate_EveryCommittedPlaylistPassesExistingBroadcastPlanner()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult result = await planner.MaintainAsync(fixture.Root, CancellationToken.None);
        var broadcastPlanner = new BroadcastPlanner();

        Assert.All(result.Manifest.Blocks!, block =>
        {
            string path = RollingPathSafety.ResolveExistingFile(
                result.Paths.RollingRoot,
                block.PlaylistPath!);
            BroadcastPlan plan = broadcastPlanner.CreatePlan([path], fixture.LibraryRoot);
            Assert.True(plan.IsReady);
            Assert.Equal(block.ItemCount, plan.Items.Count);
        });
    }

    [Fact]
    public async Task Status_ReportsLineageBufferRevisionAndNoExecutionClaim()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        await planner.MaintainAsync(fixture.Root, CancellationToken.None, committedBlockTarget: 1);

        RollingProgrammingStatus status = planner.GetStatus(fixture.Root);

        Assert.NotNull(status.Manifest);
        Assert.Single(status.Blocks);
        Assert.Equal(ProgrammingConfiguration.CreateDefault().Revision, status.LatestProgrammingRevision);
        Assert.True(status.PreparedActualDurationSeconds >= 21600);
        Assert.True(status.Validation.IsValid);
    }

    [Fact]
    public async Task RollingArtifactsContainNoDestinationSecretOrRuntimeCursor()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult result = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);

        string content = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(result.Paths.RollingRoot, "*.json", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.DoesNotContain("rtmp://", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rtmps://", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NZYTE_TV_RTMP_URL", content, StringComparison.Ordinal);
        Assert.DoesNotContain("stationPid", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ffmpegPid", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resumeGlobalIndex", content, StringComparison.OrdinalIgnoreCase);
    }

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

internal sealed class RollingLibraryFixture : IDisposable
{
    private readonly Dictionary<string, TimeSpan?> _durations = new(GetPathComparer());
    private readonly List<SongCatalogEntry> _songs = [];
    private int _nextId;

    private RollingLibraryFixture()
    {
        Root = Directory.CreateTempSubdirectory("nzytetv-rolling-library-").FullName;
        LibraryRoot = Path.Combine(Root, "library");
        CatalogPath = Path.Combine(Root, "catalog", "song-catalog.json");
        ProgrammingPath = Path.Combine(Root, "catalog", "programming.json");
        Directory.CreateDirectory(LibraryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
    }

    public string Root { get; }

    public string LibraryRoot { get; }

    public string CatalogPath { get; }

    public string ProgrammingPath { get; }

    public FixedRollingTimeProvider Time { get; } = new(
        new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    public static async Task<RollingLibraryFixture> CreateAsync(bool programming)
    {
        var fixture = new RollingLibraryFixture();
        for (int index = 1; index <= 12; index++)
        {
            string type = (index % 5) switch
            {
                0 => AssetTypes.MusicVideo,
                1 => AssetTypes.LyricVideo,
                2 => AssetTypes.Visualizer,
                3 => AssetTypes.AnimatedVisual,
                _ => AssetTypes.Performance,
            };
            await fixture.AddSongAssetAsync(
                $"song-{index:D2}",
                $"asset-{index:D2}",
                type,
                1200 + index);
        }

        await fixture.AddSongAssetAsync("song-short", "asset-short", AssetTypes.ShortForm, 30);
        await fixture.AddNonSongAssetAsync("vlog-a", AssetTypes.Vlog, 420);
        await fixture.AddNonSongAssetAsync("vlog-b", AssetTypes.Vlog, 480);
        await fixture.AddNonSongAssetAsync("bumper-a", AssetTypes.Bumper, 12);
        await fixture.AddNonSongAssetAsync("bumper-b", AssetTypes.Bumper, 10);
        await fixture.AddNonSongAssetAsync("promo-a", AssetTypes.Promo, 20);
        await fixture.WriteCatalogAsync();
        if (programming)
        {
            await new ProgrammingConfigurationStore().InitializeAsync(
                fixture.ProgrammingPath,
                CancellationToken.None);
        }

        return fixture;
    }

    public RollingProgrammingPlanner CreatePlanner(
        IRollingPlannerFaultInjector? faultInjector = null,
        IAssetMetadataRepository? metadataRepository = null)
    {
        IPlaylistPlanningSnapshotService snapshotService = CreateSnapshotService(metadataRepository);
        return new RollingProgrammingPlanner(
            snapshotService: snapshotService,
            faultInjector: faultInjector,
            committedBlockResolver: new RollingCommittedBlockResolver(
                metadataRepository: metadataRepository),
            timeProvider: Time,
            idFactory: NextId,
            baseSeedFactory: () => 919191);
    }

    public IPlaylistPlanningSnapshotService CreateSnapshotService(
        IAssetMetadataRepository? metadataRepository = null) =>
        new PlaylistPlanningSnapshotService(
            new SongCatalogStore(),
            new PlaylistLibraryLoader(
                new StubAnalyzer(_durations),
                metadataRepository: metadataRepository),
            new PlaylistGenerator(),
            metadataRepository: metadataRepository);

    public Task<RollingInitializationResult> InitializeAsync(
        RollingProgrammingPlanner planner,
        TimeSpan? testBlockDuration = null) =>
        planner.InitializeAsync(
            new RollingInitializationRequest(
                Root,
                BaseSeed: 20260930,
                TestBlockDuration: testBlockDuration),
            CancellationToken.None);

    public async Task AddSongAssetAsync(
        string contentGroupId,
        string assetId,
        string type,
        double durationSeconds)
    {
        if (_songs.All(song => song.ContentGroupId != contentGroupId))
        {
            _songs.Add(new SongCatalogEntry
            {
                ContentGroupId = contentGroupId,
                Title = contentGroupId,
                Artist = "Nzyte",
                Aliases = [],
            });
        }

        await AddAssetAsync(assetId, contentGroupId, type, durationSeconds);
        await WriteCatalogAsync();
    }

    public Task AddNonSongAssetAsync(string assetId, string type, double durationSeconds) =>
        AddAssetAsync(assetId, null, type, durationSeconds);

    public RollingPlanningInputSnapshot ReadInput(RollingCommittedBlock block)
    {
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(Root);
        string path = RollingPathSafety.ResolveExistingFile(paths.RollingRoot, block.InputSnapshotPath!);
        return RollingProgrammingJson.Deserialize<RollingPlanningInputSnapshot>(
            File.ReadAllText(path),
            "test input snapshot");
    }

    public PlaylistHistoryDocument ReadHistory(RollingArtifactReference reference)
    {
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(Root);
        return new PlaylistHistoryStore().Load(
            RollingPathSafety.ResolveExistingFile(paths.RollingRoot, reference.RelativePath));
    }

    public Dictionary<string, string> CaptureCommittedJson(RollingProgrammingManifest manifest)
    {
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(Root);
        return manifest.Blocks!
            .SelectMany(block => new[]
            {
                block.PlaylistPath!,
                block.DescriptorPath!,
                block.InputSnapshotPath!,
                block.HistoryAfter!.RelativePath,
            })
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                relative => relative,
                relative => File.ReadAllText(RollingPathSafety.ResolveExistingFile(paths.RollingRoot, relative)),
                StringComparer.Ordinal);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);

    private async Task AddAssetAsync(
        string assetId,
        string? contentGroupId,
        string type,
        double durationSeconds)
    {
        string category = type.Replace('-', ' ');
        string path = Path.Combine(LibraryRoot, category, $"{assetId}.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $"normalized-{assetId}");
        await File.WriteAllTextAsync(SourceManifestStore.GetManifestPath(path), $"technical-{assetId}");
        await new AssetMetadataStore().WriteAsync(
            path,
            new AssetMetadata
            {
                AssetId = assetId,
                ContentGroupId = contentGroupId,
                Title = assetId,
                Artist = contentGroupId is null ? null : "Nzyte",
                Type = type,
                Enabled = true,
                Tags = [],
            },
            CancellationToken.None);
        _durations[Path.GetFullPath(path)] = TimeSpan.FromSeconds(durationSeconds);
    }

    private async Task WriteCatalogAsync()
    {
        var catalog = new SongCatalog
        {
            SchemaVersion = SongCatalog.CurrentSchemaVersion,
            Songs = _songs.OrderBy(song => song.ContentGroupId, StringComparer.Ordinal).ToList(),
        };
        string json = System.Text.Json.JsonSerializer.Serialize(
            catalog,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }) + Environment.NewLine;
        await File.WriteAllTextAsync(CatalogPath, json);
    }

    private string NextId() => (++_nextId).ToString("x32");

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed class StubAnalyzer(IReadOnlyDictionary<string, TimeSpan?> durations) : IMediaAnalyzer
    {
        public Task<MediaDescription> InspectAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaDescription(
                filePath,
                durations[Path.GetFullPath(filePath)],
                "mov,mp4",
                1,
                null,
                null,
                []));

        public Task<MediaDescription> AnalyzeForVerificationAsync(
            string filePath,
            CancellationToken cancellationToken) => InspectAsync(filePath, cancellationToken);
    }
}

internal sealed class FixedRollingTimeProvider(DateTimeOffset value) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => value;
}
