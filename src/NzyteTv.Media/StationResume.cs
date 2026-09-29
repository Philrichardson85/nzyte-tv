using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record StationStartDecision(
    StationStartMode StartMode,
    int StartItemIndex,
    int? LastCompletedGlobalIndex,
    int ResumeCount);

public sealed class StationStartupException : Exception
{
    public StationStartupException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class StationResumePlanner
{
    private readonly IProcessExistence _processExistence;

    public StationResumePlanner(IProcessExistence processExistence)
    {
        ArgumentNullException.ThrowIfNull(processExistence);
        _processExistence = processExistence;
    }

    public StationStartDecision Decide(
        StationRuntimeState? persistedState,
        string queueId,
        int queueItemCount,
        int currentProcessId = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueId);
        if (queueItemCount <= 0) throw new ArgumentOutOfRangeException(nameof(queueItemCount));

        if (persistedState is null)
        {
            return Fresh();
        }

        bool interrupted = IsInterrupted(persistedState.StationState);
        if (persistedState.StationPid != currentProcessId
            && _processExistence.Exists(persistedState.StationPid))
        {
            throw new StationStartupException(
                $"Station supervisor PID {persistedState.StationPid} is still running. " +
                "A second station supervisor was not started.");
        }

        if (persistedState.SchemaVersion == StationRuntimeState.LegacySchemaVersion)
        {
            return Fresh();
        }

        if (persistedState.SchemaVersion != StationRuntimeState.CurrentSchemaVersion)
        {
            throw new StationStartupException(
                $"Persisted station state schemaVersion {persistedState.SchemaVersion} is not supported for startup.");
        }

        if (persistedState.StationState == StationState.Completed)
        {
            return Fresh();
        }

        bool sameQueue = string.Equals(persistedState.QueueId, queueId, StringComparison.Ordinal);
        if (persistedState.StationState == StationState.Stopped && !sameQueue)
        {
            return Fresh();
        }

        if (interrupted && !sameQueue)
        {
            throw new StationStartupException(
                "Persisted interrupted station state belongs to a different playlist queue. " +
                "Automatic resume was not attempted.");
        }

        if (!sameQueue)
        {
            throw new StationStartupException(
                "Persisted station state cannot be matched safely to the configured playlist queue.");
        }

        ValidateResumeMetadata(persistedState, queueItemCount);
        return new StationStartDecision(
            StationStartMode.Resume,
            persistedState.ResumeGlobalIndex!.Value,
            persistedState.LastCompletedGlobalIndex,
            checked(persistedState.ResumeCount + 1));
    }

    private static StationStartDecision Fresh() => new(
        StationStartMode.Fresh,
        StartItemIndex: 0,
        LastCompletedGlobalIndex: null,
        ResumeCount: 0);

    private static bool IsInterrupted(StationState state) => state is
        StationState.Starting or
        StationState.Broadcasting or
        StationState.Stopping or
        StationState.Failed;

    private static void ValidateResumeMetadata(StationRuntimeState state, int currentQueueItemCount)
    {
        if (state.LastStartMode is not StationStartMode startMode
            || !Enum.IsDefined(startMode)
            || (startMode == StationStartMode.Resume && state.LastResumeAtUtc is null))
        {
            throw UnsafeResume("Persisted start-mode telemetry is invalid.");
        }

        if (state.QueueItemCount != currentQueueItemCount)
        {
            throw UnsafeResume("Persisted queue length does not match the configured playlist queue.");
        }

        if (state.ResumeGlobalIndex is not int resumeIndex
            || resumeIndex < 0
            || resumeIndex >= currentQueueItemCount)
        {
            throw UnsafeResume("Persisted resume position is outside the playlist queue.");
        }

        if (state.CurrentGlobalIndex is int currentIndex
            && (currentIndex < 0 || currentIndex >= currentQueueItemCount))
        {
            throw UnsafeResume("Persisted current-item position is outside the playlist queue.");
        }

        int expectedResume = state.LastCompletedGlobalIndex is int lastCompleted
            ? lastCompleted + 1
            : 0;
        if (state.LastCompletedGlobalIndex is < 0
            || state.LastCompletedGlobalIndex >= currentQueueItemCount
            || resumeIndex != expectedResume)
        {
            throw UnsafeResume("Persisted completion and resume positions are inconsistent.");
        }

        if (state.ResumeCount < 0 || state.ResumeCount == int.MaxValue)
        {
            throw UnsafeResume("Persisted resume count is invalid.");
        }
    }

    private static StationStartupException UnsafeResume(string detail) => new(
        $"Persisted station resume state is unsafe: {detail} Automatic resume was not attempted.");
}
