using System.Threading.Channels;
using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed class StationRuntimeTracker : IBroadcastRuntimeObserver
{
    private readonly object _gate = new();
    private readonly BroadcastPlan _plan;
    private readonly TimeProvider _timeProvider;
    private StationRuntimeState _state;

    public StationRuntimeTracker(
        StationConfiguration configuration,
        BroadcastPlan plan,
        int stationPid,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(plan);
        if (stationPid <= 0) throw new ArgumentOutOfRangeException(nameof(stationPid));

        _plan = plan;
        _timeProvider = timeProvider ?? TimeProvider.System;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        _state = new StationRuntimeState
        {
            StationState = StationState.Starting,
            BroadcastState = StationBroadcastState.Starting,
            StationPid = stationPid,
            StartedAtUtc = now,
            LastHeartbeatUtc = now,
            MediaRoot = configuration.MediaRoot,
            LibraryRoot = configuration.LibraryRoot,
            QueuedPlaylistCount = configuration.Playlists.Count,
            TotalPlaylistCount = configuration.Playlists.Count,
        };
    }

    public event Action? Changed;

    public StationRuntimeState Snapshot()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    public void OnEvent(BroadcastRuntimeEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        Update(state => runtimeEvent.Kind switch
        {
            BroadcastRuntimeEventKind.BroadcastStarted => ApplyItem(state, runtimeEvent.Item),
            BroadcastRuntimeEventKind.ItemChanged => ApplyItem(state, runtimeEvent.Item),
            BroadcastRuntimeEventKind.FfmpegProcessStarted => state with
            {
                StationState = StationState.Broadcasting,
                BroadcastState = StationBroadcastState.Broadcasting,
                FfmpegPid = runtimeEvent.FfmpegPid,
            },
            BroadcastRuntimeEventKind.FfmpegProcessStopped => state.FfmpegPid == runtimeEvent.FfmpegPid
                ? state with { FfmpegPid = null }
                : state,
            BroadcastRuntimeEventKind.RecoveryStarted => ApplyItem(state, runtimeEvent.Item) with
            {
                StationState = StationState.Broadcasting,
                BroadcastState = StationBroadcastState.Recovering,
                RecoveryAttempts = runtimeEvent.RecoveryAttempts,
            },
            BroadcastRuntimeEventKind.RecoveryBudgetReset => state with { RecoveryAttempts = 0 },
            BroadcastRuntimeEventKind.BroadcastCompleted => ApplyItem(state, runtimeEvent.Item) with
            {
                FfmpegPid = null,
                RecoveryAttempts = runtimeEvent.RecoveryAttempts,
            },
            BroadcastRuntimeEventKind.BroadcastFailed => ApplyItem(state, runtimeEvent.Item) with
            {
                FfmpegPid = null,
                RecoveryAttempts = runtimeEvent.RecoveryAttempts,
            },
            _ => state,
        });
    }

    public void UpdateHeartbeat() => Update(state => state with
    {
        LastHeartbeatUtc = _timeProvider.GetUtcNow(),
    });

    public void SetStopping() => Update(state => state with
    {
        StationState = StationState.Stopping,
        BroadcastState = StationBroadcastState.Stopping,
    });

    public void SetStopped() => Update(state => state with
    {
        StationState = StationState.Stopped,
        BroadcastState = StationBroadcastState.Stopped,
        FfmpegPid = null,
        LastHeartbeatUtc = _timeProvider.GetUtcNow(),
        StoppedAtUtc = _timeProvider.GetUtcNow(),
    });

    public void SetCompleted() => Update(state => state with
    {
        StationState = StationState.Completed,
        BroadcastState = StationBroadcastState.Completed,
        FfmpegPid = null,
        QueuedPlaylistCount = 0,
        LastHeartbeatUtc = _timeProvider.GetUtcNow(),
        CompletedAtUtc = _timeProvider.GetUtcNow(),
    });

    public void SetFailed(string error) => Update(state => state with
    {
        StationState = StationState.Failed,
        BroadcastState = StationBroadcastState.Failed,
        FfmpegPid = null,
        LastHeartbeatUtc = _timeProvider.GetUtcNow(),
        LastError = error,
    });

    private StationRuntimeState ApplyItem(StationRuntimeState state, BroadcastPlanItem? item)
    {
        if (item is null)
        {
            return state;
        }

        int playlistIndex = FindPlaylistIndex(item.PlaylistPath);
        int itemCount = _plan.Items.Count(candidate => PathsEqual(
            candidate.PlaylistPath,
            item.PlaylistPath));
        return state with
        {
            CurrentPlaylist = item.PlaylistPath,
            CurrentPlaylistIndex = playlistIndex + 1,
            CurrentSequence = item.Sequence,
            CurrentPlaylistItemCount = itemCount,
            AssetId = string.IsNullOrWhiteSpace(item.AssetId) ? null : item.AssetId,
            Title = string.IsNullOrWhiteSpace(item.Title) ? null : item.Title,
            Type = string.IsNullOrWhiteSpace(item.Type) ? null : item.Type,
            QueuedPlaylistCount = Math.Max(0, state.TotalPlaylistCount - playlistIndex - 1),
        };
    }

    private int FindPlaylistIndex(string path)
    {
        for (int index = 0; index < _plan.PlaylistPaths.Count; index++)
        {
            if (PathsEqual(_plan.PlaylistPaths[index], path))
            {
                return index;
            }
        }

        return 0;
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void Update(Func<StationRuntimeState, StationRuntimeState> update)
    {
        Action? changed;
        lock (_gate)
        {
            StationRuntimeState next = update(_state);
            if (ReferenceEquals(next, _state) || next == _state)
            {
                return;
            }

            _state = next;
            changed = Changed;
        }

        changed?.Invoke();
    }
}

public sealed class StationStatePublisher : IDisposable
{
    private readonly StationRuntimeTracker _tracker;
    private readonly IStationStateStore _stateStore;
    private readonly string _statePath;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _heartbeatInterval;
    private readonly Channel<byte> _writeRequests = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });

    public StationStatePublisher(
        StationRuntimeTracker tracker,
        IStationStateStore stateStore,
        string statePath,
        TimeProvider? timeProvider = null,
        TimeSpan? heartbeatInterval = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        _tracker = tracker;
        _stateStore = stateStore;
        _statePath = statePath;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _heartbeatInterval = heartbeatInterval ?? StationRuntimePolicy.HeartbeatInterval;
        if (_heartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        }

        _tracker.Changed += RequestWrite;
    }

    public void RequestWrite() => _writeRequests.Writer.TryWrite(0);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_heartbeatInterval, _timeProvider);
        Task<bool> heartbeatTask = timer.WaitForNextTickAsync(cancellationToken).AsTask();
        Task<bool> changedTask = _writeRequests.Reader.WaitToReadAsync(cancellationToken).AsTask();

        while (true)
        {
            Task<bool> completed = await Task.WhenAny(heartbeatTask, changedTask).ConfigureAwait(false);
            bool shouldWrite = false;
            if (completed == heartbeatTask)
            {
                if (!await heartbeatTask.ConfigureAwait(false))
                {
                    return;
                }

                _tracker.UpdateHeartbeat();
                shouldWrite = true;
                heartbeatTask = timer.WaitForNextTickAsync(cancellationToken).AsTask();
            }

            if (completed == changedTask || changedTask.IsCompleted)
            {
                if (!await changedTask.ConfigureAwait(false))
                {
                    return;
                }

                while (_writeRequests.Reader.TryRead(out _))
                {
                }

                shouldWrite = true;
                changedTask = _writeRequests.Reader.WaitToReadAsync(cancellationToken).AsTask();
            }

            if (shouldWrite)
            {
                await _stateStore.WriteAsync(
                    _statePath,
                    _tracker.Snapshot(),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _tracker.Changed -= RequestWrite;
        _writeRequests.Writer.TryComplete();
    }
}

public interface IStationBroadcastRunner
{
    Task<BroadcastRecoveryResult> RunAsync(
        BroadcastPlan plan,
        string destination,
        Action<string>? onFfmpegOutput,
        Action<BroadcastRecoveryUpdate>? onUpdate,
        IBroadcastRuntimeObserver observer,
        CancellationToken cancellationToken);
}

public sealed class ResilientStationBroadcastRunner : IStationBroadcastRunner
{
    private readonly BroadcastRecoveryRunner _recoveryRunner;

    public ResilientStationBroadcastRunner(BroadcastRecoveryRunner recoveryRunner)
    {
        ArgumentNullException.ThrowIfNull(recoveryRunner);
        _recoveryRunner = recoveryRunner;
    }

    public Task<BroadcastRecoveryResult> RunAsync(
        BroadcastPlan plan,
        string destination,
        Action<string>? onFfmpegOutput,
        Action<BroadcastRecoveryUpdate>? onUpdate,
        IBroadcastRuntimeObserver observer,
        CancellationToken cancellationToken) => _recoveryRunner.RunAsync(
            plan,
            destination,
            onFfmpegOutput,
            onUpdate,
            observer,
            cancellationToken);
}

public sealed record StationRunResult(
    int ExitCode,
    StationRuntimeState FinalState,
    string? Error = null);

public sealed class StationSupervisor
{
    private readonly IStationBroadcastRunner _broadcastRunner;
    private readonly IStationStateStore _stateStore;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _heartbeatInterval;
    private readonly Func<int> _processId;

    public StationSupervisor(
        IStationBroadcastRunner broadcastRunner,
        IStationStateStore stateStore,
        TimeProvider? timeProvider = null,
        TimeSpan? heartbeatInterval = null,
        Func<int>? processId = null)
    {
        ArgumentNullException.ThrowIfNull(broadcastRunner);
        ArgumentNullException.ThrowIfNull(stateStore);
        _broadcastRunner = broadcastRunner;
        _stateStore = stateStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _heartbeatInterval = heartbeatInterval ?? StationRuntimePolicy.HeartbeatInterval;
        if (_heartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        }

        _processId = processId ?? (() => Environment.ProcessId);
    }

    public async Task<StationRunResult> RunAsync(
        StationConfiguration configuration,
        BroadcastPlan plan,
        string destination,
        Action<string>? onFfmpegOutput,
        Action<BroadcastRecoveryUpdate>? onUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (!plan.IsReady) throw new InvalidOperationException("Station broadcast plan is not ready.");

        var tracker = new StationRuntimeTracker(
            configuration,
            plan,
            _processId(),
            _timeProvider);
        await _stateStore.WriteAsync(
            configuration.StatePath,
            tracker.Snapshot(),
            CancellationToken.None).ConfigureAwait(false);

        using var publisher = new StationStatePublisher(
            tracker,
            _stateStore,
            configuration.StatePath,
            _timeProvider,
            _heartbeatInterval);
        using var publisherCancellation = new CancellationTokenSource();
        using var broadcastCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenRegistration stopRegistration = cancellationToken.Register(() =>
        {
            tracker.SetStopping();
            publisher.RequestWrite();
        });

        Task publisherTask = publisher.RunAsync(publisherCancellation.Token);
        Task<BroadcastRecoveryResult> broadcastTask = _broadcastRunner.RunAsync(
            plan,
            destination,
            onFfmpegOutput,
            onUpdate,
            tracker,
            broadcastCancellation.Token);

        BroadcastRecoveryResult? broadcastResult = null;
        Exception? failure = null;
        bool cancelled = false;

        Task firstCompleted = await Task.WhenAny(broadcastTask, publisherTask).ConfigureAwait(false);
        if (firstCompleted == publisherTask)
        {
            try
            {
                await publisherTask.ConfigureAwait(false);
                failure = new IOException("Station state publisher stopped unexpectedly.");
            }
            catch (OperationCanceledException) when (publisherCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (failure is not null)
            {
                broadcastCancellation.Cancel();
            }
        }

        try
        {
            broadcastResult = await broadcastTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (OperationCanceledException) when (failure is not null)
        {
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        publisherCancellation.Cancel();
        try
        {
            await publisherTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (publisherCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (cancelled || cancellationToken.IsCancellationRequested)
        {
            tracker.SetStopping();
            await _stateStore.WriteAsync(
                configuration.StatePath,
                tracker.Snapshot(),
                CancellationToken.None).ConfigureAwait(false);
            tracker.SetStopped();
            await _stateStore.WriteAsync(
                configuration.StatePath,
                tracker.Snapshot(),
                CancellationToken.None).ConfigureAwait(false);
            return new StationRunResult(0, tracker.Snapshot());
        }

        if (failure is not null)
        {
            string safeError = Redact(failure.Message, destination);
            tracker.SetFailed(safeError);
            await _stateStore.WriteAsync(
                configuration.StatePath,
                tracker.Snapshot(),
                CancellationToken.None).ConfigureAwait(false);
            return new StationRunResult(1, tracker.Snapshot(), safeError);
        }

        if (broadcastResult?.ExitCode == 0)
        {
            tracker.SetCompleted();
            await _stateStore.WriteAsync(
                configuration.StatePath,
                tracker.Snapshot(),
                CancellationToken.None).ConfigureAwait(false);
            return new StationRunResult(0, tracker.Snapshot());
        }

        string failureDetail = string.IsNullOrWhiteSpace(broadcastResult?.LastDiagnostic)
            ? "Broadcast recovery attempts exhausted."
            : broadcastResult.LastDiagnostic.Split(Environment.NewLine)[0];
        failureDetail = Redact(failureDetail, destination);
        tracker.SetFailed(failureDetail);
        await _stateStore.WriteAsync(
            configuration.StatePath,
            tracker.Snapshot(),
            CancellationToken.None).ConfigureAwait(false);
        return new StationRunResult(1, tracker.Snapshot(), failureDetail);
    }

    private static string Redact(string value, string destination) =>
        StationSecretRedactor.RedactRtmpUrls(
            value.Replace(destination, "[REDACTED]", StringComparison.Ordinal))!;
}
