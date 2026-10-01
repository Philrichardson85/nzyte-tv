using NzyteTv.Core;

namespace NzyteTv.Media;

public interface IStationStateLock : IDisposable;

public interface IStationStateLockProvider
{
    IStationStateLock Acquire(string statePath);
}

public sealed class StationStateLockProvider : IStationStateLockProvider
{
    public IStationStateLock Acquire(string statePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        string fullStatePath = Path.GetFullPath(statePath);
        string directory = Path.GetDirectoryName(fullStatePath)
            ?? throw new RollingStationSafetyException(
                "The station state path has no parent directory.");
        string lockPath = fullStatePath + ".lock";
        try
        {
            Directory.CreateDirectory(directory);
            return new HeldStationStateLock(new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StationStateLockUnavailableException(
                "The station state lock is unavailable; another station supervisor may be running.",
                exception);
        }
    }

    private sealed class HeldStationStateLock(FileStream stream) : IStationStateLock
    {
        public void Dispose() => stream.Dispose();
    }
}

public sealed class StationStateLockUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public interface IStationCompletionEvidenceService
{
    bool IsPositiveCompletion(
        StationRuntimeState state,
        string expectedQueueId,
        int expectedItemCount);

    Task<StationRuntimeState> ConfirmAndSealAsync(
        string statePath,
        string expectedQueueId,
        int expectedItemCount,
        int currentProcessId,
        bool controlledExecutionFinished,
        CancellationToken cancellationToken);
}

public sealed class StationCompletionEvidenceService : IStationCompletionEvidenceService
{
    private readonly IStationStateStore _stateStore;
    private readonly IStationStateLockProvider _lockProvider;
    private readonly IProcessExistence _processExistence;
    private readonly TimeProvider _timeProvider;

    public StationCompletionEvidenceService(
        IStationStateStore stateStore,
        IStationStateLockProvider? lockProvider = null,
        IProcessExistence? processExistence = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        _stateStore = stateStore;
        _lockProvider = lockProvider ?? new StationStateLockProvider();
        _processExistence = processExistence ?? new ProcessExistence();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsPositiveCompletion(
        StationRuntimeState state,
        string expectedQueueId,
        int expectedItemCount)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedQueueId);
        return expectedItemCount > 0
            && state.SchemaVersion == StationRuntimeState.CurrentSchemaVersion
            && string.Equals(state.QueueId, expectedQueueId, StringComparison.Ordinal)
            && state.QueueItemCount == expectedItemCount
            && state.LastCompletedGlobalIndex == expectedItemCount - 1
            && state.ResumeGlobalIndex is null;
    }

    public async Task<StationRuntimeState> ConfirmAndSealAsync(
        string statePath,
        string expectedQueueId,
        int expectedItemCount,
        int currentProcessId,
        bool controlledExecutionFinished,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        StationRuntimeState initial = _stateStore.Read(statePath);
        EnsurePositive(initial, expectedQueueId, expectedItemCount);
        if (initial.StationState == StationState.Completed)
        {
            return initial;
        }

        EnsureSupervisorNotRunning(initial, currentProcessId, controlledExecutionFinished);
        using IStationStateLock stationLock = _lockProvider.Acquire(statePath);
        StationRuntimeState latest = _stateStore.Read(statePath);
        EnsurePositive(latest, expectedQueueId, expectedItemCount);
        if (latest.StationState == StationState.Completed)
        {
            return latest;
        }

        EnsureSupervisorNotRunning(latest, currentProcessId, controlledExecutionFinished);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        StationRuntimeState completed = latest with
        {
            StationState = StationState.Completed,
            BroadcastState = StationBroadcastState.Completed,
            FfmpegPid = null,
            QueuedPlaylistCount = 0,
            LastHeartbeatUtc = now,
            CompletedAtUtc = now,
            CurrentGlobalIndex = expectedItemCount - 1,
            LastCompletedGlobalIndex = expectedItemCount - 1,
            ResumeGlobalIndex = null,
        };
        await _stateStore.WriteAsync(
            statePath,
            completed,
            cancellationToken).ConfigureAwait(false);
        return completed;
    }

    private void EnsurePositive(
        StationRuntimeState state,
        string expectedQueueId,
        int expectedItemCount)
    {
        if (!IsPositiveCompletion(state, expectedQueueId, expectedItemCount))
        {
            throw new RollingStationSafetyException(
                "Durable station state does not contain positive completion evidence for the claimed rolling block.");
        }
    }

    private void EnsureSupervisorNotRunning(
        StationRuntimeState state,
        int currentProcessId,
        bool controlledExecutionFinished)
    {
        bool controlledCurrentProcess = controlledExecutionFinished
            && state.StationPid == currentProcessId;
        if (!controlledCurrentProcess && _processExistence.Exists(state.StationPid))
        {
            throw new RollingStationSafetyException(
                "A live station supervisor owns the positively completed queue; external completion sealing was refused.");
        }
    }
}
