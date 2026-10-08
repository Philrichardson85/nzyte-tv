using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Dashboard.Status;

public sealed class DashboardStatusProvider : IDashboardStatusSnapshotSource
{
    private readonly IDashboardStateReader _stateReader;
    private readonly IProcessExistence _processExistence;
    private readonly TimeProvider _timeProvider;
    private readonly DashboardRuntimeInfo _runtime;

    public DashboardStatusProvider(
        IDashboardStateReader stateReader,
        IProcessExistence processExistence,
        TimeProvider timeProvider,
        DashboardRuntimeInfo runtime)
    {
        ArgumentNullException.ThrowIfNull(stateReader);
        ArgumentNullException.ThrowIfNull(processExistence);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(runtime);
        _stateReader = stateReader;
        _processExistence = processExistence;
        _timeProvider = timeProvider;
        _runtime = runtime;
    }

    public DashboardStatusSnapshot ReadStatus()
    {
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        var issues = new List<DashboardIssueCode>();

        RollingStationRuntimeState? rollingBefore = ReadState(
            _stateReader.ReadRollingState,
            DashboardIssueCode.RollingStateMissing,
            DashboardIssueCode.RollingStateInvalid,
            DashboardIssueCode.RollingStateUnreadable,
            issues);
        StationRuntimeState? station = ReadState(
            _stateReader.ReadStationState,
            DashboardIssueCode.StationStateMissing,
            DashboardIssueCode.StationStateInvalid,
            DashboardIssueCode.StationStateUnreadable,
            issues);
        RollingReplenishmentState? replenishment = ReadState(
            _stateReader.ReadReplenishmentState,
            DashboardIssueCode.ReplenishmentStateMissing,
            DashboardIssueCode.ReplenishmentStateInvalid,
            DashboardIssueCode.ReplenishmentStateUnreadable,
            issues);
        RollingStationRuntimeState? rollingAfter = ReadState(
            _stateReader.ReadRollingState,
            DashboardIssueCode.RollingStateMissing,
            DashboardIssueCode.RollingStateInvalid,
            DashboardIssueCode.RollingStateUnreadable,
            issues,
            addIssue: false);

        RollingStationRuntimeState? rolling = rollingBefore;
        if (rollingBefore != rollingAfter)
        {
            AddIssue(issues, DashboardIssueCode.RuntimeStateInconsistent);
            rolling = null;
            replenishment = null;
        }

        DashboardStationInfo stationInfo = CreateStationInfo(
            station,
            observedAt,
            issues,
            out bool stationLive);
        DashboardBroadcastInfo broadcastInfo = CreateBroadcastInfo(
            station,
            stationLive,
            issues);
        DashboardRollingInfo rollingInfo = CreateRollingInfo(
            rolling,
            replenishment,
            issues);

        bool runtimeStatesAgree = RuntimeStatesAgree(station, rolling);
        if (!runtimeStatesAgree)
        {
            AddIssue(issues, DashboardIssueCode.RuntimeStateInconsistent);
        }

        DashboardPlaybackInfo playbackInfo = CreatePlaybackInfo(
            station,
            stationLive,
            runtimeStatesAgree);
        DashboardSnapshotQuality quality = station is null && rolling is null
            ? DashboardSnapshotQuality.Unavailable
            : issues.Count == 0
                ? DashboardSnapshotQuality.Healthy
                : DashboardSnapshotQuality.Degraded;

        return new DashboardStatusSnapshot(
            DashboardStatusSnapshot.CurrentSchemaVersion,
            observedAt,
            quality,
            new DashboardInfo(
                DashboardTextSanitizer.Sanitize(_runtime.Version, 80) ?? "unknown",
                DashboardStatusSnapshot.ElapsedSeconds(_runtime.StartedAtUtc, observedAt)),
            stationInfo,
            broadcastInfo,
            rollingInfo,
            playbackInfo,
            issues.ToArray());
    }

    private DashboardStationInfo CreateStationInfo(
        StationRuntimeState? state,
        DateTimeOffset observedAt,
        ICollection<DashboardIssueCode> issues,
        out bool stationLive)
    {
        stationLive = false;
        if (state is null)
        {
            return new DashboardStationInfo(
                DashboardAvailability.Unavailable,
                DashboardStationStatus.Unknown,
                DashboardProcessEvidence.Unknown,
                null,
                null,
                null,
                null);
        }

        bool active = state.StationState is
            StationState.Starting or StationState.Broadcasting or StationState.Stopping;
        TimeSpan heartbeatAge = observedAt - state.LastHeartbeatUtc;
        bool clockInvalid = heartbeatAge < TimeSpan.Zero;
        if (clockInvalid)
        {
            AddIssue(issues, DashboardIssueCode.StationClockInvalid);
        }

        bool heartbeatStale = clockInvalid
            || heartbeatAge > StationRuntimePolicy.StaleHeartbeatThreshold;
        if (active && heartbeatStale)
        {
            AddIssue(issues, DashboardIssueCode.StationHeartbeatStale);
        }

        bool? processExists = TryProcessExists(state.StationPid);
        if (active && processExists == false)
        {
            AddIssue(issues, DashboardIssueCode.StationProcessMissing);
        }

        stationLive = active && !heartbeatStale && processExists == true;
        DashboardStationStatus status = active && !stationLive
            ? DashboardStationStatus.Stale
            : MapStationStatus(state.StationState);
        DashboardProcessEvidence processEvidence = stationLive
            ? DashboardProcessEvidence.Running
            : active
                ? DashboardProcessEvidence.Unknown
                : DashboardProcessEvidence.NotRunning;
        bool sessionTimeReliable = stationLive && state.StartedAtUtc <= observedAt;
        if (stationLive && !sessionTimeReliable)
        {
            AddIssue(issues, DashboardIssueCode.StationClockInvalid);
        }

        return new DashboardStationInfo(
            DashboardAvailability.Available,
            status,
            processEvidence,
            state.LastHeartbeatUtc,
            clockInvalid ? null : Math.Max(0, (long)heartbeatAge.TotalSeconds),
            sessionTimeReliable ? state.StartedAtUtc : null,
            sessionTimeReliable
                ? DashboardStatusSnapshot.ElapsedSeconds(state.StartedAtUtc, observedAt)
                : null);
    }

    private DashboardBroadcastInfo CreateBroadcastInfo(
        StationRuntimeState? state,
        bool stationLive,
        ICollection<DashboardIssueCode> issues)
    {
        if (state is null)
        {
            return new DashboardBroadcastInfo(
                DashboardBroadcastState.Unknown,
                DashboardFfmpegState.Unknown,
                null);
        }

        int? recoveryAttempts = state.RecoveryAttempts >= 0 ? state.RecoveryAttempts : null;
        if (recoveryAttempts is null)
        {
            AddIssue(issues, DashboardIssueCode.RuntimeStateInconsistent);
        }

        DashboardFfmpegState ffmpegState;
        if (state.StationState is StationState.Stopped or StationState.Completed or StationState.Failed)
        {
            ffmpegState = DashboardFfmpegState.NotRunning;
        }
        else if (!stationLive)
        {
            ffmpegState = DashboardFfmpegState.Unknown;
        }
        else if (state.FfmpegPid is not int ffmpegPid)
        {
            ffmpegState = DashboardFfmpegState.NotRunning;
        }
        else
        {
            bool? exists = TryProcessExists(ffmpegPid);
            ffmpegState = exists switch
            {
                true => DashboardFfmpegState.Running,
                false => DashboardFfmpegState.NotRunning,
                null => DashboardFfmpegState.Unknown,
            };
        }

        bool active = state.StationState is
            StationState.Starting or StationState.Broadcasting or StationState.Stopping;
        DashboardBroadcastState broadcastState = active && !stationLive
            ? DashboardBroadcastState.Unknown
            : MapBroadcastState(state.BroadcastState);

        return new DashboardBroadcastInfo(
            broadcastState,
            ffmpegState,
            recoveryAttempts);
    }

    private static DashboardRollingInfo CreateRollingInfo(
        RollingStationRuntimeState? rolling,
        RollingReplenishmentState? replenishment,
        ICollection<DashboardIssueCode> issues)
    {
        if (rolling is null)
        {
            return new DashboardRollingInfo(
                DashboardAvailability.Unavailable,
                DashboardRollingPhase.Unknown,
                null,
                null,
                null,
                null,
                null,
                null,
                DashboardBufferHealth.Unknown,
                DashboardReplenishmentHealth.Unknown);
        }

        long? nextRequired = rolling.LastCompletedBlockSequence switch
        {
            null => 1,
            long.MaxValue => null,
            long value => value + 1,
        };
        if (nextRequired is null)
        {
            AddIssue(issues, DashboardIssueCode.RuntimeStateInconsistent);
        }

        long? futureCount = null;
        int? futureTarget = null;
        long? deficit = null;
        DashboardBufferHealth bufferHealth = DashboardBufferHealth.Unknown;
        DashboardReplenishmentHealth replenishmentHealth = DashboardReplenishmentHealth.Unknown;

        if (replenishment is not null)
        {
            long? expectedAnchor = rolling.ActiveBlockSequence ?? nextRequired;
            if (!string.Equals(
                    replenishment.PlannerId,
                    rolling.PlannerId,
                    StringComparison.Ordinal))
            {
                AddIssue(issues, DashboardIssueCode.ReplenishmentLineageMismatch);
            }
            else if (expectedAnchor is null || replenishment.AnchorSequence != expectedAnchor)
            {
                AddIssue(issues, DashboardIssueCode.ReplenishmentStateInconsistent);
            }
            else
            {
                futureCount = Math.Max(
                    0,
                    replenishment.HighestCommittedSequence - replenishment.AnchorSequence);
                futureTarget = replenishment.FutureBlockTarget;
                deficit = replenishment.BufferDeficit;
                bufferHealth = replenishment.BufferDeficit == 0
                    ? DashboardBufferHealth.Healthy
                    : replenishment.HighestCommittedSequence <= replenishment.AnchorSequence
                        ? DashboardBufferHealth.Empty
                        : DashboardBufferHealth.Low;
                replenishmentHealth = MapReplenishmentHealth(replenishment.Health);
            }
        }

        return new DashboardRollingInfo(
            DashboardAvailability.Available,
            MapRollingPhase(rolling.Phase),
            rolling.ActiveBlockSequence,
            rolling.LastCompletedBlockSequence,
            nextRequired,
            futureCount,
            futureTarget,
            deficit,
            bufferHealth,
            replenishmentHealth);
    }

    private static DashboardPlaybackInfo CreatePlaybackInfo(
        StationRuntimeState? state,
        bool stationLive,
        bool runtimeStatesAgree)
    {
        if (state is null || !runtimeStatesAgree)
        {
            return new DashboardPlaybackInfo(
                DashboardPlaybackAvailability.Unavailable,
                null,
                null,
                null,
                null);
        }

        if (!stationLive || state.StationState != StationState.Broadcasting)
        {
            return new DashboardPlaybackInfo(
                DashboardPlaybackAvailability.NotPlaying,
                null,
                null,
                null,
                null);
        }

        int? currentItemNumber = state.CurrentGlobalIndex is int index ? index + 1 : null;
        return new DashboardPlaybackInfo(
            currentItemNumber is null
                ? DashboardPlaybackAvailability.Unavailable
                : DashboardPlaybackAvailability.Available,
            DashboardTextSanitizer.Sanitize(state.Title),
            DashboardTextSanitizer.Sanitize(state.Type, 80),
            currentItemNumber,
            state.QueueItemCount);
    }

    private static bool RuntimeStatesAgree(
        StationRuntimeState? station,
        RollingStationRuntimeState? rolling) =>
        station is null
        || rolling?.ActiveQueueId is null
        || string.Equals(station.QueueId, rolling.ActiveQueueId, StringComparison.Ordinal);

    private bool? TryProcessExists(int processId)
    {
        try
        {
            return _processExistence.Exists(processId);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return null;
        }
    }

    private static T? ReadState<T>(
        Func<T?> read,
        DashboardIssueCode missing,
        DashboardIssueCode invalid,
        DashboardIssueCode unreadable,
        ICollection<DashboardIssueCode> issues,
        bool addIssue = true)
        where T : class
    {
        try
        {
            T? value = read();
            if (value is null && addIssue)
            {
                AddIssue(issues, missing);
            }

            return value;
        }
        catch (InvalidDataException)
        {
            if (addIssue)
            {
                AddIssue(issues, invalid);
            }
        }
        catch (FileNotFoundException)
        {
            if (addIssue)
            {
                AddIssue(issues, missing);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (addIssue)
            {
                AddIssue(issues, unreadable);
            }
        }

        return null;
    }

    private static void AddIssue(
        ICollection<DashboardIssueCode> issues,
        DashboardIssueCode issue)
    {
        if (!issues.Contains(issue))
        {
            issues.Add(issue);
        }
    }

    private static DashboardStationStatus MapStationStatus(StationState state) => state switch
    {
        StationState.Starting => DashboardStationStatus.Starting,
        StationState.Broadcasting => DashboardStationStatus.Running,
        StationState.Stopping => DashboardStationStatus.Stopping,
        StationState.Stopped => DashboardStationStatus.Stopped,
        StationState.Completed => DashboardStationStatus.Completed,
        StationState.Failed => DashboardStationStatus.Failed,
        _ => DashboardStationStatus.Unknown,
    };

    private static DashboardBroadcastState MapBroadcastState(StationBroadcastState state) =>
        state switch
        {
            StationBroadcastState.Starting => DashboardBroadcastState.Starting,
            StationBroadcastState.Broadcasting => DashboardBroadcastState.Broadcasting,
            StationBroadcastState.Recovering => DashboardBroadcastState.Recovering,
            StationBroadcastState.Stopping => DashboardBroadcastState.Stopping,
            StationBroadcastState.Stopped => DashboardBroadcastState.Stopped,
            StationBroadcastState.Completed => DashboardBroadcastState.Completed,
            StationBroadcastState.Failed => DashboardBroadcastState.Failed,
            _ => DashboardBroadcastState.Unknown,
        };

    private static DashboardRollingPhase MapRollingPhase(RollingStationPhase phase) => phase switch
    {
        RollingStationPhase.Claimed => DashboardRollingPhase.Claimed,
        RollingStationPhase.Executing => DashboardRollingPhase.Executing,
        RollingStationPhase.Stopped => DashboardRollingPhase.Stopped,
        RollingStationPhase.Advancing => DashboardRollingPhase.Advancing,
        RollingStationPhase.WaitingForBlock => DashboardRollingPhase.WaitingForBlock,
        RollingStationPhase.Failed => DashboardRollingPhase.Failed,
        _ => DashboardRollingPhase.Unknown,
    };

    private static DashboardReplenishmentHealth MapReplenishmentHealth(
        RollingReplenishmentHealth health) => health switch
        {
            RollingReplenishmentHealth.Healthy => DashboardReplenishmentHealth.Healthy,
            RollingReplenishmentHealth.Degraded => DashboardReplenishmentHealth.Degraded,
            RollingReplenishmentHealth.Blocked => DashboardReplenishmentHealth.Blocked,
            RollingReplenishmentHealth.SafetyFailure => DashboardReplenishmentHealth.SafetyFailure,
            _ => DashboardReplenishmentHealth.Unknown,
        };
}
