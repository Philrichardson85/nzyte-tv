using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class RollingStationCoordinatorTests
{
    [Theory]
    [MemberData(nameof(AllFaultCheckpoints))]
    public async Task EveryFaultCheckpoint_LeavesOnlyReadableDurableDocuments(
        RollingCoordinatorCheckpoint checkpoint)
    {
        using var fixture = new CoordinatorFixture();
        var executor = new ScriptedExecutor(fixture.CompleteExecution);

        await Assert.ThrowsAsync<RollingCoordinatorSimulatedCrashException>(() =>
            fixture.CreateCoordinator(
                executor,
                faultInjector: new ThrowOnceFault(checkpoint, 1)).RunAsync(
                    fixture.ConfigurationPath,
                    false,
                    CancellationToken.None));

        if (File.Exists(fixture.RollingStatePath))
        {
            _ = fixture.RollingStore.Read(fixture.RollingStatePath);
        }

        if (File.Exists(fixture.StationStatePath))
        {
            _ = fixture.StationStore.Read(fixture.StationStatePath);
        }
    }

    public static TheoryData<RollingCoordinatorCheckpoint> AllFaultCheckpoints => new(
        Enum.GetValues<RollingCoordinatorCheckpoint>());

    [Fact]
    public async Task SixBlockSimulation_ReplenishesThroughEightWithoutReplayOrSkip()
    {
        using var fixture = new CoordinatorFixture(initialBlockCount: 3, availableBlockCount: 8);
        var cancellation = new CancellationTokenSource();
        var trigger = new RollingReplenishmentTrigger();
        var signalingStore = new SignalingRollingStationStateStore(fixture.RollingStore, trigger);
        var ownership = new RollingCoordinatorOwnershipSignal();
        var lockProvider = new SignalingRollingCoordinatorLockProvider(
            new RollingCoordinatorLockProvider(),
            ownership);
        var executor = new ScriptedExecutor(async (sequence, configuration, plan, token) =>
        {
            if (sequence == 6)
            {
                while (fixture.PlanStore.Manifest.Blocks!.Count < 8)
                {
                    await Task.Delay(1, token);
                }
            }

            return await fixture.CompleteExecution(sequence, configuration, plan, token);
        });
        var fault = new CancelAtFault(
            RollingCoordinatorCheckpoint.AfterRollingCompletionPersistence,
            sequence: 6,
            cancellation);
        RollingStationCoordinator coordinator = fixture.CreateCoordinator(
            executor,
            faultInjector: fault,
            rollingStateStore: signalingStore,
            lockProvider: lockProvider);
        var maintainer = new CoordinatorFakeMaintainer(fixture);
        var replenisher = new RollingProgrammingReplenisher(
            maintainer,
            trigger,
            rollingConfigurationLoader: new FixedRollingConfigurationLoader(fixture.Configuration),
            stationConfigurationLoader: new FixedStationConfigurationLoader(fixture.StationConfiguration),
            rollingStateStore: signalingStore,
            planStore: fixture.PlanStore,
            timeProvider: new FixedTimeProvider(fixture.Now),
            consistencyCheckInterval: TimeSpan.FromMilliseconds(25));
        var host = new RollingStationRuntimeHost(coordinator, replenisher, ownership);
        Dictionary<string, (byte[] Content, DateTime Mtime)> immutablePrefix = Enumerable
            .Range(1, 3)
            .Select(sequence => Path.Combine(fixture.Root, $"block-{sequence}.json"))
            .ToDictionary(
                path => path,
                path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)));

        RollingStationRunResult result = await host.RunAsync(
            fixture.ConfigurationPath,
            false,
            cancellation.Token);

        Assert.True(
            result.ExitCode == 0,
            $"{result.Error}; calls={string.Join(',', executor.Calls)}; " +
            $"phase={result.FinalState?.Phase}; active={result.FinalState?.ActiveBlockSequence}");
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], executor.Calls);
        Assert.Equal(6, executor.Calls.Distinct().Count());
        Assert.Equal(8, fixture.PlanStore.Manifest.Blocks!.Count);
        Assert.Equal(9, fixture.PlanStore.Manifest.NextSequence);
        Assert.Equal(6, result.FinalState!.LastCompletedBlockSequence);
        Assert.Null(result.FinalState.ActiveBlockSequence);
        Assert.Contains(8, maintainer.Targets);
        for (int index = 0; index < fixture.PlanStore.Manifest.Blocks.Count - 1; index++)
        {
            Assert.Equal(
                fixture.PlanStore.Manifest.Blocks[index].HistoryAfter,
                fixture.PlanStore.Manifest.Blocks[index + 1].HistoryBefore);
        }

        foreach ((string path, (byte[] content, DateTime mtime)) in immutablePrefix)
        {
            Assert.Equal(content, File.ReadAllBytes(path));
            Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));
        }

        StationRuntimeState cp2 = fixture.StationStore.Read(fixture.StationStatePath);
        Assert.Equal(fixture.Resolved[6].QueueId, cp2.QueueId);
        Assert.Equal(6, fixture.CompletedStates.Count);
    }

    [Fact]
    public async Task IntegratedRestart_RecoversInterruptedExecutionAndTransientPlanningThenReachesBlockEight()
    {
        using var fixture = new CoordinatorFixture(initialBlockCount: 3, availableBlockCount: 8);
        var maintainer = new CoordinatorFakeMaintainer(fixture);
        maintainer.Exceptions.Enqueue(
            new RollingPlannerLockUnavailableException("simulated planner interruption", new IOException("held")));
        var firstTrigger = new RollingReplenishmentTrigger();
        var firstStore = new SignalingRollingStationStateStore(fixture.RollingStore, firstTrigger);
        var firstOwnership = new RollingCoordinatorOwnershipSignal();
        var firstExecutor = new ScriptedExecutor(async (sequence, configuration, plan, token) =>
        {
            if (sequence == 1)
            {
                return await fixture.CompleteExecution(sequence, configuration, plan, token);
            }

            StationRuntimeState partial = fixture.CreateStationState(
                plan,
                StationState.Broadcasting,
                completedIndex: 0);
            await fixture.StationStore.WriteAsync(
                fixture.StationStatePath,
                partial,
                CancellationToken.None);
            throw new RollingCoordinatorSimulatedCrashException(
                "simulated process interruption during block two");
        });
        var firstCoordinator = fixture.CreateCoordinator(
            firstExecutor,
            rollingStateStore: firstStore,
            lockProvider: new SignalingRollingCoordinatorLockProvider(
                new RollingCoordinatorLockProvider(),
                firstOwnership));
        var firstReplenisher = new RollingProgrammingReplenisher(
            maintainer,
            firstTrigger,
            rollingConfigurationLoader: new FixedRollingConfigurationLoader(fixture.Configuration),
            stationConfigurationLoader: new FixedStationConfigurationLoader(fixture.StationConfiguration),
            rollingStateStore: firstStore,
            planStore: fixture.PlanStore,
            timeProvider: new FixedTimeProvider(fixture.Now),
            consistencyCheckInterval: TimeSpan.FromMilliseconds(25));
        var firstHost = new RollingStationRuntimeHost(
            firstCoordinator,
            firstReplenisher,
            firstOwnership);

        await Assert.ThrowsAsync<RollingCoordinatorSimulatedCrashException>(() =>
            firstHost.RunAsync(fixture.ConfigurationPath, false, CancellationToken.None));

        Assert.Equal([1L, 2L], firstExecutor.Calls);
        Assert.Equal(1, fixture.StationStore.Read(fixture.StationStatePath).ResumeGlobalIndex);
        Assert.Contains(maintainer.Targets, target => target == 3);

        using var cancellation = new CancellationTokenSource();
        var secondTrigger = new RollingReplenishmentTrigger();
        var secondStore = new SignalingRollingStationStateStore(fixture.RollingStore, secondTrigger);
        var secondOwnership = new RollingCoordinatorOwnershipSignal();
        var secondExecutor = new ScriptedExecutor(fixture.CompleteExecution);
        var secondCoordinator = fixture.CreateCoordinator(
            secondExecutor,
            faultInjector: new CancelAtFault(
                RollingCoordinatorCheckpoint.AfterRollingCompletionPersistence,
                sequence: 6,
                cancellation),
            rollingStateStore: secondStore,
            lockProvider: new SignalingRollingCoordinatorLockProvider(
                new RollingCoordinatorLockProvider(),
                secondOwnership));
        var secondReplenisher = new RollingProgrammingReplenisher(
            maintainer,
            secondTrigger,
            rollingConfigurationLoader: new FixedRollingConfigurationLoader(fixture.Configuration),
            stationConfigurationLoader: new FixedStationConfigurationLoader(fixture.StationConfiguration),
            rollingStateStore: secondStore,
            planStore: fixture.PlanStore,
            timeProvider: new FixedTimeProvider(fixture.Now),
            consistencyCheckInterval: TimeSpan.FromMilliseconds(25));
        var secondHost = new RollingStationRuntimeHost(
            secondCoordinator,
            secondReplenisher,
            secondOwnership);

        RollingStationRunResult result = await secondHost.RunAsync(
            fixture.ConfigurationPath,
            false,
            cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([2L, 3L, 4L, 5L, 6L], secondExecutor.Calls);
        Assert.Equal(8, fixture.PlanStore.Manifest.Blocks!.Count);
        Assert.Equal(6, result.FinalState!.LastCompletedBlockSequence);
        Assert.Contains(maintainer.Targets, target => target == 8);
    }

    [Fact]
    public async Task Crash01_BeforeClaim_LeavesCoordinatorUninitializedAndDoesNotSkipBlock()
    {
        using var fixture = new CoordinatorFixture();
        var executor = new ScriptedExecutor();
        RollingStationCoordinator coordinator = fixture.CreateCoordinator(
            executor,
            faultInjector: new ThrowOnceFault(RollingCoordinatorCheckpoint.BeforeClaimPersistence, 1));

        await Assert.ThrowsAsync<RollingCoordinatorSimulatedCrashException>(() =>
            coordinator.RunAsync(fixture.ConfigurationPath, false, CancellationToken.None));

        Assert.Null(fixture.RollingStore.ReadIfExists(fixture.RollingStatePath));
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task Crash02_ClaimPersistedBeforeExecution_RecoversSameClaimAndClaimIsNotPlaybackEvidence()
    {
        using var fixture = new CoordinatorFixture();
        var firstExecutor = new ScriptedExecutor();
        RollingStationCoordinator first = fixture.CreateCoordinator(
            firstExecutor,
            faultInjector: new ThrowOnceFault(RollingCoordinatorCheckpoint.AfterClaimPersistence, 1));

        await Assert.ThrowsAsync<RollingCoordinatorSimulatedCrashException>(() =>
            first.RunAsync(fixture.ConfigurationPath, false, CancellationToken.None));
        RollingStationRuntimeState claimed = fixture.RollingStore.Read(fixture.RollingStatePath);
        Assert.Equal(RollingStationPhase.Claimed, claimed.Phase);
        Assert.Equal(1, claimed.ActiveBlockSequence);
        Assert.Null(fixture.StationStore.ReadIfExists(fixture.StationStatePath));
        Assert.Empty(firstExecutor.Calls);

        var resumedExecutor = new ScriptedExecutor(fixture.StopExecution);
        RollingStationRunResult result = await fixture.CreateCoordinator(resumedExecutor).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([1L], resumedExecutor.Calls);
        Assert.Equal(RollingStationPhase.Stopped, result.FinalState!.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Crash03And04_DuringBlock_RestartsTheSameClaimWithCp2Cursor(bool halfway)
    {
        using var fixture = new CoordinatorFixture();
        var crashing = new ScriptedExecutor(async (sequence, configuration, plan, token) =>
        {
            StationRuntimeState partial = halfway
                ? fixture.CreateStationState(plan, StationState.Broadcasting, completedIndex: 0)
                : fixture.CreateStationState(plan, StationState.Broadcasting, completedIndex: null);
            await fixture.StationStore.WriteAsync(
                fixture.StationStatePath,
                partial,
                CancellationToken.None);
            throw new RollingCoordinatorSimulatedCrashException("simulated parent crash");
        });

        await Assert.ThrowsAsync<RollingCoordinatorSimulatedCrashException>(() =>
            fixture.CreateCoordinator(crashing).RunAsync(
                fixture.ConfigurationPath,
                false,
                CancellationToken.None));
        StationRuntimeState persisted = fixture.StationStore.Read(fixture.StationStatePath);

        var resumed = new ScriptedExecutor(fixture.StopExecution);
        RollingStationRunResult result = await fixture.CreateCoordinator(resumed).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([1L], resumed.Calls);
        Assert.Equal(halfway ? 1 : 0, persisted.ResumeGlobalIndex);
        Assert.Equal(fixture.Resolved[1].QueueId, result.FinalState!.ActiveQueueId);
    }

    [Fact]
    public async Task Crash05_CleanStopRetainsClaimAndDurableCursorForRestart()
    {
        using var fixture = new CoordinatorFixture();
        var stopping = new ScriptedExecutor(fixture.StopExecution);

        RollingStationRunResult stopped = await fixture.CreateCoordinator(stopping).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(0, stopped.ExitCode);
        Assert.Equal(RollingStationPhase.Stopped, stopped.FinalState!.Phase);
        Assert.Equal(1, stopped.FinalState.ActiveBlockSequence);
        Assert.Equal(0, fixture.StationStore.Read(fixture.StationStatePath).ResumeGlobalIndex);

        var resumed = new ScriptedExecutor(fixture.FailExecution);
        RollingStationRunResult failed = await fixture.CreateCoordinator(resumed).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(1, failed.ExitCode);
        Assert.Equal([1L], resumed.Calls);
        Assert.Equal(1, failed.FinalState!.ActiveBlockSequence);
    }

    [Fact]
    public async Task Crash06_GracefulRebootStateResumesClaimRatherThanStartingBlockOneFresh()
    {
        using var fixture = new CoordinatorFixture();
        await fixture.CreateCoordinator(new ScriptedExecutor(fixture.StopAfterFirstCompletion)).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);
        StationRuntimeState beforeRestart = fixture.StationStore.Read(fixture.StationStatePath);

        StationRuntimeState? seenAtRestart = null;
        var executor = new ScriptedExecutor(async (sequence, configuration, plan, token) =>
        {
            seenAtRestart = fixture.StationStore.Read(fixture.StationStatePath);
            return await fixture.StopExecution(sequence, configuration, plan, token);
        });
        await fixture.CreateCoordinator(executor).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(1, beforeRestart.ResumeGlobalIndex);
        Assert.Equal(1, seenAtRestart!.ResumeGlobalIndex);
        Assert.Equal([1L], executor.Calls);
    }

    [Fact]
    public async Task Crash07_FinalItemPersistedBeforeCp2Completed_IsSealedThenAdvanced()
    {
        using var fixture = new CoordinatorFixture();
        var executor = new ScriptedExecutor(fixture.PositiveNonterminalCompletion);
        RollingStationCoordinator coordinator = fixture.CreateCoordinator(
            executor,
            faultInjector: new ThrowOnceFault(RollingCoordinatorCheckpoint.PositiveCompletionObserved, 1));

        await Assert.ThrowsAsync<RollingCoordinatorSimulatedCrashException>(() =>
            coordinator.RunAsync(fixture.ConfigurationPath, false, CancellationToken.None));
        Assert.Equal(
            StationState.Broadcasting,
            fixture.StationStore.Read(fixture.StationStatePath).StationState);

        var sealFault = new ThrowOnceFault(RollingCoordinatorCheckpoint.AfterCompletionSeal, 1);
        await Assert.ThrowsAsync<RollingCoordinatorSimulatedCrashException>(() =>
            fixture.CreateCoordinator(new ScriptedExecutor(), faultInjector: sealFault).RunAsync(
                fixture.ConfigurationPath,
                false,
                CancellationToken.None));
        Assert.Equal(
            StationState.Completed,
            fixture.StationStore.Read(fixture.StationStatePath).StationState);

        var next = new ScriptedExecutor(fixture.StopExecution);
        RollingStationRunResult resumed = await fixture.CreateCoordinator(next).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal([2L], next.Calls);
        Assert.Equal(2, resumed.FinalState!.ActiveBlockSequence);
    }

    [Fact]
    public async Task Crash08_Cp2CompletedBeforeRollingCompletion_DoesNotReplayCompletedBlock()
    {
        using var fixture = new CoordinatorFixture();
        var executor = new ScriptedExecutor(fixture.CompleteExecution);
        RollingStationCoordinator coordinator = fixture.CreateCoordinator(
            executor,
            faultInjector: new ThrowOnceFault(RollingCoordinatorCheckpoint.AfterExecutorReturn, 1));

        await Assert.ThrowsAsync<RollingCoordinatorSimulatedCrashException>(() =>
            coordinator.RunAsync(fixture.ConfigurationPath, false, CancellationToken.None));

        var resumedExecutor = new ScriptedExecutor(fixture.StopExecution);
        RollingStationRunResult resumed = await fixture.CreateCoordinator(resumedExecutor).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal([2L], resumedExecutor.Calls);
        Assert.Equal(2, resumed.FinalState!.ActiveBlockSequence);
    }

    [Fact]
    public async Task Crash09_RollingCompletionBeforeNextClaim_AdvancesExactlyOnce()
    {
        using var fixture = new CoordinatorFixture();
        var completing = new ScriptedExecutor(fixture.CompleteExecution);
        RollingStationCoordinator coordinator = fixture.CreateCoordinator(
            completing,
            faultInjector: new ThrowOnceFault(
                RollingCoordinatorCheckpoint.AfterRollingCompletionPersistence,
                1));

        await Assert.ThrowsAsync<RollingCoordinatorSimulatedCrashException>(() =>
            coordinator.RunAsync(fixture.ConfigurationPath, false, CancellationToken.None));
        RollingStationRuntimeState advanced = fixture.RollingStore.Read(fixture.RollingStatePath);
        Assert.Equal(1, advanced.LastCompletedBlockSequence);
        Assert.Null(advanced.ActiveBlockSequence);

        var next = new ScriptedExecutor(fixture.StopExecution);
        await fixture.CreateCoordinator(next).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal([2L], next.Calls);
    }

    [Fact]
    public async Task Crash10_NextClaimBeforeFirstItem_RecoversNextWithoutReplayingPriorBlock()
    {
        using var fixture = new CoordinatorFixture();
        var complete = new ScriptedExecutor(fixture.CompleteExecution);
        RollingStationCoordinator coordinator = fixture.CreateCoordinator(
            complete,
            faultInjector: new ThrowOnceFault(RollingCoordinatorCheckpoint.AfterClaimPersistence, 2));

        await Assert.ThrowsAsync<RollingCoordinatorSimulatedCrashException>(() =>
            coordinator.RunAsync(fixture.ConfigurationPath, false, CancellationToken.None));
        Assert.Equal(2, fixture.RollingStore.Read(fixture.RollingStatePath).ActiveBlockSequence);

        var resumed = new ScriptedExecutor(fixture.StopExecution);
        await fixture.CreateCoordinator(resumed).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal([2L], resumed.Calls);
    }

    [Fact]
    public async Task Crash11_CoordinatorDiesAfterNextBlockStarts_ResumesThatBlock()
    {
        using var fixture = new CoordinatorFixture();
        int calls = 0;
        var executor = new ScriptedExecutor(async (sequence, configuration, plan, token) =>
        {
            calls++;
            if (sequence == 1)
            {
                return await fixture.CompleteExecution(sequence, configuration, plan, token);
            }

            await fixture.StationStore.WriteAsync(
                fixture.StationStatePath,
                fixture.CreateStationState(plan, StationState.Broadcasting, completedIndex: null),
                CancellationToken.None);
            throw new RollingCoordinatorSimulatedCrashException("crash after next block starts");
        });

        await Assert.ThrowsAsync<RollingCoordinatorSimulatedCrashException>(() =>
            fixture.CreateCoordinator(executor).RunAsync(
                fixture.ConfigurationPath,
                false,
                CancellationToken.None));
        Assert.Equal(2, calls);

        var resumed = new ScriptedExecutor(fixture.StopExecution);
        await fixture.CreateCoordinator(resumed).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);
        Assert.Equal([2L], resumed.Calls);
    }

    [Fact]
    public async Task Crash12_ManifestGrowthDuringExecution_DoesNotChangeActiveClaim()
    {
        using var fixture = new CoordinatorFixture(initialBlockCount: 2, availableBlockCount: 3);
        string? activeId = null;
        var executor = new ScriptedExecutor(async (sequence, configuration, plan, token) =>
        {
            activeId = fixture.RollingStore.Read(fixture.RollingStatePath).ActiveBlockId;
            fixture.PublishThrough(3);
            return await fixture.StopExecution(sequence, configuration, plan, token);
        });

        RollingStationRunResult result = await fixture.CreateCoordinator(executor).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(fixture.Resolved[1].Block.BlockId, activeId);
        Assert.Equal(fixture.Resolved[1].Block.BlockId, result.FinalState!.ActiveBlockId);
        Assert.Equal(3, fixture.PlanStore.Manifest.Blocks!.Count);
    }

    [Fact]
    public async Task Crash13_MissingNextBlock_WaitsCancellablyWithoutReplayingPreviousBlock()
    {
        using var fixture = new CoordinatorFixture(initialBlockCount: 1, availableBlockCount: 1);
        using var cancellation = new CancellationTokenSource();
        var executor = new ScriptedExecutor(fixture.CompleteExecution);
        RollingStationCoordinator coordinator = fixture.CreateCoordinator(
            executor,
            delay: (delay, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            });

        RollingStationRunResult result = await coordinator.RunAsync(
            fixture.ConfigurationPath,
            false,
            cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([1L], executor.Calls);
        Assert.Equal(RollingStationPhase.WaitingForBlock, result.FinalState!.Phase);
        Assert.Equal(1, result.FinalState.LastCompletedBlockSequence);
        Assert.Null(result.FinalState.ActiveBlockSequence);
    }

    [Fact]
    public async Task MissingBlockPolling_UsesFiveFifteenThirtyThenSixtySecondCap()
    {
        using var fixture = new CoordinatorFixture(initialBlockCount: 0, availableBlockCount: 0);
        using var cancellation = new CancellationTokenSource();
        var observed = new List<TimeSpan>();
        RollingStationCoordinator coordinator = fixture.CreateCoordinator(
            new ScriptedExecutor(),
            delay: (delay, token) =>
            {
                observed.Add(delay);
                if (observed.Count == 5)
                {
                    cancellation.Cancel();
                    return Task.FromCanceled(cancellation.Token);
                }

                return Task.CompletedTask;
            });

        RollingStationRunResult result = await coordinator.RunAsync(
            fixture.ConfigurationPath,
            false,
            cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [5d, 15d, 30d, 60d, 60d],
            observed.Select(value => value.TotalSeconds));
    }

    [Fact]
    public async Task Crash14_CorruptExactNextBlock_IsPermanentAndNoReplacementSequenceIsUsed()
    {
        using var fixture = new CoordinatorFixture();
        fixture.Resolver.Failures[2] = new InvalidDataException("block 2 identity mismatch");
        var executor = new ScriptedExecutor(fixture.CompleteExecution);

        RollingStationRunResult result = await fixture.CreateCoordinator(executor).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(StationExitCodes.PermanentStartupFailure, result.ExitCode);
        Assert.Equal([1L], executor.Calls);
        Assert.Equal(1, result.FinalState!.LastCompletedBlockSequence);
        Assert.Null(result.FinalState.ActiveBlockSequence);
        Assert.Equal(RollingStationFailureDisposition.Permanent, result.FinalState.FailureDisposition);
    }

    [Fact]
    public async Task Crash15_UnavailableMediaMount_IsRestartableAndRetainsClaim()
    {
        using var fixture = new CoordinatorFixture();
        var claimCrash = new ThrowOnceFault(RollingCoordinatorCheckpoint.AfterClaimPersistence, 1);
        await Assert.ThrowsAsync<RollingCoordinatorSimulatedCrashException>(() =>
            fixture.CreateCoordinator(new ScriptedExecutor(), faultInjector: claimCrash).RunAsync(
                fixture.ConfigurationPath,
                false,
                CancellationToken.None));
        fixture.Resolver.Failures[1] = new RollingMediaUnavailableException("media mount unavailable");

        RollingStationRunResult result = await fixture.CreateCoordinator(new ScriptedExecutor()).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, result.FinalState!.ActiveBlockSequence);
        Assert.Equal(RollingStationFailureDisposition.Restartable, result.FinalState.FailureDisposition);
    }

    [Fact]
    public async Task Crash16_InvalidVisibleProgrammingConfiguration_DoesNotBlockPreparedExecution()
    {
        using var fixture = new CoordinatorFixture();
        string programmingPath = Path.Combine(fixture.MediaRoot, "catalog", "programming.json");
        Directory.CreateDirectory(Path.GetDirectoryName(programmingPath)!);
        File.WriteAllText(programmingPath, "{ invalid future programming");
        var executor = new ScriptedExecutor(fixture.StopExecution);

        RollingStationRunResult result = await fixture.CreateCoordinator(executor).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([1L], executor.Calls);
    }

    [Fact]
    public async Task Crash17_LiveStaticStationBlocksInitialRollingCutoverPermanently()
    {
        using var fixture = new CoordinatorFixture(livePids: [41]);
        StationRuntimeState staticState = fixture.CreateForeignStationState(StationState.Broadcasting, 41);
        await fixture.StationStore.WriteAsync(
            fixture.StationStatePath,
            staticState,
            CancellationToken.None);
        var executor = new ScriptedExecutor();

        RollingStationRunResult result = await fixture.CreateCoordinator(executor).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(StationExitCodes.PermanentStartupFailure, result.ExitCode);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task Crash18_TwoCoordinatorsCannotMakeCompetingClaims()
    {
        using var fixture = new CoordinatorFixture();
        var provider = new RollingCoordinatorLockProvider();
        using IRollingCoordinatorLock held = provider.Acquire(fixture.StationStatePath);
        var executor = new ScriptedExecutor();

        RollingStationRunResult result = await fixture.CreateCoordinator(executor).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(StationExitCodes.PermanentStartupFailure, result.ExitCode);
        Assert.Empty(executor.Calls);
        Assert.Null(fixture.RollingStore.ReadIfExists(fixture.RollingStatePath));
    }

    [Fact]
    public async Task Crash19_ContradictoryCp2AndRollingStateRefusesUnsafeExecution()
    {
        using var fixture = new CoordinatorFixture();
        await Assert.ThrowsAsync<RollingCoordinatorSimulatedCrashException>(() =>
            fixture.CreateCoordinator(
                new ScriptedExecutor(),
                faultInjector: new ThrowOnceFault(
                    RollingCoordinatorCheckpoint.AfterClaimPersistence,
                    1)).RunAsync(fixture.ConfigurationPath, false, CancellationToken.None));
        await fixture.StationStore.WriteAsync(
            fixture.StationStatePath,
            fixture.CreateForeignStationState(StationState.Stopped, 41),
            CancellationToken.None);

        RollingStationRunResult result = await fixture.CreateCoordinator(new ScriptedExecutor()).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(StationExitCodes.PermanentStartupFailure, result.ExitCode);
        Assert.Contains("different", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Crash20_PreviouslyClaimedBlockRemovedFromManifestIsPermanent()
    {
        using var fixture = new CoordinatorFixture();
        await Assert.ThrowsAsync<RollingCoordinatorSimulatedCrashException>(() =>
            fixture.CreateCoordinator(
                new ScriptedExecutor(),
                faultInjector: new ThrowOnceFault(
                    RollingCoordinatorCheckpoint.AfterClaimPersistence,
                    1)).RunAsync(fixture.ConfigurationPath, false, CancellationToken.None));
        fixture.PublishThrough(0);

        RollingStationRunResult result = await fixture.CreateCoordinator(new ScriptedExecutor()).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(StationExitCodes.PermanentStartupFailure, result.ExitCode);
        Assert.Contains("no longer present", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviouslyCompletedBlockRemovedFromManifestIsAlsoPermanent()
    {
        using var fixture = new CoordinatorFixture();
        await Assert.ThrowsAsync<RollingCoordinatorSimulatedCrashException>(() =>
            fixture.CreateCoordinator(
                new ScriptedExecutor(fixture.CompleteExecution),
                faultInjector: new ThrowOnceFault(
                    RollingCoordinatorCheckpoint.AfterRollingCompletionPersistence,
                    1)).RunAsync(fixture.ConfigurationPath, false, CancellationToken.None));
        fixture.PublishThrough(0);

        RollingStationRunResult result = await fixture.CreateCoordinator(new ScriptedExecutor()).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(StationExitCodes.PermanentStartupFailure, result.ExitCode);
        Assert.Contains("last completed", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StoppedStaticCutover_RequiresExplicitFirstRunOptionAndPersistsAudit()
    {
        using var fixture = new CoordinatorFixture();
        StationRuntimeState stoppedStatic = fixture.CreateForeignStationState(StationState.Stopped, 41);
        await fixture.StationStore.WriteAsync(
            fixture.StationStatePath,
            stoppedStatic,
            CancellationToken.None);

        RollingStationRunResult refused = await fixture.CreateCoordinator(new ScriptedExecutor()).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);
        Assert.Equal(StationExitCodes.PermanentStartupFailure, refused.ExitCode);
        Assert.Null(fixture.RollingStore.ReadIfExists(fixture.RollingStatePath));

        var executor = new ScriptedExecutor(fixture.StopExecution);
        RollingStationRunResult accepted = await fixture.CreateCoordinator(executor).RunAsync(
            fixture.ConfigurationPath,
            true,
            CancellationToken.None);

        Assert.Equal(0, accepted.ExitCode);
        Assert.Equal(stoppedStatic.QueueId, accepted.FinalState!.InitialCutoverSourceQueueId);
        Assert.NotNull(accepted.FinalState.InitialCutoverAcceptedAtUtc);
    }

    [Fact]
    public async Task StoppedStaticCutover_CannotOverrideLegacyInterruptedOrExistingRollingState()
    {
        using var fixture = new CoordinatorFixture();
        StationRuntimeState legacy = fixture.CreateForeignStationState(StationState.Stopped, 41) with
        {
            SchemaVersion = StationRuntimeState.LegacySchemaVersion,
            QueueId = null,
            QueueItemCount = null,
            CurrentGlobalIndex = null,
            LastCompletedGlobalIndex = null,
            ResumeGlobalIndex = null,
            LastStartMode = null,
        };
        await fixture.StationStore.WriteAsync(fixture.StationStatePath, legacy, CancellationToken.None);
        RollingStationRunResult legacyResult = await fixture.CreateCoordinator(new ScriptedExecutor()).RunAsync(
            fixture.ConfigurationPath,
            true,
            CancellationToken.None);
        Assert.Equal(StationExitCodes.PermanentStartupFailure, legacyResult.ExitCode);

        File.Delete(fixture.StationStatePath);
        await fixture.RollingStore.WriteAsync(
            fixture.RollingStatePath,
            new RollingStationRuntimeState
            {
                PlannerId = fixture.Configuration.PlannerId,
                Phase = RollingStationPhase.WaitingForBlock,
                UpdatedAtUtc = fixture.Now,
            },
            CancellationToken.None);
        RollingStationRunResult existing = await fixture.CreateCoordinator(new ScriptedExecutor()).RunAsync(
            fixture.ConfigurationPath,
            true,
            CancellationToken.None);
        Assert.Equal(StationExitCodes.PermanentStartupFailure, existing.ExitCode);
    }

    [Fact]
    public async Task StoppedStaticCutover_AuditSurvivesWaitingForFirstCommittedBlock()
    {
        using var fixture = new CoordinatorFixture(initialBlockCount: 0, availableBlockCount: 1);
        StationRuntimeState stoppedStatic = fixture.CreateForeignStationState(StationState.Stopped, 41);
        await fixture.StationStore.WriteAsync(
            fixture.StationStatePath,
            stoppedStatic,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        RollingStationRunResult waiting = await fixture.CreateCoordinator(
            new ScriptedExecutor(),
            delay: (delay, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            }).RunAsync(fixture.ConfigurationPath, true, cancellation.Token);

        Assert.Equal(RollingStationPhase.WaitingForBlock, waiting.FinalState!.Phase);
        Assert.Equal(stoppedStatic.QueueId, waiting.FinalState.InitialCutoverSourceQueueId);
        Assert.NotNull(waiting.FinalState.InitialCutoverAcceptedAtUtc);

        fixture.PublishThrough(1);
        var executor = new ScriptedExecutor(fixture.StopExecution);
        RollingStationRunResult started = await fixture.CreateCoordinator(executor).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(0, started.ExitCode);
        Assert.Equal([1L], executor.Calls);
        Assert.Equal(stoppedStatic.QueueId, started.FinalState!.InitialCutoverSourceQueueId);
    }

    [Fact]
    public async Task RuntimeFailureRetainsExactClaimAndIsRestartable()
    {
        using var fixture = new CoordinatorFixture();
        var executor = new ScriptedExecutor(fixture.FailExecution);

        RollingStationRunResult result = await fixture.CreateCoordinator(executor).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(RollingStationPhase.Failed, result.FinalState!.Phase);
        Assert.Equal(1, result.FinalState.ActiveBlockSequence);
        Assert.Equal(fixture.Resolved[1].Block.BlockId, result.FinalState.ActiveBlockId);
        Assert.Equal(fixture.Resolved[1].QueueId, result.FinalState.ActiveQueueId);
    }

    [Fact]
    public async Task ExecutorExitZeroWithoutPositiveDurableCompletion_IsPermanentSafetyFailure()
    {
        using var fixture = new CoordinatorFixture();
        var executor = new ScriptedExecutor(async (sequence, configuration, plan, token) =>
        {
            StationRuntimeState incomplete = fixture.CreateStationState(
                plan,
                StationState.Broadcasting,
                completedIndex: null);
            await fixture.StationStore.WriteAsync(
                fixture.StationStatePath,
                incomplete,
                CancellationToken.None);
            return new StationRunResult(0, incomplete);
        });

        RollingStationRunResult result = await fixture.CreateCoordinator(executor).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(StationExitCodes.PermanentStartupFailure, result.ExitCode);
        Assert.Contains("without durable positive", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.FinalState!.ActiveBlockSequence);
    }

    [Fact]
    public async Task RuntimeFailure_RedactsDestinationFromRollingStateAndDiagnostic()
    {
        using var fixture = new CoordinatorFixture();
        const string secret = "rtmps://example.invalid/live2/FAKE-SECRET";
        var executor = new ScriptedExecutor(async (sequence, configuration, plan, token) =>
        {
            StationRuntimeState state = fixture.CreateStationState(
                plan,
                StationState.Failed,
                completedIndex: null) with
            { LastError = $"failed output {secret}" };
            await fixture.StationStore.WriteAsync(
                fixture.StationStatePath,
                state,
                CancellationToken.None);
            return new StationRunResult(1, state, state.LastError);
        });

        RollingStationRunResult result = await fixture.CreateCoordinator(executor).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);
        string rollingJson = File.ReadAllText(fixture.RollingStatePath);
        string stationJson = File.ReadAllText(fixture.StationStatePath);

        Assert.DoesNotContain(secret, result.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, rollingJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, stationJson, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", rollingJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDuringExecutor_RetainsActiveClaimAndCp2Cursor()
    {
        using var fixture = new CoordinatorFixture();
        using var cancellation = new CancellationTokenSource();
        var executor = new ScriptedExecutor(async (sequence, configuration, plan, token) =>
        {
            StationRuntimeState stopped = fixture.CreateStationState(
                plan,
                StationState.Stopped,
                completedIndex: 0);
            await fixture.StationStore.WriteAsync(
                fixture.StationStatePath,
                stopped,
                CancellationToken.None);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        RollingStationRunResult result = await fixture.CreateCoordinator(executor).RunAsync(
            fixture.ConfigurationPath,
            false,
            cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(RollingStationPhase.Stopped, result.FinalState!.Phase);
        Assert.Equal(1, result.FinalState.ActiveBlockSequence);
        Assert.Equal(1, fixture.StationStore.Read(fixture.StationStatePath).ResumeGlobalIndex);
    }

    [Fact]
    public async Task CancellationAfterClaimBeforeExecution_WritesStoppedWithoutInventingPlayback()
    {
        using var fixture = new CoordinatorFixture();
        using var cancellation = new CancellationTokenSource();
        var executor = new ScriptedExecutor();
        RollingStationCoordinator coordinator = fixture.CreateCoordinator(
            executor,
            faultInjector: new CancelAtFault(
                RollingCoordinatorCheckpoint.AfterClaimPersistence,
                1,
                cancellation));

        RollingStationRunResult result = await coordinator.RunAsync(
            fixture.ConfigurationPath,
            false,
            cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(RollingStationPhase.Stopped, result.FinalState!.Phase);
        Assert.Equal(1, result.FinalState.ActiveBlockSequence);
        Assert.Empty(executor.Calls);
        Assert.Null(fixture.StationStore.ReadIfExists(fixture.StationStatePath));
    }

    [Fact]
    public async Task SimulatedThreeBlockExecution_CompletesInOrderThenWaitsWithoutReplay()
    {
        using var fixture = new CoordinatorFixture();
        using var cancellation = new CancellationTokenSource();
        var executor = new ScriptedExecutor(fixture.CompleteExecution);
        RollingStationCoordinator coordinator = fixture.CreateCoordinator(
            executor,
            delay: (delay, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            });

        RollingStationRunResult result = await coordinator.RunAsync(
            fixture.ConfigurationPath,
            false,
            cancellation.Token);

        Assert.Equal([1L, 2L, 3L], executor.Calls);
        Assert.Equal(3, result.FinalState!.LastCompletedBlockSequence);
        Assert.Equal(fixture.Resolved[3].Block.BlockId, result.FinalState.LastCompletedBlockId);
        Assert.Equal(RollingStationPhase.WaitingForBlock, result.FinalState.Phase);
    }

    [Fact]
    public async Task StaticCp2LockPreventsRollingProductionExecutorFromStartingBroadcastRunner()
    {
        using var fixture = new CoordinatorFixture();
        StationRuntimeState completed = fixture.CreateStationState(
            fixture.Resolved[1].BroadcastPlan,
            StationState.Completed,
            fixture.Resolved[1].BroadcastPlan.Items.Count - 1);
        await fixture.StationStore.WriteAsync(
            fixture.StationStatePath,
            completed,
            CancellationToken.None);
        var runner = new NeverCalledBroadcastRunner();
        var supervisor = new StationSupervisor(
            runner,
            fixture.StationStore,
            processId: () => 900,
            resumePlanner: new StationResumePlanner(new FixedProcessExistence()));
        var executor = new StationSupervisorRollingBlockExecutor(
            supervisor,
            "rtmps://example.invalid/live2/FAKE-KEY");
        using var heldCp2Lock = new FileStream(
            fixture.StationStatePath + ".lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        RollingStationRunResult result = await fixture.CreateCoordinator(executor).RunAsync(
            fixture.ConfigurationPath,
            false,
            CancellationToken.None);

        Assert.Equal(StationExitCodes.PermanentStartupFailure, result.ExitCode);
        Assert.False(runner.Called);
    }

    private sealed class CoordinatorFixture : IDisposable
    {
        private readonly IReadOnlyCollection<int> _livePids;

        public CoordinatorFixture(
            int initialBlockCount = 3,
            int availableBlockCount = 3,
            IReadOnlyCollection<int>? livePids = null)
        {
            Root = Directory.CreateTempSubdirectory("nzytetv-rolling-coordinator-").FullName;
            MediaRoot = Directory.CreateDirectory(Path.Combine(Root, "media")).FullName;
            LibraryRoot = Directory.CreateDirectory(Path.Combine(MediaRoot, "library")).FullName;
            StationStatePath = Path.Combine(Root, "runtime", "state.json");
            RollingStatePath = Path.Combine(Root, "runtime", "rolling-state.json");
            ConfigurationPath = Path.Combine(Root, "rolling-station.json");
            string staticPlaylist = Path.Combine(Root, "static-playlist.json");
            File.WriteAllText(staticPlaylist, "{}");
            StationConfiguration = new StationConfiguration
            {
                MediaRoot = MediaRoot,
                LibraryRoot = LibraryRoot,
                StatePath = StationStatePath,
                Playlists = [staticPlaylist],
            };
            Configuration = new RollingStationConfiguration
            {
                StationConfigPath = Path.Combine(Root, "station.json"),
                PlannerId = "00112233445566778899aabbccddeeff",
                RollingStatePath = RollingStatePath,
            };
            File.WriteAllText(ConfigurationPath, "{}");
            File.WriteAllText(Configuration.StationConfigPath, "{}");
            _livePids = livePids ?? [];

            Resolved = Enumerable.Range(1, availableBlockCount)
                .ToDictionary(sequence => (long)sequence, CreateResolved);
            Resolver = new FakeResolver(Resolved);
            PlanStore = new MutablePlanStore(CreateManifest(initialBlockCount));
        }

        public DateTimeOffset Now { get; } = DateTimeOffset.Parse("2026-10-01T12:00:00Z");

        public string Root { get; }

        public string MediaRoot { get; }

        public string LibraryRoot { get; }

        public string StationStatePath { get; }

        public string RollingStatePath { get; }

        public string ConfigurationPath { get; }

        public StationConfiguration StationConfiguration { get; }

        public RollingStationConfiguration Configuration { get; }

        public StationStateStore StationStore { get; } = new();

        public RollingStationStateStore RollingStore { get; } = new();

        public IReadOnlyDictionary<long, ResolvedRollingCommittedBlock> Resolved { get; }

        public FakeResolver Resolver { get; }

        public MutablePlanStore PlanStore { get; }

        public Dictionary<long, StationRuntimeState> CompletedStates { get; } = [];

        public RollingStationCoordinator CreateCoordinator(
            IRollingBlockExecutor executor,
            IRollingCoordinatorFaultInjector? faultInjector = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null,
            IRollingStationStateStore? rollingStateStore = null,
            IRollingCoordinatorLockProvider? lockProvider = null) => new(
            executor,
            rollingConfigurationLoader: new FixedRollingConfigurationLoader(Configuration),
            stationConfigurationLoader: new FixedStationConfigurationLoader(StationConfiguration),
            rollingStateStore: rollingStateStore ?? RollingStore,
            stationStateStore: StationStore,
            planStore: PlanStore,
            blockResolver: Resolver,
            lockProvider: lockProvider,
            processExistence: new FixedProcessExistence([.. _livePids]),
            faultInjector: faultInjector,
            timeProvider: new FixedTimeProvider(Now),
            processId: () => 900,
            delay: delay ?? ((value, token) => Task.Delay(value, token)));

        public void PublishThrough(int count) => PlanStore.Manifest = CreateManifest(count);

        public async Task<StationRunResult> CompleteExecution(
            long sequence,
            StationConfiguration configuration,
            BroadcastPlan plan,
            CancellationToken cancellationToken)
        {
            StationRuntimeState state = CreateStationState(
                plan,
                StationState.Completed,
                plan.Items.Count - 1);
            await StationStore.WriteAsync(StationStatePath, state, CancellationToken.None);
            CompletedStates[sequence] = state;
            return new StationRunResult(0, state);
        }

        public async Task<StationRunResult> PositiveNonterminalCompletion(
            long sequence,
            StationConfiguration configuration,
            BroadcastPlan plan,
            CancellationToken cancellationToken)
        {
            StationRuntimeState state = CreateStationState(
                plan,
                StationState.Broadcasting,
                plan.Items.Count - 1);
            await StationStore.WriteAsync(StationStatePath, state, CancellationToken.None);
            return new StationRunResult(1, state, "simulated crash after final item");
        }

        public async Task<StationRunResult> StopExecution(
            long sequence,
            StationConfiguration configuration,
            BroadcastPlan plan,
            CancellationToken cancellationToken)
        {
            StationRuntimeState state = CreateStationState(
                plan,
                StationState.Stopped,
                completedIndex: null);
            await StationStore.WriteAsync(StationStatePath, state, CancellationToken.None);
            return new StationRunResult(0, state);
        }

        public async Task<StationRunResult> StopAfterFirstCompletion(
            long sequence,
            StationConfiguration configuration,
            BroadcastPlan plan,
            CancellationToken cancellationToken)
        {
            StationRuntimeState state = CreateStationState(
                plan,
                StationState.Stopped,
                completedIndex: 0);
            await StationStore.WriteAsync(StationStatePath, state, CancellationToken.None);
            return new StationRunResult(0, state);
        }

        public async Task<StationRunResult> FailExecution(
            long sequence,
            StationConfiguration configuration,
            BroadcastPlan plan,
            CancellationToken cancellationToken)
        {
            StationRuntimeState state = CreateStationState(
                plan,
                StationState.Failed,
                completedIndex: null) with
            { LastError = "simulated runtime failure" };
            await StationStore.WriteAsync(StationStatePath, state, CancellationToken.None);
            return new StationRunResult(1, state, state.LastError);
        }

        public StationRuntimeState CreateStationState(
            BroadcastPlan plan,
            StationState stationState,
            int? completedIndex)
        {
            bool fullyCompleted = completedIndex == plan.Items.Count - 1;
            int? resume = fullyCompleted ? null : (completedIndex ?? -1) + 1;
            return new StationRuntimeState
            {
                StationState = stationState,
                BroadcastState = stationState switch
                {
                    StationState.Completed => StationBroadcastState.Completed,
                    StationState.Stopped => StationBroadcastState.Stopped,
                    StationState.Failed => StationBroadcastState.Failed,
                    _ => StationBroadcastState.Broadcasting,
                },
                StationPid = 41,
                StartedAtUtc = Now - TimeSpan.FromMinutes(5),
                LastHeartbeatUtc = Now,
                MediaRoot = MediaRoot,
                LibraryRoot = LibraryRoot,
                TotalPlaylistCount = 1,
                QueuedPlaylistCount = stationState == StationState.Completed ? 0 : 1,
                QueueId = BroadcastQueueIdentity.Create(plan),
                QueueItemCount = plan.Items.Count,
                CurrentGlobalIndex = completedIndex ?? 0,
                LastCompletedGlobalIndex = completedIndex,
                ResumeGlobalIndex = resume,
                LastStartMode = StationStartMode.Fresh,
                CompletedAtUtc = stationState == StationState.Completed ? Now : null,
                StoppedAtUtc = stationState == StationState.Stopped ? Now : null,
            };
        }

        public StationRuntimeState CreateForeignStationState(StationState state, int pid)
        {
            BroadcastPlan plan = Resolved[1].BroadcastPlan;
            return CreateStationState(plan, state, completedIndex: null) with
            {
                StationPid = pid,
                QueueId = new string('f', 64),
            };
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private ResolvedRollingCommittedBlock CreateResolved(int sequence)
        {
            string playlistPath = Path.Combine(Root, $"block-{sequence}.json");
            File.WriteAllText(playlistPath, "{}");
            var playlistItems = Enumerable.Range(1, 3).Select(index => new PlaylistItem(
                index,
                $"asset-{sequence}-{index}",
                $"song-{sequence}-{index}",
                $"Block {sequence} item {index}",
                AssetTypes.MusicVideo,
                null,
                $"music/asset-{sequence}-{index}.mp4",
                60,
                (index - 1) * 60)).ToArray();
            var playlist = new PlaylistDocument
            {
                GeneratedAtUtc = Now,
                ScheduleStartUtc = Now.AddHours((sequence - 1) * 6),
                Seed = sequence,
                TargetDurationSeconds = 180,
                ActualDurationSeconds = 180,
                Items = playlistItems,
            };
            BroadcastPlanItem[] planItems = playlistItems.Select(item => new BroadcastPlanItem(
                playlistPath,
                item.Sequence,
                item.AssetId,
                item.RelativePath,
                Path.Combine(LibraryRoot, item.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                item.DurationSeconds,
                item.Title,
                item.Type)).ToArray();
            var plan = new BroadcastPlan(
                LibraryRoot,
                [playlistPath],
                planItems,
                [],
                planItems.Length,
                planItems.Sum(item => item.DurationSeconds))
            {
                PlaylistContentHashes = [new string((char)('0' + sequence), 64)],
            };
            RollingCommittedBlock block = CreateBlock(sequence);
            return new ResolvedRollingCommittedBlock(
                block,
                playlistPath,
                playlist,
                new RollingPlanningInputSnapshot(),
                plan,
                BroadcastQueueIdentity.Create(plan));
        }

        private RollingProgrammingManifest CreateManifest(int count)
        {
            RollingArtifactReference genesis = new(
                $"history/{new string('0', 64)}.json",
                new string('0', 64));
            IReadOnlyList<RollingCommittedBlock> blocks = Enumerable.Range(1, count)
                .Select(sequence => Resolved[(long)sequence].Block)
                .ToArray();
            return new RollingProgrammingManifest
            {
                PlannerId = Configuration.PlannerId,
                BaseSeed = 7,
                TargetBlockDurationSeconds = 21600,
                TargetPreparedBlockCount = 3,
                NextSequence = count + 1,
                GenesisHistory = genesis,
                HistoryHead = count == 0 ? genesis : blocks[^1].HistoryAfter,
                Blocks = blocks,
                InitializedAtUtc = Now,
            };
        }

        private RollingCommittedBlock CreateBlock(int sequence)
        {
            string blockId = new string((char)('a' + sequence - 1), 64);
            string? parentId = sequence == 1
                ? null
                : new string((char)('a' + sequence - 2), 64);
            string beforeHash = new string((char)('0' + sequence - 1), 64);
            string afterHash = new string((char)('0' + sequence), 64);
            return new RollingCommittedBlock
            {
                Sequence = sequence,
                BlockId = blockId,
                ParentBlockId = parentId,
                Seed = sequence,
                PlaylistPath = $"blocks/{blockId}/playlist.json",
                PlaylistSha256 = new string('1', 64),
                DescriptorPath = $"blocks/{blockId}/block.json",
                DescriptorSha256 = new string('2', 64),
                InputSnapshotPath = $"blocks/{blockId}/input.json",
                InputSnapshotSha256 = new string('3', 64),
                TargetDurationSeconds = 21600,
                ActualDurationSeconds = 21600,
                ScheduleStartUtc = Now.AddHours((sequence - 1) * 6),
                ScheduleEndUtc = Now.AddHours(sequence * 6),
                ItemCount = 3,
                HistoryBefore = new RollingArtifactReference(
                    $"history/{beforeHash}.json",
                    beforeHash),
                HistoryAfter = new RollingArtifactReference(
                    $"history/{afterHash}.json",
                    afterHash),
                CatalogSnapshotHash = new string('4', 64),
                ProgrammingSnapshotHash = new string('5', 64),
                InventorySnapshotHash = new string('6', 64),
                ProgrammingSchemaVersion = 1,
                ProgrammingRevision = 1,
                PlannerAlgorithmVersion = RollingProgrammingPolicy.PlannerAlgorithmVersion,
                GeneratedAtUtc = Now,
            };
        }
    }

    private sealed class ScriptedExecutor : IRollingBlockExecutor
    {
        private readonly Func<long, StationConfiguration, BroadcastPlan, CancellationToken, Task<StationRunResult>>
            _execute;

        public ScriptedExecutor(
            Func<long, StationConfiguration, BroadcastPlan, CancellationToken, Task<StationRunResult>>? execute = null)
        {
            _execute = execute ?? ((sequence, configuration, plan, token) =>
                throw new InvalidOperationException("Unexpected executor invocation."));
        }

        public List<long> Calls { get; } = [];

        public Task<StationRunResult> RunAsync(
            StationConfiguration configuration,
            BroadcastPlan plan,
            CancellationToken cancellationToken)
        {
            long sequence = long.Parse(
                Path.GetFileNameWithoutExtension(plan.PlaylistPaths.Single())
                    .Replace("block-", string.Empty, StringComparison.Ordinal));
            Calls.Add(sequence);
            return _execute(sequence, configuration, plan, cancellationToken);
        }
    }

    private sealed class CoordinatorFakeMaintainer(CoordinatorFixture fixture)
        : IRollingBlockMaintainer
    {
        public List<long> Targets { get; } = [];

        public Queue<Exception> Exceptions { get; } = [];

        public Task<RollingMaintainResult> EnsureCommittedThroughAsync(
            string mediaRoot,
            long requiredHighestSequence,
            CancellationToken cancellationToken)
        {
            Targets.Add(requiredHighestSequence);
            if (Exceptions.TryDequeue(out Exception? exception))
            {
                throw exception;
            }

            fixture.PublishThrough(Math.Max(
                fixture.PlanStore.Manifest.Blocks!.Count,
                checked((int)requiredHighestSequence)));
            return Task.FromResult(new RollingMaintainResult(
                RollingProgrammingPaths.FromMediaRoot(mediaRoot),
                fixture.PlanStore.Manifest,
                0,
                0,
                true));
        }
    }

    private sealed class ThrowOnceFault(
        RollingCoordinatorCheckpoint checkpoint,
        long sequence) : IRollingCoordinatorFaultInjector
    {
        private bool _thrown;

        public void Reach(RollingCoordinatorCheckpoint current, long currentSequence)
        {
            if (!_thrown && current == checkpoint && currentSequence == sequence)
            {
                _thrown = true;
                throw new RollingCoordinatorSimulatedCrashException(
                    $"Simulated crash at {current} for block {currentSequence}.");
            }
        }
    }

    private sealed class CancelAtFault(
        RollingCoordinatorCheckpoint checkpoint,
        long sequence,
        CancellationTokenSource cancellation) : IRollingCoordinatorFaultInjector
    {
        public void Reach(RollingCoordinatorCheckpoint current, long currentSequence)
        {
            if (current == checkpoint && currentSequence == sequence)
            {
                cancellation.Cancel();
            }
        }
    }

    private sealed class FakeResolver(
        IReadOnlyDictionary<long, ResolvedRollingCommittedBlock> resolved)
        : IRollingCommittedBlockResolver
    {
        public Dictionary<long, Exception> Failures { get; } = [];

        public ResolvedRollingCommittedBlock ResolveManifestBlock(
            RollingProgrammingPaths paths,
            RollingProgrammingManifest manifest,
            long sequence,
            string libraryRoot)
        {
            if (Failures.TryGetValue(sequence, out Exception? failure))
            {
                throw failure;
            }

            if (sequence < 1 || sequence > manifest.Blocks!.Count)
            {
                throw new RollingBlockNotAvailableException(sequence);
            }

            RollingCommittedBlock manifestBlock = manifest.Blocks[(int)sequence - 1];
            if (!resolved.TryGetValue(sequence, out ResolvedRollingCommittedBlock? value))
            {
                throw new RollingBlockNotAvailableException(sequence);
            }

            return value with { Block = manifestBlock };
        }

        public ResolvedRollingCommittedBlock VerifyBlock(
            RollingProgrammingPaths paths,
            string plannerId,
            RollingCommittedBlock block,
            string libraryRoot) => ResolveManifestBlock(
                paths,
                new RollingProgrammingManifest { Blocks = [block] },
                block.Sequence,
                libraryRoot);
    }

    private sealed class MutablePlanStore(RollingProgrammingManifest manifest) : IRollingPlanStore
    {
        public RollingProgrammingManifest Manifest { get; set; } = manifest;

        public RollingProgrammingManifest LoadManifest(string path) => Manifest;

        public RollingProgrammingManifest? LoadManifestIfExists(string path) => Manifest;

        public Task WriteManifestAsync(
            string path,
            RollingProgrammingManifest value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task WriteStagedAsync<T>(string path, T value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task WriteImmutableAsync<T>(string path, T value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public T Read<T>(string path, string description) => throw new NotSupportedException();

        public string ReadText(string path) => throw new NotSupportedException();
    }

    private sealed class FixedRollingConfigurationLoader(RollingStationConfiguration configuration)
        : IRollingStationConfigurationLoader
    {
        public RollingStationConfiguration Load(string path) => configuration;
    }

    private sealed class FixedStationConfigurationLoader(StationConfiguration configuration)
        : IStationConfigurationLoader
    {
        public StationConfiguration Load(string path) => configuration;
    }

    private sealed class FixedProcessExistence(params int[] livePids) : IProcessExistence
    {
        private readonly HashSet<int> _livePids = [.. livePids];

        public bool Exists(int processId) => _livePids.Contains(processId);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NeverCalledBroadcastRunner : IStationBroadcastRunner
    {
        public bool Called { get; private set; }

        public Task<BroadcastRecoveryResult> RunAsync(
            BroadcastPlan plan,
            string destination,
            int startItemIndex,
            Action<string>? onFfmpegOutput,
            Action<BroadcastRecoveryUpdate>? onUpdate,
            IBroadcastRuntimeObserver observer,
            CancellationToken cancellationToken)
        {
            Called = true;
            throw new InvalidOperationException("Broadcast runner must not be called while CP2 lock is held.");
        }
    }
}
