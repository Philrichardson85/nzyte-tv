using NzyteTv.Core;

namespace NzyteTv.Media;

public interface IRollingProgrammingReplenisher
{
    Task RunAsync(string configurationPath, CancellationToken cancellationToken);
}

public sealed class RollingProgrammingReplenisher : IRollingProgrammingReplenisher
{
    private const int ConsistentReadAttempts = 5;

    private readonly IRollingBlockMaintainer _maintainer;
    private readonly IRollingStationConfigurationLoader _rollingConfigurationLoader;
    private readonly IStationConfigurationLoader _stationConfigurationLoader;
    private readonly IRollingStationStateStore _rollingStateStore;
    private readonly IRollingPlanStore _planStore;
    private readonly IRollingReplenishmentStateStore _replenishmentStateStore;
    private readonly IRollingReplenishmentTrigger _trigger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _consistencyCheckInterval;
    private readonly Action<string>? _diagnostic;

    public RollingProgrammingReplenisher(
        IRollingBlockMaintainer maintainer,
        IRollingReplenishmentTrigger trigger,
        IRollingStationConfigurationLoader? rollingConfigurationLoader = null,
        IStationConfigurationLoader? stationConfigurationLoader = null,
        IRollingStationStateStore? rollingStateStore = null,
        IRollingPlanStore? planStore = null,
        IRollingReplenishmentStateStore? replenishmentStateStore = null,
        TimeProvider? timeProvider = null,
        TimeSpan? consistencyCheckInterval = null,
        Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(maintainer);
        ArgumentNullException.ThrowIfNull(trigger);
        _maintainer = maintainer;
        _trigger = trigger;
        _rollingConfigurationLoader = rollingConfigurationLoader
            ?? new RollingStationConfigurationLoader();
        _stationConfigurationLoader = stationConfigurationLoader
            ?? new StationConfigurationLoader();
        _rollingStateStore = rollingStateStore ?? new RollingStationStateStore();
        _planStore = planStore ?? new RollingPlanStore();
        _replenishmentStateStore = replenishmentStateStore
            ?? new RollingReplenishmentStateStore();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _consistencyCheckInterval = consistencyCheckInterval
            ?? RollingReplenishmentPolicy.ConsistencyCheckInterval;
        if (_consistencyCheckInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(consistencyCheckInterval),
                "The replenishment consistency interval must be positive.");
        }

        _diagnostic = diagnostic;
    }

    public async Task RunAsync(string configurationPath, CancellationToken cancellationToken)
    {
        RollingStationConfiguration rollingConfiguration =
            _rollingConfigurationLoader.Load(configurationPath);
        StationConfiguration stationConfiguration =
            _stationConfigurationLoader.Load(rollingConfiguration.StationConfigPath);
        string diagnosticsPath = RollingReplenishmentStateStore.GetPath(
            rollingConfiguration.RollingStatePath);
        RollingReplenishmentState? advisory = ReadAdvisoryIfUsable(
            diagnosticsPath,
            rollingConfiguration.PlannerId);
        bool reconciliationRequired = true;
        int retryIndex = 0;
        long lastObservedHighest = -1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BufferObservation observation;
            try
            {
                observation = ReadConsistentObservation(
                    rollingConfiguration,
                    stationConfiguration);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                FailureKind kind = ClassifyObservationFailure(exception, stationConfiguration.MediaRoot);
                RollingBufferSnapshot fallback = CreateFallbackBuffer(advisory);
                advisory = await PublishFailureAsync(
                    diagnosticsPath,
                    rollingConfiguration.PlannerId,
                    fallback,
                    advisory,
                    kind,
                    exception,
                    attemptedAtUtc: null,
                    cancellationToken).ConfigureAwait(false);
                if (kind == FailureKind.Safety)
                {
                    return;
                }

                await WaitForRetryAsync(retryIndex++, cancellationToken).ConfigureAwait(false);
                continue;
            }

            RollingBufferSnapshot buffer = observation.Buffer;
            if (buffer.HighestCommittedSequence > lastObservedHighest)
            {
                retryIndex = 0;
                lastObservedHighest = buffer.HighestCommittedSequence;
            }

            bool requiresMaintenance = reconciliationRequired || buffer.BufferDeficit > 0;
            if (!requiresMaintenance)
            {
                advisory = await PublishSuccessAsync(
                    diagnosticsPath,
                    rollingConfiguration.PlannerId,
                    buffer,
                    advisory,
                    attemptedAtUtc: null,
                    succeededAtUtc: null,
                    cancellationToken).ConfigureAwait(false);
                await _trigger.WaitAsync(_consistencyCheckInterval, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            DateTimeOffset attemptedAt = _timeProvider.GetUtcNow();
            try
            {
                _ = await _maintainer.EnsureCommittedThroughAsync(
                    stationConfiguration.MediaRoot,
                    buffer.RequiredHighestSequence,
                    cancellationToken).ConfigureAwait(false);
                reconciliationRequired = false;
                retryIndex = 0;
                BufferObservation refreshed = ReadConsistentObservation(
                    rollingConfiguration,
                    stationConfiguration);
                lastObservedHighest = Math.Max(
                    lastObservedHighest,
                    refreshed.Buffer.HighestCommittedSequence);
                DateTimeOffset succeededAt = _timeProvider.GetUtcNow();
                advisory = await PublishSuccessAsync(
                    diagnosticsPath,
                    rollingConfiguration.PlannerId,
                    refreshed.Buffer,
                    advisory,
                    attemptedAt,
                    succeededAt,
                    cancellationToken).ConfigureAwait(false);
                if (refreshed.Buffer.BufferDeficit > 0)
                {
                    continue;
                }

                await _trigger.WaitAsync(_consistencyCheckInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                FailureKind kind = ClassifyMaintenanceFailure(exception);
                RollingBufferSnapshot latest = TryReadLatestBuffer(
                    rollingConfiguration,
                    stationConfiguration) ?? buffer;
                if (latest.HighestCommittedSequence > lastObservedHighest)
                {
                    retryIndex = 0;
                    lastObservedHighest = latest.HighestCommittedSequence;
                }

                advisory = await PublishFailureAsync(
                    diagnosticsPath,
                    rollingConfiguration.PlannerId,
                    latest,
                    advisory,
                    kind,
                    exception,
                    attemptedAt,
                    cancellationToken).ConfigureAwait(false);
                if (kind == FailureKind.Safety)
                {
                    return;
                }

                await WaitForRetryAsync(retryIndex++, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private BufferObservation ReadConsistentObservation(
        RollingStationConfiguration configuration,
        StationConfiguration stationConfiguration)
    {
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(
            stationConfiguration.MediaRoot);
        for (int attempt = 0; attempt < ConsistentReadAttempts; attempt++)
        {
            RollingStationRuntimeState? before = _rollingStateStore.ReadIfExists(
                configuration.RollingStatePath);
            RollingProgrammingManifest manifest = _planStore.LoadManifest(paths.ManifestPath);
            RollingStationRuntimeState? after = _rollingStateStore.ReadIfExists(
                configuration.RollingStatePath);
            if (before != after)
            {
                continue;
            }

            if (!string.Equals(manifest.PlannerId, configuration.PlannerId, StringComparison.Ordinal)
                || (after is not null
                    && !string.Equals(
                        after.PlannerId,
                        configuration.PlannerId,
                        StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "Rolling replenishment inputs belong to different planner lineages.");
            }

            return new BufferObservation(
                manifest,
                after,
                RollingBufferPolicy.Calculate(manifest, after));
        }

        throw new IOException(
            "Rolling execution state changed repeatedly while replenishment read its planning snapshot.");
    }

    private RollingBufferSnapshot? TryReadLatestBuffer(
        RollingStationConfiguration configuration,
        StationConfiguration stationConfiguration)
    {
        try
        {
            return ReadConsistentObservation(configuration, stationConfiguration).Buffer;
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException or InvalidDataException or
            IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<RollingReplenishmentState?> PublishSuccessAsync(
        string path,
        string plannerId,
        RollingBufferSnapshot buffer,
        RollingReplenishmentState? previous,
        DateTimeOffset? attemptedAtUtc,
        DateTimeOffset? succeededAtUtc,
        CancellationToken cancellationToken)
    {
        var next = new RollingReplenishmentState
        {
            PlannerId = plannerId,
            Health = buffer.BufferDeficit == 0
                ? RollingReplenishmentHealth.Healthy
                : RollingReplenishmentHealth.Degraded,
            AnchorSequence = buffer.AnchorSequence,
            HighestCommittedSequence = buffer.HighestCommittedSequence,
            RequiredHighestSequence = buffer.RequiredHighestSequence,
            FutureBlockTarget = buffer.FutureBlockTarget,
            BufferDeficit = buffer.BufferDeficit,
            LastAttemptAtUtc = attemptedAtUtc ?? previous?.LastAttemptAtUtc,
            LastSuccessAtUtc = succeededAtUtc ?? previous?.LastSuccessAtUtc,
            LastErrorAtUtc = previous?.LastErrorAtUtc,
            ErrorClassification = previous?.ErrorClassification
                ?? RollingReplenishmentErrorClassification.None,
            LastError = previous?.LastError,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
        };
        return await TryPublishAsync(path, next, previous, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RollingReplenishmentState?> PublishFailureAsync(
        string path,
        string plannerId,
        RollingBufferSnapshot buffer,
        RollingReplenishmentState? previous,
        FailureKind kind,
        Exception exception,
        DateTimeOffset? attemptedAtUtc,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var next = new RollingReplenishmentState
        {
            PlannerId = plannerId,
            Health = kind switch
            {
                FailureKind.Transient => RollingReplenishmentHealth.Degraded,
                FailureKind.Blocked => RollingReplenishmentHealth.Blocked,
                FailureKind.Safety => RollingReplenishmentHealth.SafetyFailure,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            },
            AnchorSequence = buffer.AnchorSequence,
            HighestCommittedSequence = buffer.HighestCommittedSequence,
            RequiredHighestSequence = buffer.RequiredHighestSequence,
            FutureBlockTarget = buffer.FutureBlockTarget,
            BufferDeficit = buffer.BufferDeficit,
            LastAttemptAtUtc = attemptedAtUtc ?? previous?.LastAttemptAtUtc,
            LastSuccessAtUtc = previous?.LastSuccessAtUtc,
            LastErrorAtUtc = now,
            ErrorClassification = kind switch
            {
                FailureKind.Transient => RollingReplenishmentErrorClassification.Transient,
                FailureKind.Blocked => RollingReplenishmentErrorClassification.PlanningBlocked,
                FailureKind.Safety => RollingReplenishmentErrorClassification.SafetyFailure,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            },
            LastError = Safe(exception.Message),
            UpdatedAtUtc = now,
        };
        return await TryPublishAsync(path, next, previous, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RollingReplenishmentState?> TryPublishAsync(
        string path,
        RollingReplenishmentState next,
        RollingReplenishmentState? previous,
        CancellationToken cancellationToken)
    {
        if (previous is not null && SemanticallyEqual(previous, next))
        {
            return previous;
        }

        try
        {
            await _replenishmentStateStore.WriteAsync(path, next, cancellationToken)
                .ConfigureAwait(false);
            return next;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _diagnostic?.Invoke(
                $"Rolling replenishment diagnostics could not be persisted: {Safe(exception.Message)}");
            return previous;
        }
    }

    private RollingReplenishmentState? ReadAdvisoryIfUsable(string path, string plannerId)
    {
        try
        {
            RollingReplenishmentState? state = _replenishmentStateStore.ReadIfExists(path);
            if (state is not null
                && !string.Equals(state.PlannerId, plannerId, StringComparison.Ordinal))
            {
                _diagnostic?.Invoke(
                    "Existing replenishment diagnostics belong to a different planner lineage and were ignored.");
                return null;
            }

            return state;
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _diagnostic?.Invoke(
                $"Existing replenishment diagnostics could not be read and were ignored: {Safe(exception.Message)}");
            return null;
        }
    }

    private async Task WaitForRetryAsync(int retryIndex, CancellationToken cancellationToken)
    {
        IReadOnlyList<TimeSpan> intervals = RollingReplenishmentPolicy.RetryIntervals;
        TimeSpan delay = intervals[Math.Min(retryIndex, intervals.Count - 1)];
        await _trigger.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
    }

    private static FailureKind ClassifyMaintenanceFailure(Exception exception) => exception switch
    {
        RollingPlanningSafetyException => FailureKind.Safety,
        RollingPlanningBlockedException => FailureKind.Blocked,
        RollingPlannerLockUnavailableException => FailureKind.Transient,
        MediaToolNotFoundException => FailureKind.Transient,
        FileNotFoundException => FailureKind.Transient,
        DirectoryNotFoundException => FailureKind.Transient,
        IOException => FailureKind.Transient,
        UnauthorizedAccessException => FailureKind.Transient,
        InvalidDataException => FailureKind.Safety,
        _ => FailureKind.Safety,
    };

    private static FailureKind ClassifyObservationFailure(Exception exception, string mediaRoot)
    {
        if (exception is DirectoryNotFoundException
            || (exception is FileNotFoundException && !Directory.Exists(mediaRoot)))
        {
            return FailureKind.Transient;
        }

        if (exception is FileNotFoundException)
        {
            return FailureKind.Safety;
        }

        if (exception is IOException or UnauthorizedAccessException)
        {
            return FailureKind.Transient;
        }

        return FailureKind.Safety;
    }

    private static RollingBufferSnapshot CreateFallbackBuffer(
        RollingReplenishmentState? previous) => previous is null
            ? new RollingBufferSnapshot(
                NextRequiredSequence: 1,
                AnchorSequence: 1,
                HighestCommittedSequence: 0,
                FutureBlockTarget: RollingProgrammingPolicy.DefaultTargetPreparedBlockCount - 1,
                CommittedFutureBlockCount: 0,
                RequiredHighestSequence: RollingProgrammingPolicy.DefaultTargetPreparedBlockCount,
                BufferDeficit: RollingProgrammingPolicy.DefaultTargetPreparedBlockCount,
                RollingBufferHealth.Empty)
            : new RollingBufferSnapshot(
                NextRequiredSequence: previous.AnchorSequence,
                previous.AnchorSequence,
                previous.HighestCommittedSequence,
                previous.FutureBlockTarget,
                Math.Max(0, previous.HighestCommittedSequence - previous.AnchorSequence),
                previous.RequiredHighestSequence,
                previous.BufferDeficit,
                previous.BufferDeficit == 0
                    ? RollingBufferHealth.Healthy
                    : previous.HighestCommittedSequence <= previous.AnchorSequence
                        ? RollingBufferHealth.Empty
                        : RollingBufferHealth.Low);

    private static bool SemanticallyEqual(
        RollingReplenishmentState left,
        RollingReplenishmentState right) => left == (right with
        {
            UpdatedAtUtc = left.UpdatedAtUtc,
        });

    private static string Safe(string? value) =>
        StationSecretRedactor.RedactRtmpUrls(value) ?? "Rolling replenishment failed.";

    private sealed record BufferObservation(
        RollingProgrammingManifest Manifest,
        RollingStationRuntimeState? ExecutionState,
        RollingBufferSnapshot Buffer);

    private enum FailureKind
    {
        Transient,
        Blocked,
        Safety,
    }
}
