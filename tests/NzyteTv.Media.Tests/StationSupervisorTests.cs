using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class StationSupervisorTests
{
    private const string Destination = "rtmps://example.invalid/live2/SECRET-KEY";

    [Fact]
    public void RuntimeTracker_InitialStateIsStarting()
    {
        using var fixture = new SupervisorFixture();
        var tracker = new StationRuntimeTracker(fixture.Configuration, fixture.Plan, 123);

        StationRuntimeState state = tracker.Snapshot();

        Assert.Equal(StationState.Starting, state.StationState);
        Assert.Equal(StationBroadcastState.Starting, state.BroadcastState);
        Assert.Equal(123, state.StationPid);
        Assert.Equal(2, state.QueuedPlaylistCount);
        Assert.Null(state.FfmpegPid);
        Assert.Equal(StationRuntimeState.CurrentSchemaVersion, state.SchemaVersion);
        Assert.Equal(BroadcastQueueIdentity.Create(fixture.Plan), state.QueueId);
        Assert.Equal(2, state.QueueItemCount);
        Assert.Equal(0, state.CurrentGlobalIndex);
        Assert.Null(state.LastCompletedGlobalIndex);
        Assert.Equal(0, state.ResumeGlobalIndex);
        Assert.Equal(StationStartMode.Fresh, state.LastStartMode);
        Assert.Equal(0, state.ResumeCount);
    }

    [Fact]
    public void RuntimeTracker_BroadcastItemAndRecoveryEventsUpdateObservableState()
    {
        using var fixture = new SupervisorFixture();
        var tracker = new StationRuntimeTracker(fixture.Configuration, fixture.Plan, 123);

        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.BroadcastStarted,
            fixture.Plan.Items[0],
            GlobalItemIndex: 0));
        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.FfmpegProcessStarted,
            FfmpegPid: 1001));
        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.ItemCompleted,
            fixture.Plan.Items[0],
            GlobalItemIndex: 0));
        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.ItemChanged,
            fixture.Plan.Items[1],
            GlobalItemIndex: 1));

        StationRuntimeState playing = tracker.Snapshot();
        Assert.Equal(StationState.Broadcasting, playing.StationState);
        Assert.Equal(1001, playing.FfmpegPid);
        Assert.Equal(fixture.Plan.PlaylistPaths[1], playing.CurrentPlaylist);
        Assert.Equal(2, playing.CurrentPlaylistIndex);
        Assert.Equal(1, playing.CurrentSequence);
        Assert.Equal("Second title", playing.Title);
        Assert.Equal("animated-visual", playing.Type);
        Assert.Equal(1, playing.CurrentGlobalIndex);
        Assert.Equal(0, playing.LastCompletedGlobalIndex);
        Assert.Equal(1, playing.ResumeGlobalIndex);

        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.FfmpegProcessStopped,
            FfmpegPid: 1001));
        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.RecoveryStarted,
            fixture.Plan.Items[1],
            RecoveryAttempts: 1,
            GlobalItemIndex: 1));
        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.FfmpegProcessStarted,
            FfmpegPid: 1002));

        StationRuntimeState recovered = tracker.Snapshot();
        Assert.Equal(1002, recovered.FfmpegPid);
        Assert.Equal(1, recovered.RecoveryAttempts);
        Assert.Equal(StationBroadcastState.Broadcasting, recovered.BroadcastState);
        Assert.Equal(0, recovered.ResumeCount);
    }

    [Fact]
    public void RuntimeTracker_CompletionIsMonotonicAndDuplicateSafe()
    {
        using var fixture = new SupervisorFixture();
        var tracker = new StationRuntimeTracker(fixture.Configuration, fixture.Plan, 123);

        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.ItemCompleted,
            fixture.Plan.Items[1],
            GlobalItemIndex: 1));
        Assert.Null(tracker.Snapshot().LastCompletedGlobalIndex);
        Assert.Equal(0, tracker.Snapshot().ResumeGlobalIndex);

        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.ItemCompleted,
            fixture.Plan.Items[0],
            GlobalItemIndex: 0));
        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.ItemCompleted,
            fixture.Plan.Items[0],
            GlobalItemIndex: 0));

        Assert.Equal(0, tracker.Snapshot().LastCompletedGlobalIndex);
        Assert.Equal(1, tracker.Snapshot().ResumeGlobalIndex);

        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.ItemCompleted,
            fixture.Plan.Items[1],
            GlobalItemIndex: 1));

        Assert.Equal(1, tracker.Snapshot().LastCompletedGlobalIndex);
        Assert.Null(tracker.Snapshot().ResumeGlobalIndex);
    }

    [Fact]
    public async Task RunAsync_CallsBroadcastRunnerAndSuccessfulQueueCompletesWithZero()
    {
        using var fixture = new SupervisorFixture();
        var runner = new ScriptedBroadcastRunner(async (observer, cancellationToken) =>
        {
            observer.OnEvent(new BroadcastRuntimeEvent(
                BroadcastRuntimeEventKind.BroadcastStarted,
                fixture.Plan.Items[0]));
            observer.OnEvent(new BroadcastRuntimeEvent(
                BroadcastRuntimeEventKind.FfmpegProcessStarted,
                FfmpegPid: 501));
            observer.OnEvent(new BroadcastRuntimeEvent(
                BroadcastRuntimeEventKind.ItemChanged,
                fixture.Plan.Items[1]));
            await Task.Yield();
            observer.OnEvent(new BroadcastRuntimeEvent(
                BroadcastRuntimeEventKind.FfmpegProcessStopped,
                FfmpegPid: 501));
            observer.OnEvent(new BroadcastRuntimeEvent(
                BroadcastRuntimeEventKind.BroadcastCompleted,
                fixture.Plan.Items[1]));
            return new BroadcastRecoveryResult(0, 0, fixture.Plan.Items[1], string.Empty);
        });
        var store = new RecordingStateStore();
        var supervisor = new StationSupervisor(runner, store, processId: () => 77);

        StationRunResult result = await supervisor.RunAsync(
            fixture.Configuration,
            fixture.Plan,
            Destination,
            null,
            null,
            CancellationToken.None);

        Assert.True(runner.Called);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(StationState.Completed, result.FinalState.StationState);
        Assert.Equal(StationBroadcastState.Completed, result.FinalState.BroadcastState);
        Assert.Equal(0, result.FinalState.QueuedPlaylistCount);
        Assert.NotNull(result.FinalState.CompletedAtUtc);
        Assert.Null(result.FinalState.FfmpegPid);
        Assert.Equal(1, result.FinalState.LastCompletedGlobalIndex);
        Assert.Null(result.FinalState.ResumeGlobalIndex);
        Assert.Contains(store.Writes, state => state.StationState == StationState.Starting);
        Assert.Equal(StationState.Completed, store.Writes[^1].StationState);
    }

    [Fact]
    public async Task RunAsync_CancellationStopsHeartbeatAndWritesStoppedStateWithoutRetry()
    {
        using var fixture = new SupervisorFixture();
        var runner = new CancellableBroadcastRunner(fixture.Plan.Items[0]);
        var store = new RecordingStateStore();
        var supervisor = new StationSupervisor(
            runner,
            store,
            heartbeatInterval: TimeSpan.FromMilliseconds(20),
            processId: () => 77);
        using var cancellation = new CancellationTokenSource();

        Task<StationRunResult> task = supervisor.RunAsync(
            fixture.Configuration,
            fixture.Plan,
            Destination,
            null,
            null,
            cancellation.Token);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(35);
        cancellation.Cancel();
        StationRunResult result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        int writesAfterStop = store.Writes.Count;
        await Task.Delay(50);

        Assert.Equal(0, result.ExitCode);
        Assert.True(runner.CancellationObserved);
        Assert.Equal(0, runner.RecoveryAttempts);
        Assert.Equal(StationState.Stopped, result.FinalState.StationState);
        Assert.Equal(StationBroadcastState.Stopped, result.FinalState.BroadcastState);
        Assert.Null(result.FinalState.FfmpegPid);
        Assert.NotNull(result.FinalState.StoppedAtUtc);
        Assert.Contains(store.Writes, state => state.StationState == StationState.Stopping);
        Assert.Equal(StationState.Stopped, store.Writes[^1].StationState);
        Assert.Equal(writesAfterStop, store.Writes.Count);
    }

    [Fact]
    public async Task RunAsync_CleanStopPreservesDurableResumeCursor()
    {
        using var fixture = new SupervisorFixture();
        var runner = new ProgressingCancellableBroadcastRunner(fixture.Plan);
        var store = new RecordingStateStore();
        var supervisor = new StationSupervisor(runner, store, processId: () => 77);
        using var cancellation = new CancellationTokenSource();

        Task<StationRunResult> task = supervisor.RunAsync(
            fixture.Configuration,
            fixture.Plan,
            Destination,
            null,
            null,
            cancellation.Token);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        StationRunResult result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StationState.Stopped, result.FinalState.StationState);
        Assert.Equal(0, result.FinalState.LastCompletedGlobalIndex);
        Assert.Equal(1, result.FinalState.ResumeGlobalIndex);
        Assert.Equal(1, result.FinalState.CurrentGlobalIndex);
        Assert.Null(result.FinalState.FfmpegPid);
    }

    [Fact]
    public async Task RunAsync_HeartbeatUpdatesWhileBroadcastIsActive()
    {
        using var fixture = new SupervisorFixture();
        var runner = new ScriptedBroadcastRunner(async (observer, cancellationToken) =>
        {
            observer.OnEvent(new BroadcastRuntimeEvent(
                BroadcastRuntimeEventKind.FfmpegProcessStarted,
                FfmpegPid: 901));
            await Task.Delay(75, cancellationToken);
            observer.OnEvent(new BroadcastRuntimeEvent(
                BroadcastRuntimeEventKind.FfmpegProcessStopped,
                FfmpegPid: 901));
            return new BroadcastRecoveryResult(0, 0, fixture.Plan.Items[^1], string.Empty);
        });
        var store = new RecordingStateStore();
        var supervisor = new StationSupervisor(
            runner,
            store,
            heartbeatInterval: TimeSpan.FromMilliseconds(15),
            processId: () => 77);

        StationRunResult result = await supervisor.RunAsync(
            fixture.Configuration,
            fixture.Plan,
            Destination,
            null,
            null,
            CancellationToken.None);

        DateTimeOffset startedAt = store.Writes[0].StartedAtUtc;
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(store.Writes, state => state.LastHeartbeatUtc > startedAt);
    }

    [Fact]
    public async Task RunAsync_UnrecoverableFailureWritesFailedStateAndReturnsNonzero()
    {
        using var fixture = new SupervisorFixture();
        var runner = new ScriptedBroadcastRunner((observer, cancellationToken) => Task.FromResult(
            new BroadcastRecoveryResult(1, 10, fixture.Plan.Items[0], "network failure")));
        var store = new RecordingStateStore();
        var supervisor = new StationSupervisor(runner, store, processId: () => 77);

        StationRunResult result = await supervisor.RunAsync(
            fixture.Configuration,
            fixture.Plan,
            Destination,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(StationState.Failed, result.FinalState.StationState);
        Assert.Equal("network failure", result.FinalState.LastError);
        Assert.Equal(StationState.Failed, store.Writes[^1].StationState);
    }

    [Fact]
    public async Task RunAsync_DestinationIsRedactedFromFailureState()
    {
        using var fixture = new SupervisorFixture();
        var runner = new ScriptedBroadcastRunner((observer, cancellationToken) => Task.FromResult(
            new BroadcastRecoveryResult(
                1,
                10,
                fixture.Plan.Items[0],
                $"failed output {Destination}")));
        var store = new RecordingStateStore();
        var supervisor = new StationSupervisor(runner, store, processId: () => 77);

        StationRunResult result = await supervisor.RunAsync(
            fixture.Configuration,
            fixture.Plan,
            Destination,
            null,
            null,
            CancellationToken.None);

        string stateText = string.Join(
            Environment.NewLine,
            store.Writes.Select(state => state.LastError ?? string.Empty));
        Assert.DoesNotContain(Destination, stateText, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.FinalState.LastError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StationState.Stopped)]
    [InlineData(StationState.Broadcasting)]
    [InlineData(StationState.Failed)]
    public async Task RunAsync_MatchingDurableStateResumesWithNewPidAndTelemetry(
        StationState persistedStationState)
    {
        using var fixture = new SupervisorFixture();
        StationRuntimeState persisted = fixture.PersistedState(persistedStationState);
        var runner = new ScriptedBroadcastRunner((observer, cancellationToken) => Task.FromResult(
            new BroadcastRecoveryResult(1, 0, fixture.Plan.Items[1], "runtime failure")));
        var store = new RecordingStateStore(persisted);
        var supervisor = new StationSupervisor(
            runner,
            store,
            processId: () => 77,
            resumePlanner: new StationResumePlanner(new FixedProcessExistence()));

        StationRunResult result = await supervisor.RunAsync(
            fixture.Configuration,
            fixture.Plan,
            Destination,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(1, runner.StartItemIndex);
        Assert.Equal(77, store.Writes[0].StationPid);
        Assert.Equal(StationStartMode.Resume, store.Writes[0].LastStartMode);
        Assert.Equal(persisted.ResumeCount + 1, store.Writes[0].ResumeCount);
        Assert.NotNull(store.Writes[0].LastResumeAtUtc);
        Assert.Equal(0, result.FinalState.LastCompletedGlobalIndex);
        Assert.Equal(1, result.FinalState.ResumeGlobalIndex);
    }

    [Theory]
    [InlineData(StationState.Stopped, true)]
    [InlineData(StationState.Completed, false)]
    public async Task RunAsync_ChangedStoppedQueueOrCompletedRunStartsFresh(
        StationState priorState,
        bool changeQueue)
    {
        using var fixture = new SupervisorFixture();
        StationRuntimeState persisted = fixture.PersistedState(priorState) with
        {
            QueueId = changeQueue ? new string('b', 64) : BroadcastQueueIdentity.Create(fixture.Plan),
            CurrentGlobalIndex = 1,
            LastCompletedGlobalIndex = priorState == StationState.Completed ? 1 : 0,
            ResumeGlobalIndex = priorState == StationState.Completed ? null : 1,
        };
        var runner = new ScriptedBroadcastRunner((observer, cancellationToken) => Task.FromResult(
            new BroadcastRecoveryResult(1, 0, fixture.Plan.Items[0], "runtime failure")));
        var store = new RecordingStateStore(persisted);
        var supervisor = new StationSupervisor(runner, store, processId: () => 77);

        await supervisor.RunAsync(
            fixture.Configuration,
            fixture.Plan,
            Destination,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(0, runner.StartItemIndex);
        Assert.Equal(StationStartMode.Fresh, store.Writes[0].LastStartMode);
        Assert.Equal(0, store.Writes[0].ResumeCount);
    }

    [Fact]
    public async Task RunAsync_LegacyStateStartsFreshWithoutInferringPosition()
    {
        using var fixture = new SupervisorFixture();
        StationRuntimeState persisted = fixture.PersistedState(StationState.Stopped) with
        {
            SchemaVersion = StationRuntimeState.LegacySchemaVersion,
            QueueId = null,
            QueueItemCount = null,
            CurrentGlobalIndex = null,
            LastCompletedGlobalIndex = null,
            ResumeGlobalIndex = null,
            LastStartMode = null,
            ResumeCount = 0,
        };
        var runner = new ScriptedBroadcastRunner((observer, cancellationToken) => Task.FromResult(
            new BroadcastRecoveryResult(1, 0, fixture.Plan.Items[0], "runtime failure")));
        var store = new RecordingStateStore(persisted);

        await new StationSupervisor(runner, store, processId: () => 77).RunAsync(
            fixture.Configuration,
            fixture.Plan,
            Destination,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(0, runner.StartItemIndex);
        Assert.Equal(StationRuntimeState.CurrentSchemaVersion, store.Writes[0].SchemaVersion);
        Assert.Equal(StationStartMode.Fresh, store.Writes[0].LastStartMode);
    }

    [Fact]
    public async Task RunAsync_InterruptedChangedQueueRefusesBeforeBroadcastOrStateWrite()
    {
        using var fixture = new SupervisorFixture();
        StationRuntimeState persisted = fixture.PersistedState(StationState.Broadcasting) with
        {
            QueueId = new string('b', 64),
        };
        var runner = new ScriptedBroadcastRunner((observer, cancellationToken) => Task.FromResult(
            new BroadcastRecoveryResult(0, 0, null, string.Empty)));
        var store = new RecordingStateStore(persisted);
        var supervisor = new StationSupervisor(
            runner,
            store,
            processId: () => 77,
            resumePlanner: new StationResumePlanner(new FixedProcessExistence()));

        StationStartupException exception = await Assert.ThrowsAsync<StationStartupException>(() =>
            supervisor.RunAsync(
                fixture.Configuration,
                fixture.Plan,
                Destination,
                null,
                null,
                CancellationToken.None));

        Assert.Contains("different playlist queue", exception.Message, StringComparison.Ordinal);
        Assert.False(runner.Called);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task RunAsync_LivePersistedStationPreventsSecondSupervisor()
    {
        using var fixture = new SupervisorFixture();
        StationRuntimeState persisted = fixture.PersistedState(StationState.Broadcasting);
        var runner = new ScriptedBroadcastRunner((observer, cancellationToken) => Task.FromResult(
            new BroadcastRecoveryResult(0, 0, null, string.Empty)));
        var store = new RecordingStateStore(persisted);
        var supervisor = new StationSupervisor(
            runner,
            store,
            processId: () => 77,
            resumePlanner: new StationResumePlanner(new FixedProcessExistence(persisted.StationPid)));

        await Assert.ThrowsAsync<StationStartupException>(() => supervisor.RunAsync(
            fixture.Configuration,
            fixture.Plan,
            Destination,
            null,
            null,
            CancellationToken.None));

        Assert.False(runner.Called);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task RunAsync_ExclusiveStateLockPreventsSimultaneousSupervisors()
    {
        using var fixture = new SupervisorFixture();
        var firstRunner = new CancellableBroadcastRunner(fixture.Plan.Items[0]);
        var firstSupervisor = new StationSupervisor(
            firstRunner,
            new RecordingStateStore(),
            processId: () => 77);
        using var cancellation = new CancellationTokenSource();
        Task<StationRunResult> firstRun = firstSupervisor.RunAsync(
            fixture.Configuration,
            fixture.Plan,
            Destination,
            null,
            null,
            cancellation.Token);
        await firstRunner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondRunner = new ScriptedBroadcastRunner((observer, cancellationToken) => Task.FromResult(
            new BroadcastRecoveryResult(0, 0, null, string.Empty)));
        var secondSupervisor = new StationSupervisor(
            secondRunner,
            new RecordingStateStore(),
            processId: () => 88);

        try
        {
            StationStartupException exception = await Assert.ThrowsAsync<StationStartupException>(() =>
                secondSupervisor.RunAsync(
                    fixture.Configuration,
                    fixture.Plan,
                    Destination,
                    null,
                    null,
                    CancellationToken.None));

            Assert.Contains("second station supervisor", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(secondRunner.Called);
        }
        finally
        {
            cancellation.Cancel();
            await firstRun.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RunAsync_ExistingUnlockedCompanionFileDoesNotBlockRestart()
    {
        using var fixture = new SupervisorFixture();
        File.WriteAllText(fixture.Configuration.StatePath + ".lock", string.Empty);
        var runner = new ScriptedBroadcastRunner((observer, cancellationToken) => Task.FromResult(
            new BroadcastRecoveryResult(1, 0, fixture.Plan.Items[0], "runtime failure")));

        StationRunResult result = await new StationSupervisor(
            runner,
            new RecordingStateStore(),
            processId: () => 77).RunAsync(
                fixture.Configuration,
                fixture.Plan,
                Destination,
                null,
                null,
                CancellationToken.None);

        Assert.True(runner.Called);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_DoesNotMutateSchedulerHistory()
    {
        using var fixture = new SupervisorFixture();
        string historyPath = Path.Combine(fixture.Root, "history.json");
        const string originalHistory = "{\"schemaVersion\":1,\"plays\":[]}";
        File.WriteAllText(historyPath, originalHistory);
        var runner = new ScriptedBroadcastRunner((observer, cancellationToken) => Task.FromResult(
            new BroadcastRecoveryResult(1, 0, fixture.Plan.Items[0], "runtime failure")));

        await new StationSupervisor(runner, new RecordingStateStore(), processId: () => 77).RunAsync(
            fixture.Configuration,
            fixture.Plan,
            Destination,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(originalHistory, File.ReadAllText(historyPath));
    }

    private sealed class ScriptedBroadcastRunner(
        Func<IBroadcastRuntimeObserver, CancellationToken, Task<BroadcastRecoveryResult>> run) :
        IStationBroadcastRunner
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
            StartItemIndex = startItemIndex;
            return run(observer, cancellationToken);
        }

        public int? StartItemIndex { get; private set; }
    }

    private sealed class CancellableBroadcastRunner(BroadcastPlanItem item) : IStationBroadcastRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public int RecoveryAttempts { get; private set; }

        public async Task<BroadcastRecoveryResult> RunAsync(
            BroadcastPlan plan,
            string destination,
            int startItemIndex,
            Action<string>? onFfmpegOutput,
            Action<BroadcastRecoveryUpdate>? onUpdate,
            IBroadcastRuntimeObserver observer,
            CancellationToken cancellationToken)
        {
            observer.OnEvent(new BroadcastRuntimeEvent(
                BroadcastRuntimeEventKind.BroadcastStarted,
                item));
            observer.OnEvent(new BroadcastRuntimeEvent(
                BroadcastRuntimeEventKind.FfmpegProcessStarted,
                FfmpegPid: 801));
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                observer.OnEvent(new BroadcastRuntimeEvent(
                    BroadcastRuntimeEventKind.FfmpegProcessStopped,
                    FfmpegPid: 801));
                throw;
            }

            return new BroadcastRecoveryResult(0, RecoveryAttempts, item, string.Empty);
        }
    }

    private sealed class ProgressingCancellableBroadcastRunner(BroadcastPlan plan) : IStationBroadcastRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BroadcastRecoveryResult> RunAsync(
            BroadcastPlan broadcastPlan,
            string destination,
            int startItemIndex,
            Action<string>? onFfmpegOutput,
            Action<BroadcastRecoveryUpdate>? onUpdate,
            IBroadcastRuntimeObserver observer,
            CancellationToken cancellationToken)
        {
            observer.OnEvent(new BroadcastRuntimeEvent(
                BroadcastRuntimeEventKind.BroadcastStarted,
                plan.Items[0],
                GlobalItemIndex: 0));
            observer.OnEvent(new BroadcastRuntimeEvent(
                BroadcastRuntimeEventKind.FfmpegProcessStarted,
                FfmpegPid: 802));
            observer.OnEvent(new BroadcastRuntimeEvent(
                BroadcastRuntimeEventKind.ItemCompleted,
                plan.Items[0],
                GlobalItemIndex: 0));
            observer.OnEvent(new BroadcastRuntimeEvent(
                BroadcastRuntimeEventKind.ItemChanged,
                plan.Items[1],
                GlobalItemIndex: 1));
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                observer.OnEvent(new BroadcastRuntimeEvent(
                    BroadcastRuntimeEventKind.FfmpegProcessStopped,
                    FfmpegPid: 802));
                throw;
            }

            return new BroadcastRecoveryResult(0, 0, plan.Items[1], string.Empty);
        }
    }

    private sealed class RecordingStateStore(StationRuntimeState? initialState = null) : IStationStateStore
    {
        private readonly object _gate = new();

        public List<StationRuntimeState> Writes { get; } = [];

        public Task WriteAsync(
            string path,
            StationRuntimeState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                Writes.Add(state);
            }

            return Task.CompletedTask;
        }

        public StationRuntimeState Read(string path)
        {
            lock (_gate)
            {
                return Writes.Count == 0
                    ? initialState ?? throw new FileNotFoundException()
                    : Writes[^1];
            }
        }

        public StationRuntimeState? ReadIfExists(string path)
        {
            lock (_gate)
            {
                return Writes.Count == 0 ? initialState : Writes[^1];
            }
        }
    }

    private sealed class FixedProcessExistence(params int[] livePids) : IProcessExistence
    {
        private readonly HashSet<int> _livePids = [.. livePids];

        public bool Exists(int processId) => _livePids.Contains(processId);
    }

    private sealed class SupervisorFixture : IDisposable
    {
        public SupervisorFixture()
        {
            Root = Directory.CreateTempSubdirectory("nzytetv-station-supervisor-").FullName;
            string mediaRoot = Directory.CreateDirectory(Path.Combine(Root, "media")).FullName;
            string libraryRoot = Directory.CreateDirectory(Path.Combine(mediaRoot, "library")).FullName;
            string firstPlaylist = Path.Combine(mediaRoot, "first.json");
            string secondPlaylist = Path.Combine(mediaRoot, "second.json");
            Configuration = new StationConfiguration
            {
                MediaRoot = mediaRoot,
                LibraryRoot = libraryRoot,
                StatePath = Path.Combine(Root, "state.json"),
                Playlists = [firstPlaylist, secondPlaylist],
            };
            BroadcastPlanItem[] items =
            [
                new(firstPlaylist, 1, "asset-1", "first.mp4", Path.Combine(libraryRoot, "first.mp4"), 30, "First title", "music-video"),
                new(secondPlaylist, 1, "asset-2", "second.mp4", Path.Combine(libraryRoot, "second.mp4"), 30, "Second title", "animated-visual"),
            ];
            Plan = new BroadcastPlan(
                libraryRoot,
                [firstPlaylist, secondPlaylist],
                items,
                [],
                2,
                60);
        }

        public string Root { get; }

        public StationConfiguration Configuration { get; }

        public BroadcastPlan Plan { get; }

        public StationRuntimeState PersistedState(StationState stationState) => new()
        {
            StationState = stationState,
            BroadcastState = stationState switch
            {
                StationState.Stopped => StationBroadcastState.Stopped,
                StationState.Completed => StationBroadcastState.Completed,
                StationState.Failed => StationBroadcastState.Failed,
                _ => StationBroadcastState.Broadcasting,
            },
            StationPid = 41,
            StartedAtUtc = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero),
            LastHeartbeatUtc = new DateTimeOffset(2026, 9, 29, 12, 1, 0, TimeSpan.Zero),
            MediaRoot = Configuration.MediaRoot,
            LibraryRoot = Configuration.LibraryRoot,
            CurrentPlaylist = Plan.Items[1].PlaylistPath,
            CurrentPlaylistIndex = 2,
            CurrentSequence = 1,
            CurrentPlaylistItemCount = 1,
            AssetId = Plan.Items[1].AssetId,
            Title = Plan.Items[1].Title,
            Type = Plan.Items[1].Type,
            TotalPlaylistCount = 2,
            QueuedPlaylistCount = 0,
            QueueId = BroadcastQueueIdentity.Create(Plan),
            QueueItemCount = 2,
            CurrentGlobalIndex = 1,
            LastCompletedGlobalIndex = 0,
            ResumeGlobalIndex = 1,
            LastStartMode = StationStartMode.Fresh,
            ResumeCount = 2,
        };

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
