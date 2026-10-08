namespace NzyteTv.Dashboard.Status;

public enum DashboardSnapshotQuality
{
    Healthy,
    Degraded,
    Unavailable,
}

public enum DashboardAvailability
{
    Available,
    Unavailable,
}

public enum DashboardStationStatus
{
    Starting,
    Running,
    Stopping,
    Stopped,
    Completed,
    Failed,
    Stale,
    Unknown,
}

public enum DashboardProcessEvidence
{
    Running,
    NotRunning,
    Unknown,
}

public enum DashboardBroadcastState
{
    Starting,
    Broadcasting,
    Recovering,
    Stopping,
    Stopped,
    Completed,
    Failed,
    Unknown,
}

public enum DashboardFfmpegState
{
    Running,
    NotRunning,
    Unknown,
}

public enum DashboardRollingPhase
{
    Claimed,
    Executing,
    Stopped,
    Advancing,
    WaitingForBlock,
    Failed,
    Unknown,
}

public enum DashboardBufferHealth
{
    Healthy,
    Low,
    Empty,
    Unknown,
}

public enum DashboardReplenishmentHealth
{
    Healthy,
    Degraded,
    Blocked,
    SafetyFailure,
    Unknown,
}

public enum DashboardPlaybackAvailability
{
    Available,
    NotPlaying,
    Unavailable,
}

public enum DashboardIssueCode
{
    StationStateMissing,
    StationStateInvalid,
    StationStateUnreadable,
    StationHeartbeatStale,
    StationProcessMissing,
    StationClockInvalid,
    RollingStateMissing,
    RollingStateInvalid,
    RollingStateUnreadable,
    ReplenishmentStateMissing,
    ReplenishmentStateInvalid,
    ReplenishmentStateUnreadable,
    ReplenishmentLineageMismatch,
    ReplenishmentStateInconsistent,
    RuntimeStateInconsistent,
    SnapshotRefreshFailed,
}

public sealed record DashboardRuntimeInfo(string Version, DateTimeOffset StartedAtUtc);

public sealed record DashboardInfo(string Version, long UptimeSeconds);

public sealed record DashboardStationInfo(
    DashboardAvailability Availability,
    DashboardStationStatus Status,
    DashboardProcessEvidence ProcessEvidence,
    DateTimeOffset? HeartbeatAtUtc,
    long? HeartbeatAgeSeconds,
    DateTimeOffset? SessionStartedAtUtc,
    long? SessionUptimeSeconds);

public sealed record DashboardBroadcastInfo(
    DashboardBroadcastState State,
    DashboardFfmpegState FfmpegState,
    int? RecoveryAttempts);

public sealed record DashboardRollingInfo(
    DashboardAvailability Availability,
    DashboardRollingPhase Phase,
    long? ActiveBlockSequence,
    long? LastCompletedBlockSequence,
    long? NextRequiredBlockSequence,
    long? CommittedFutureBlockCount,
    int? FutureBlockTarget,
    long? BufferDeficit,
    DashboardBufferHealth BufferHealth,
    DashboardReplenishmentHealth ReplenishmentHealth);

public sealed record DashboardPlaybackInfo(
    DashboardPlaybackAvailability Availability,
    string? Title,
    string? ItemType,
    int? CurrentItemNumber,
    int? TotalItemCount);

public sealed record DashboardStatusSnapshot(
    int SchemaVersion,
    DateTimeOffset ObservedAtUtc,
    DashboardSnapshotQuality Quality,
    DashboardInfo Dashboard,
    DashboardStationInfo Station,
    DashboardBroadcastInfo Broadcast,
    DashboardRollingInfo Rolling,
    DashboardPlaybackInfo Playback,
    IReadOnlyList<DashboardIssueCode> Issues)
{
    public const int CurrentSchemaVersion = 1;

    public static DashboardStatusSnapshot CreateUnavailable(
        DateTimeOffset observedAtUtc,
        DashboardRuntimeInfo runtime,
        DashboardIssueCode issue) => new(
            CurrentSchemaVersion,
            observedAtUtc,
            DashboardSnapshotQuality.Unavailable,
            new DashboardInfo(
                runtime.Version,
                ElapsedSeconds(runtime.StartedAtUtc, observedAtUtc)),
            new DashboardStationInfo(
                DashboardAvailability.Unavailable,
                DashboardStationStatus.Unknown,
                DashboardProcessEvidence.Unknown,
                null,
                null,
                null,
                null),
            new DashboardBroadcastInfo(
                DashboardBroadcastState.Unknown,
                DashboardFfmpegState.Unknown,
                null),
            new DashboardRollingInfo(
                DashboardAvailability.Unavailable,
                DashboardRollingPhase.Unknown,
                null,
                null,
                null,
                null,
                null,
                null,
                DashboardBufferHealth.Unknown,
                DashboardReplenishmentHealth.Unknown),
            new DashboardPlaybackInfo(
                DashboardPlaybackAvailability.Unavailable,
                null,
                null,
                null,
                null),
            [issue]);

    internal static long ElapsedSeconds(DateTimeOffset start, DateTimeOffset end) =>
        Math.Max(0, (long)(end - start).TotalSeconds);
}
