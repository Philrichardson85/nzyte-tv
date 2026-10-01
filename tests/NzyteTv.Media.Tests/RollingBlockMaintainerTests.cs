using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class RollingBlockMaintainerTests
{
    [Fact]
    public async Task HealthyBuffer_ReconcilesWithoutInitializingFfprobeOrGenerationDependencies()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        await planner.MaintainAsync(fixture.Root, CancellationToken.None);
        var locator = new CountingMediaToolLocator();
        int snapshots = 0;
        var maintainer = new RollingBlockMaintainer(
            mediaToolLocator: locator,
            reconciliationPlannerFactory: () => fixture.CreatePlanner(),
            generationPlannerFactory: _ => fixture.CreatePlanner(),
            snapshotServiceFactory: _ =>
            {
                snapshots++;
                return fixture.CreateSnapshotService();
            });

        RollingMaintainResult result = await maintainer.EnsureCommittedThroughAsync(
            fixture.Root,
            requiredHighestSequence: 3,
            CancellationToken.None);

        Assert.True(result.TargetSatisfied);
        Assert.Equal(3, result.Manifest.Blocks!.Count);
        Assert.Equal(0, locator.FfprobeCalls);
        Assert.Equal(0, snapshots);
    }

    [Fact]
    public async Task Deficit_LazilyInitializesDependenciesAndDelegatesGenerationToAcceptedPlanner()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 3);
        var locator = new CountingMediaToolLocator();
        var maintainer = new RollingBlockMaintainer(
            mediaToolLocator: locator,
            reconciliationPlannerFactory: () => fixture.CreatePlanner(),
            generationPlannerFactory: _ => fixture.CreatePlanner(),
            snapshotServiceFactory: _ => fixture.CreateSnapshotService());

        RollingMaintainResult result = await maintainer.EnsureCommittedThroughAsync(
            fixture.Root,
            requiredHighestSequence: 4,
            CancellationToken.None);

        Assert.True(result.TargetSatisfied);
        Assert.Equal(4, result.Manifest.Blocks!.Count);
        Assert.Equal(1, locator.FfprobeCalls);
        Assert.Equal(result.Manifest.Blocks[2].HistoryAfter, result.Manifest.Blocks[3].HistoryBefore);
    }

    [Fact]
    public async Task MalformedFutureProgramming_IsClassifiedBlockedWithoutChangingCommittedPrefix()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult first = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 3);
        Dictionary<string, string> prefix = fixture.CaptureCommittedJson(first.Manifest);
        File.WriteAllText(fixture.ProgrammingPath, "{ malformed future policy");
        var maintainer = new RollingBlockMaintainer(
            mediaToolLocator: new CountingMediaToolLocator(),
            reconciliationPlannerFactory: () => fixture.CreatePlanner(),
            generationPlannerFactory: _ => fixture.CreatePlanner(),
            snapshotServiceFactory: _ => fixture.CreateSnapshotService());

        await Assert.ThrowsAsync<RollingPlanningBlockedException>(() =>
            maintainer.EnsureCommittedThroughAsync(
                fixture.Root,
                requiredHighestSequence: 4,
                CancellationToken.None));

        RollingProgrammingManifest manifest = new RollingPlanStore().LoadManifest(
            RollingProgrammingPaths.FromMediaRoot(fixture.Root).ManifestPath);
        Assert.Equal(3, manifest.Blocks!.Count);
        Assert.Equal(prefix, fixture.CaptureCommittedJson(manifest));
    }

    [Fact]
    public async Task PlannerLockContention_RemainsTypedAndTransientToCallers()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(fixture.Root);
        using IRollingPlannerLock held = new RollingPlannerLockProvider().Acquire(paths.PlannerLockPath);
        var maintainer = new RollingBlockMaintainer(
            reconciliationPlannerFactory: () => fixture.CreatePlanner());

        await Assert.ThrowsAsync<RollingPlannerLockUnavailableException>(() =>
            maintainer.EnsureCommittedThroughAsync(
                fixture.Root,
                requiredHighestSequence: 3,
                CancellationToken.None));
    }

    private sealed class CountingMediaToolLocator : IMediaToolLocator
    {
        public int FfprobeCalls { get; private set; }

        public Task<MediaToolPaths> LocateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MediaToolPaths("ffmpeg", "ffprobe"));

        public Task<string> LocateFfmpegAsync(CancellationToken cancellationToken) =>
            Task.FromResult("ffmpeg");

        public Task<string> LocateFfprobeAsync(CancellationToken cancellationToken)
        {
            FfprobeCalls++;
            return Task.FromResult("ffprobe");
        }
    }
}
