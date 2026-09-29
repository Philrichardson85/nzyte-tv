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
    }

    [Fact]
    public void RuntimeTracker_BroadcastItemAndRecoveryEventsUpdateObservableState()
    {
        using var fixture = new SupervisorFixture();
        var tracker = new StationRuntimeTracker(fixture.Configuration, fixture.Plan, 123);

        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.BroadcastStarted,
            fixture.Plan.Items[0]));
        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.FfmpegProcessStarted,
            FfmpegPid: 1001));
        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.ItemChanged,
            fixture.Plan.Items[1]));

        StationRuntimeState playing = tracker.Snapshot();
        Assert.Equal(StationState.Broadcasting, playing.StationState);
        Assert.Equal(1001, playing.FfmpegPid);
        Assert.Equal(fixture.Plan.PlaylistPaths[1], playing.CurrentPlaylist);
        Assert.Equal(2, playing.CurrentPlaylistIndex);
        Assert.Equal(1, playing.CurrentSequence);
        Assert.Equal("Second title", playing.Title);
        Assert.Equal("animated-visual", playing.Type);

        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.FfmpegProcessStopped,
            FfmpegPid: 1001));
        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.RecoveryStarted,
            fixture.Plan.Items[1],
            RecoveryAttempts: 1));
        tracker.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.FfmpegProcessStarted,
            FfmpegPid: 1002));

        StationRuntimeState recovered = tracker.Snapshot();
        Assert.Equal(1002, recovered.FfmpegPid);
        Assert.Equal(1, recovered.RecoveryAttempts);
        Assert.Equal(StationBroadcastState.Broadcasting, recovered.BroadcastState);
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

    private sealed class ScriptedBroadcastRunner(
        Func<IBroadcastRuntimeObserver, CancellationToken, Task<BroadcastRecoveryResult>> run) :
        IStationBroadcastRunner
    {
        public bool Called { get; private set; }

        public Task<BroadcastRecoveryResult> RunAsync(
            BroadcastPlan plan,
            string destination,
            Action<string>? onFfmpegOutput,
            Action<BroadcastRecoveryUpdate>? onUpdate,
            IBroadcastRuntimeObserver observer,
            CancellationToken cancellationToken)
        {
            Called = true;
            return run(observer, cancellationToken);
        }
    }

    private sealed class CancellableBroadcastRunner(BroadcastPlanItem item) : IStationBroadcastRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public int RecoveryAttempts { get; private set; }

        public async Task<BroadcastRecoveryResult> RunAsync(
            BroadcastPlan plan,
            string destination,
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

    private sealed class RecordingStateStore : IStationStateStore
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
                return Writes[^1];
            }
        }
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

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
