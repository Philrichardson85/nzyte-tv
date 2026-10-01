using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class RollingProgrammingReplenisherTests
{
    [Fact]
    public async Task UninitializedStation_MaintainsInitialThreeBlockWindow()
    {
        using var fixture = new ReplenisherFixture(committed: 0);
        fixture.Trigger.CancelOnWaitNumber = 1;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync());

        Assert.Equal([3L], fixture.Maintainer.Targets);
        Assert.Equal(3, fixture.PlanStore.Manifest.Blocks!.Count);
        RollingReplenishmentState state = fixture.ReadAdvisory();
        Assert.Equal(RollingReplenishmentHealth.Healthy, state.Health);
        Assert.Equal(3, state.RequiredHighestSequence);
        Assert.Equal(0, state.BufferDeficit);
    }

    [Fact]
    public async Task ActiveBlockTwo_MaintainsThroughBlockFour()
    {
        using var fixture = new ReplenisherFixture(committed: 3);
        await fixture.WriteActiveStateAsync(RollingStationPhase.Executing, active: 2, completed: 1);
        fixture.Trigger.CancelOnWaitNumber = 1;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync());

        Assert.Equal([4L], fixture.Maintainer.Targets);
        Assert.Equal(4, fixture.PlanStore.Manifest.Blocks!.Count);
    }

    [Fact]
    public async Task HealthyBuffer_PerformsOneStartupReconciliationThenWaits()
    {
        using var fixture = new ReplenisherFixture(committed: 3);
        await fixture.WriteActiveStateAsync(RollingStationPhase.Claimed, active: 1, completed: null);
        fixture.Trigger.CancelOnWaitNumber = 1;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync());

        Assert.Equal([3L], fixture.Maintainer.Targets);
        Assert.Equal(1, fixture.Trigger.WaitCount);
    }

    [Fact]
    public async Task ExecutionAdvanceDuringMaintenance_RecalculatesAndExtendsNewTarget()
    {
        using var fixture = new ReplenisherFixture(committed: 3);
        await fixture.WriteActiveStateAsync(RollingStationPhase.Executing, active: 1, completed: null);
        fixture.Maintainer.OnCallAsync = async (target, call) =>
        {
            if (call == 1)
            {
                await fixture.WriteActiveStateAsync(
                    RollingStationPhase.Executing,
                    active: 2,
                    completed: 1);
            }

            fixture.PlanStore.PublishThrough((int)target);
        };
        fixture.Trigger.CancelOnWaitNumber = 1;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync());

        Assert.Equal([3L, 4L], fixture.Maintainer.Targets);
        Assert.Equal(4, fixture.PlanStore.Manifest.Blocks!.Count);
    }

    [Fact]
    public async Task LockContention_IsRetriedAndBackoffResetsAfterSuccess()
    {
        using var fixture = new ReplenisherFixture(committed: 2);
        fixture.Maintainer.Exceptions.Enqueue(
            new RollingPlannerLockUnavailableException("busy", new IOException("held")));
        fixture.Trigger.CancelOnWaitNumber = 2;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync());

        Assert.Equal([3L, 3L], fixture.Maintainer.Targets);
        Assert.Equal(
            [RollingReplenishmentPolicy.RetryIntervals[0], ReplenisherFixture.ConsistencyInterval],
            fixture.Trigger.Timeouts);
        RollingReplenishmentState state = fixture.ReadAdvisory();
        Assert.Equal(RollingReplenishmentHealth.Healthy, state.Health);
        Assert.Equal(RollingReplenishmentErrorClassification.Transient, state.ErrorClassification);
        Assert.Contains("busy", state.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedTemporaryFailures_UseFiveFifteenThirtyThenSixtySecondBackoff()
    {
        using var fixture = new ReplenisherFixture(committed: 2);
        foreach (string message in new[] { "one", "two", "three", "four" })
        {
            fixture.Maintainer.Exceptions.Enqueue(
                new RollingPlannerLockUnavailableException(message, new IOException("held")));
        }

        fixture.Trigger.CancelOnWaitNumber = 5;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync());

        Assert.Equal([3L, 3L, 3L, 3L, 3L], fixture.Maintainer.Targets);
        Assert.Equal(
            [
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(60),
                ReplenisherFixture.ConsistencyInterval,
            ],
            fixture.Trigger.Timeouts);
    }

    [Theory]
    [InlineData("mount")]
    [InlineData("ffprobe")]
    public async Task TemporaryMediaOrToolFailure_IsRetryable(string failure)
    {
        using var fixture = new ReplenisherFixture(committed: 2);
        fixture.Maintainer.Exceptions.Enqueue(failure == "mount"
            ? new DirectoryNotFoundException("media mount unavailable")
            : new MediaToolNotFoundException("FFprobe unavailable"));
        fixture.Trigger.CancelOnWaitNumber = 1;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync());

        RollingReplenishmentState state = fixture.ReadAdvisory();
        Assert.Equal(RollingReplenishmentHealth.Degraded, state.Health);
        Assert.Equal(
            RollingReplenishmentErrorClassification.Transient,
            state.ErrorClassification);
        Assert.Equal([TimeSpan.FromSeconds(5)], fixture.Trigger.Timeouts);
    }

    [Fact]
    public async Task PlanningBlocked_RecordsHealthWithoutInvalidatingCommittedBlocks()
    {
        using var fixture = new ReplenisherFixture(committed: 2);
        fixture.Maintainer.Exceptions.Enqueue(
            new RollingPlanningBlockedException(
                "future programming is invalid",
                new InvalidDataException("malformed")));
        fixture.Trigger.CancelOnWaitNumber = 1;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync());

        Assert.Equal(2, fixture.PlanStore.Manifest.Blocks!.Count);
        RollingReplenishmentState state = fixture.ReadAdvisory();
        Assert.Equal(RollingReplenishmentHealth.Blocked, state.Health);
        Assert.Equal(
            RollingReplenishmentErrorClassification.PlanningBlocked,
            state.ErrorClassification);
    }

    [Fact]
    public async Task CommittedChainSafetyFailure_StopsReplenishmentWithoutRetrying()
    {
        using var fixture = new ReplenisherFixture(committed: 2);
        fixture.Maintainer.Exceptions.Enqueue(
            new RollingPlanningSafetyException(
                "committed chain is corrupt",
                new InvalidDataException("hash mismatch")));

        await fixture.RunAsync();

        Assert.Single(fixture.Maintainer.Targets);
        Assert.Equal(0, fixture.Trigger.WaitCount);
        Assert.Equal(
            RollingReplenishmentHealth.SafetyFailure,
            fixture.ReadAdvisory().Health);
    }

    [Fact]
    public async Task CancellationDuringMaintenance_IsObservedWithoutPublishingFalseSuccess()
    {
        using var fixture = new ReplenisherFixture(committed: 2);
        fixture.Maintainer.OnCallAsync = (_, _) =>
        {
            fixture.Cancellation.Cancel();
            return Task.FromCanceled(fixture.Cancellation.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync());

        Assert.Single(fixture.Maintainer.Targets);
        Assert.False(File.Exists(fixture.AdvisoryPath));
    }

    [Fact]
    public async Task SidecarWriteFailure_IsReportedButDoesNotStopMaintenance()
    {
        using var fixture = new ReplenisherFixture(
            committed: 2,
            advisoryStore: new ThrowingAdvisoryStore());
        fixture.Trigger.CancelOnWaitNumber = 1;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync());

        Assert.Single(fixture.Maintainer.Targets);
        Assert.Equal(3, fixture.PlanStore.Manifest.Blocks!.Count);
        Assert.Contains(fixture.Diagnostics, value =>
            value.Contains("could not be persisted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ErrorsAreRedactedBeforeAdvisoryPersistence()
    {
        const string secret = "rtmps://example.invalid/live2/FAKE-SECRET";
        using var fixture = new ReplenisherFixture(committed: 2);
        fixture.Maintainer.Exceptions.Enqueue(
            new RollingPlanningBlockedException(
                $"invalid destination {secret}",
                new InvalidOperationException("invalid")));
        fixture.Trigger.CancelOnWaitNumber = 1;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync());
        string json = File.ReadAllText(fixture.AdvisoryPath);

        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
    }

    private sealed class ReplenisherFixture : IDisposable
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T12:00:00Z");

        public ReplenisherFixture(
            int committed,
            IRollingReplenishmentStateStore? advisoryStore = null)
        {
            Root = Directory.CreateTempSubdirectory("nzytetv-replenisher-").FullName;
            MediaRoot = Directory.CreateDirectory(Path.Combine(Root, "media")).FullName;
            LibraryRoot = Directory.CreateDirectory(Path.Combine(MediaRoot, "library")).FullName;
            RollingStatePath = Path.Combine(Root, "runtime", "rolling-state.json");
            AdvisoryPath = RollingReplenishmentStateStore.GetPath(RollingStatePath);
            Configuration = new RollingStationConfiguration
            {
                StationConfigPath = Path.Combine(Root, "station.json"),
                PlannerId = "00112233445566778899aabbccddeeff",
                RollingStatePath = RollingStatePath,
            };
            StationConfiguration = new StationConfiguration
            {
                MediaRoot = MediaRoot,
                LibraryRoot = LibraryRoot,
                StatePath = Path.Combine(Root, "runtime", "state.json"),
                Playlists = [Path.Combine(Root, "static.json")],
            };
            PlanStore = new MutablePlanStore(CreateManifest(committed));
            Maintainer = new FakeMaintainer(PlanStore);
            Trigger = new ScriptedTrigger(Cancellation);
            var replenisher = new RollingProgrammingReplenisher(
                Maintainer,
                Trigger,
                rollingConfigurationLoader: new FixedRollingLoader(Configuration),
                stationConfigurationLoader: new FixedStationLoader(StationConfiguration),
                rollingStateStore: RollingStore,
                planStore: PlanStore,
                replenishmentStateStore: advisoryStore ?? new RollingReplenishmentStateStore(),
                timeProvider: new FixedTimeProvider(Now),
                consistencyCheckInterval: ConsistencyInterval,
                diagnostic: Diagnostics.Add);
            Replenisher = replenisher;
        }

        public static TimeSpan ConsistencyInterval => TimeSpan.FromSeconds(37);

        public string Root { get; }

        public string MediaRoot { get; }

        public string LibraryRoot { get; }

        public string RollingStatePath { get; }

        public string AdvisoryPath { get; }

        public RollingStationConfiguration Configuration { get; }

        public StationConfiguration StationConfiguration { get; }

        public MutablePlanStore PlanStore { get; }

        public FakeMaintainer Maintainer { get; }

        public ScriptedTrigger Trigger { get; }

        public RollingStationStateStore RollingStore { get; } = new();

        public CancellationTokenSource Cancellation { get; } = new();

        public List<string> Diagnostics { get; } = [];

        public IRollingProgrammingReplenisher Replenisher { get; }

        public Task RunAsync() => Replenisher.RunAsync("ignored.json", Cancellation.Token);

        public RollingReplenishmentState ReadAdvisory() =>
            new RollingReplenishmentStateStore().Read(AdvisoryPath);

        public Task WriteActiveStateAsync(
            RollingStationPhase phase,
            long active,
            long? completed) => RollingStore.WriteAsync(
                RollingStatePath,
                new RollingStationRuntimeState
                {
                    PlannerId = Configuration.PlannerId,
                    Phase = phase,
                    ActiveBlockSequence = active,
                    ActiveBlockId = new string('a', 64),
                    ActiveQueueId = new string('b', 64),
                    ClaimedAtUtc = Now,
                    LastCompletedBlockSequence = completed,
                    LastCompletedBlockId = completed is null ? null : new string('c', 64),
                    LastCompletedAtUtc = completed is null ? null : Now - TimeSpan.FromHours(6),
                    UpdatedAtUtc = Now,
                },
                CancellationToken.None);

        public void Dispose()
        {
            Cancellation.Dispose();
            Directory.Delete(Root, recursive: true);
        }

        private RollingProgrammingManifest CreateManifest(int count)
        {
            var genesis = new RollingArtifactReference(
                $"history/{new string('0', 64)}.json",
                new string('0', 64));
            RollingCommittedBlock[] blocks = Enumerable.Range(1, count)
                .Select(sequence => new RollingCommittedBlock
                {
                    Sequence = sequence,
                    BlockId = new string((char)('a' + (sequence % 20)), 64),
                })
                .ToArray();
            return new RollingProgrammingManifest
            {
                PlannerId = Configuration.PlannerId,
                BaseSeed = 7,
                TargetPreparedBlockCount = 3,
                NextSequence = count + 1,
                GenesisHistory = genesis,
                HistoryHead = genesis,
                Blocks = blocks,
                InitializedAtUtc = Now,
            };
        }

        public sealed class MutablePlanStore(RollingProgrammingManifest manifest) : IRollingPlanStore
        {
            public RollingProgrammingManifest Manifest { get; private set; } = manifest;

            public void PublishThrough(int count)
            {
                RollingProgrammingManifest old = Manifest;
                RollingCommittedBlock[] blocks = Enumerable.Range(1, count)
                    .Select(sequence => new RollingCommittedBlock
                    {
                        Sequence = sequence,
                        BlockId = new string((char)('a' + (sequence % 20)), 64),
                    })
                    .ToArray();
                Manifest = new RollingProgrammingManifest
                {
                    PlannerId = old.PlannerId,
                    BaseSeed = old.BaseSeed,
                    TargetBlockDurationSeconds = old.TargetBlockDurationSeconds,
                    TargetPreparedBlockCount = old.TargetPreparedBlockCount,
                    NextSequence = count + 1,
                    GenesisHistory = old.GenesisHistory,
                    HistoryHead = old.HistoryHead,
                    Blocks = blocks,
                    InitializedAtUtc = old.InitializedAtUtc,
                };
            }

            public RollingProgrammingManifest LoadManifest(string path) => Manifest;

            public RollingProgrammingManifest? LoadManifestIfExists(string path) => Manifest;

            public Task WriteManifestAsync(
                string path,
                RollingProgrammingManifest manifest,
                CancellationToken cancellationToken)
            {
                Manifest = manifest;
                return Task.CompletedTask;
            }

            public Task WriteStagedAsync<T>(
                string path,
                T value,
                CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task WriteImmutableAsync<T>(
                string path,
                T value,
                CancellationToken cancellationToken) => throw new NotSupportedException();

            public T Read<T>(string path, string description) => throw new NotSupportedException();

            public string ReadText(string path) => throw new NotSupportedException();
        }

        public sealed class FakeMaintainer(MutablePlanStore planStore) : IRollingBlockMaintainer
        {
            private int _calls;

            public List<long> Targets { get; } = [];

            public Queue<Exception> Exceptions { get; } = [];

            public Func<long, int, Task>? OnCallAsync { get; set; }

            public async Task<RollingMaintainResult> EnsureCommittedThroughAsync(
                string mediaRoot,
                long requiredHighestSequence,
                CancellationToken cancellationToken)
            {
                Targets.Add(requiredHighestSequence);
                int call = ++_calls;
                if (Exceptions.TryDequeue(out Exception? exception))
                {
                    throw exception;
                }

                if (OnCallAsync is not null)
                {
                    await OnCallAsync(requiredHighestSequence, call);
                }
                else
                {
                    planStore.PublishThrough(Math.Max(
                        planStore.Manifest.Blocks!.Count,
                        checked((int)requiredHighestSequence)));
                }

                return new RollingMaintainResult(
                    RollingProgrammingPaths.FromMediaRoot(mediaRoot),
                    planStore.Manifest,
                    0,
                    0,
                    planStore.Manifest.Blocks!.Count >= requiredHighestSequence);
            }
        }

        public sealed class ScriptedTrigger(CancellationTokenSource cancellation)
            : IRollingReplenishmentTrigger
        {
            public int CancelOnWaitNumber { get; set; } = int.MaxValue;

            public int WaitCount { get; private set; }

            public List<TimeSpan> Timeouts { get; } = [];

            public void Signal()
            {
            }

            public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
            {
                Timeouts.Add(timeout);
                WaitCount++;
                if (WaitCount >= CancelOnWaitNumber)
                {
                    cancellation.Cancel();
                    return Task.FromCanceled(cancellationToken);
                }

                return Task.CompletedTask;
            }
        }

        private sealed class FixedRollingLoader(RollingStationConfiguration configuration)
            : IRollingStationConfigurationLoader
        {
            public RollingStationConfiguration Load(string path) => configuration;
        }

        private sealed class FixedStationLoader(StationConfiguration configuration)
            : IStationConfigurationLoader
        {
            public StationConfiguration Load(string path) => configuration;
        }

        private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => value;
        }
    }

    private sealed class ThrowingAdvisoryStore : IRollingReplenishmentStateStore
    {
        public Task WriteAsync(
            string path,
            RollingReplenishmentState state,
            CancellationToken cancellationToken) => throw new IOException("disk unavailable");

        public RollingReplenishmentState Read(string path) => throw new FileNotFoundException();

        public RollingReplenishmentState? ReadIfExists(string path) => null;
    }
}
