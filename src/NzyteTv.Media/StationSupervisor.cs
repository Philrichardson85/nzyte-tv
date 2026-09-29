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
        : this(
            configuration,
            plan,
            stationPid,
            BroadcastQueueIdentity.Create(plan),
            new StationStartDecision(StationStartMode.Fresh, 0, null, 0),
            timeProvider)
    {
    }

    public StationRuntimeTracker(
        StationConfiguration configuration,
        BroadcastPlan plan,
        int stationPid,
        string queueId,
        StationStartDecision startDecision,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueId);
        ArgumentNullException.ThrowIfNull(startDecision);
        if (stationPid <= 0) throw new ArgumentOutOfRangeException(nameof(stationPid));
        if (startDecision.StartItemIndex < 0 || startDecision.StartItemIndex >= plan.Items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startDecision));
        }

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
            QueueId = queueId,
            QueueItemCount = plan.Items.Count,
            CurrentGlobalIndex = startDecision.StartItemIndex,
            LastCompletedGlobalIndex = startDecision.LastCompletedGlobalIndex,
            ResumeGlobalIndex = startDecision.StartItemIndex,
            LastStartMode = startDecision.StartMode,
            ResumeCount = startDecision.ResumeCount,
            LastResumeAtUtc = startDecision.StartMode == StationStartMode.Resume ? now : null,
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
            BroadcastRuntimeEventKind.BroadcastStarted => ApplyItem(
                state,
                runtimeEvent.Item,
                runtimeEvent.GlobalItemIndex),
            BroadcastRuntimeEventKind.ItemChanged => ApplyItem(
                state,
                runtimeEvent.Item,
                runtimeEvent.GlobalItemIndex),
            BroadcastRuntimeEventKind.ItemCompleted => ApplyCompletion(
                state,
                runtimeEvent.GlobalItemIndex),
            BroadcastRuntimeEventKind.FfmpegProcessStarted => state with
            {
                StationState = StationState.Broadcasting,
                BroadcastState = StationBroadcastState.Broadcasting,
                FfmpegPid = runtimeEvent.FfmpegPid,
            },
            BroadcastRuntimeEventKind.FfmpegProcessStopped => state.FfmpegPid == runtimeEvent.FfmpegPid
                ? state with { FfmpegPid = null }
                : state,
            BroadcastRuntimeEventKind.RecoveryStarted => ApplyItem(
                state,
                runtimeEvent.Item,
                runtimeEvent.GlobalItemIndex) with
            {
                StationState = StationState.Broadcasting,
                BroadcastState = StationBroadcastState.Recovering,
                RecoveryAttempts = runtimeEvent.RecoveryAttempts,
            },
            BroadcastRuntimeEventKind.RecoveryBudgetReset => state with { RecoveryAttempts = 0 },
            BroadcastRuntimeEventKind.BroadcastCompleted => ApplyItem(
                state,
                runtimeEvent.Item,
                runtimeEvent.GlobalItemIndex) with
            {
                FfmpegPid = null,
                RecoveryAttempts = runtimeEvent.RecoveryAttempts,
            },
            BroadcastRuntimeEventKind.BroadcastFailed => ApplyItem(
                state,
                runtimeEvent.Item,
                runtimeEvent.GlobalItemIndex) with
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
        CurrentGlobalIndex = _plan.Items.Count - 1,
        LastCompletedGlobalIndex = _plan.Items.Count - 1,
        ResumeGlobalIndex = null,
    });

    public void SetFailed(string error) => Update(state => state with
    {
        StationState = StationState.Failed,
        BroadcastState = StationBroadcastState.Failed,
        FfmpegPid = null,
        LastHeartbeatUtc = _timeProvider.GetUtcNow(),
        LastError = error,
    });

    private StationRuntimeState ApplyItem(
        StationRuntimeState state,
        BroadcastPlanItem? item,
        int? globalItemIndex)
    {
        if (item is null)
        {
            return state;
        }

        int playlistIndex = FindPlaylistIndex(item.PlaylistPath);
        int resolvedGlobalIndex = ResolveGlobalIndex(item, globalItemIndex);
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
            CurrentGlobalIndex = resolvedGlobalIndex,
            QueuedPlaylistCount = Math.Max(0, state.TotalPlaylistCount - playlistIndex - 1),
        };
    }

    private StationRuntimeState ApplyCompletion(StationRuntimeState state, int? globalItemIndex)
    {
        if (globalItemIndex is not int completedIndex
            || completedIndex < 0
            || completedIndex >= _plan.Items.Count
            || state.ResumeGlobalIndex != completedIndex)
        {
            return state;
        }

        return state with
        {
            LastCompletedGlobalIndex = completedIndex,
            ResumeGlobalIndex = completedIndex + 1 < _plan.Items.Count
                ? completedIndex + 1
                : null,
        };
    }

    private int ResolveGlobalIndex(BroadcastPlanItem item, int? suppliedIndex)
    {
        if (suppliedIndex is int supplied
            && supplied >= 0
            && supplied < _plan.Items.Count
            && _plan.Items[supplied] == item)
        {
            return supplied;
        }

        for (int index = 0; index < _plan.Items.Count; index++)
        {
            BroadcastPlanItem candidate = _plan.Items[index];
            if (candidate.Sequence == item.Sequence
                && PathsEqual(candidate.PlaylistPath, item.PlaylistPath))
            {
                return index;
            }
        }

        throw new InvalidOperationException("Broadcast runtime event item is not in the station queue.");
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
        int startItemIndex,
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
        int startItemIndex,
        Action<string>? onFfmpegOutput,
        Action<BroadcastRecoveryUpdate>? onUpdate,
        IBroadcastRuntimeObserver observer,
        CancellationToken cancellationToken) => _recoveryRunner.RunAsync(
            plan,
            destination,
            startItemIndex,
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
    private readonly StationResumePlanner _resumePlanner;

    public StationSupervisor(
        IStationBroadcastRunner broadcastRunner,
        IStationStateStore stateStore,
        TimeProvider? timeProvider = null,
        TimeSpan? heartbeatInterval = null,
        Func<int>? processId = null,
        StationResumePlanner? resumePlanner = null)
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
        _resumePlanner = resumePlanner ?? new StationResumePlanner(new ProcessExistence());
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

        using FileStream stationLock = AcquireStationLock(configuration.StatePath);
        string queueId = BroadcastQueueIdentity.Create(plan);
        StationRuntimeState? persistedState;
        try
        {
            persistedState = _stateStore.ReadIfExists(configuration.StatePath);
        }
        catch (InvalidDataException exception)
        {
            throw new StationStartupException(
                $"Persisted station state is invalid and cannot be resumed safely: " +
                $"{StationSecretRedactor.RedactRtmpUrls(exception.Message)}",
                exception);
        }

        int currentProcessId = _processId();
        StationStartDecision startDecision = _resumePlanner.Decide(
            persistedState,
            queueId,
            plan.Items.Count,
            currentProcessId);
        var tracker = new StationRuntimeTracker(
            configuration,
            plan,
            currentProcessId,
            queueId,
            startDecision,
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
            startDecision.StartItemIndex,
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

    private static FileStream AcquireStationLock(string statePath)
    {
        string fullStatePath = Path.GetFullPath(statePath);
        string directory = Path.GetDirectoryName(fullStatePath)
            ?? throw new StationStartupException("The station state path has no parent directory.");
        string lockPath = fullStatePath + ".lock";
        try
        {
            Directory.CreateDirectory(directory);
            return new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException exception)
        {
            throw new StationStartupException(
                "The station state lock is unavailable; another station supervisor may already be using this state path. " +
                "A second station supervisor was not started.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new StationStartupException(
                "The station state lock could not be created with the current permissions.",
                exception);
        }
    }
}
