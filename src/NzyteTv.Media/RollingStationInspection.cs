using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record RollingStationValidationResult(
    RollingStationConfiguration? Configuration,
    StationConfiguration? StationConfiguration,
    RollingProgrammingManifest? Manifest,
    RollingStationRuntimeState? RollingState,
    StationRuntimeState? StationState,
    bool FfmpegAvailable,
    BroadcastDestinationStatus DestinationStatus,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public RollingBufferSnapshot? Buffer { get; init; }

    public RollingReplenishmentState? ReplenishmentState { get; init; }

    public bool IsReady => Errors.Count == 0
        && FfmpegAvailable
        && DestinationStatus != BroadcastDestinationStatus.Invalid;
}

public sealed record RollingStationStatusSnapshot(
    RollingStationValidationResult Validation,
    long NextRequiredSequence,
    bool NextBlockCommitted,
    StationStatusSnapshot? StationStatus,
    DateTimeOffset ObservedAtUtc);

public interface IRollingStationInspectionService
{
    RollingStationValidationResult Validate(
        string configurationPath,
        string? configuredDestination);

    RollingStationStatusSnapshot GetStatus(
        string configurationPath,
        string? configuredDestination);
}

public sealed class RollingStationInspectionService : IRollingStationInspectionService
{
    private readonly IRollingStationConfigurationLoader _rollingConfigurationLoader;
    private readonly IStationConfigurationLoader _stationConfigurationLoader;
    private readonly IRollingStationStateStore _rollingStateStore;
    private readonly IStationStateStore _stationStateStore;
    private readonly IRollingPlanStore _planStore;
    private readonly IRollingCommittedBlockResolver _blockResolver;
    private readonly IStationCompletionEvidenceService _completionEvidence;
    private readonly IProcessExistence _processExistence;
    private readonly IRollingReplenishmentStateStore _replenishmentStateStore;
    private readonly Func<string> _locateFfmpeg;
    private readonly TimeProvider _timeProvider;

    public RollingStationInspectionService(
        IRollingStationConfigurationLoader? rollingConfigurationLoader = null,
        IStationConfigurationLoader? stationConfigurationLoader = null,
        IRollingStationStateStore? rollingStateStore = null,
        IStationStateStore? stationStateStore = null,
        IRollingPlanStore? planStore = null,
        IRollingCommittedBlockResolver? blockResolver = null,
        IStationCompletionEvidenceService? completionEvidence = null,
        IProcessExistence? processExistence = null,
        IRollingReplenishmentStateStore? replenishmentStateStore = null,
        Func<string>? locateFfmpeg = null,
        TimeProvider? timeProvider = null)
    {
        _rollingConfigurationLoader = rollingConfigurationLoader
            ?? new RollingStationConfigurationLoader();
        _stationConfigurationLoader = stationConfigurationLoader
            ?? new StationConfigurationLoader();
        _rollingStateStore = rollingStateStore ?? new RollingStationStateStore();
        _stationStateStore = stationStateStore ?? new StationStateStore();
        _planStore = planStore ?? new RollingPlanStore();
        _blockResolver = blockResolver ?? new RollingCommittedBlockResolver(
            _planStore,
            broadcastPlanner: new BroadcastPlanner());
        _processExistence = processExistence ?? new ProcessExistence();
        _replenishmentStateStore = replenishmentStateStore
            ?? new RollingReplenishmentStateStore();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _completionEvidence = completionEvidence ?? new StationCompletionEvidenceService(
            _stationStateStore,
            processExistence: _processExistence,
            timeProvider: _timeProvider);
        _locateFfmpeg = locateFfmpeg ?? MediaToolLocator.LocateFfmpegOnPath;
    }

    public RollingStationValidationResult Validate(
        string configurationPath,
        string? configuredDestination)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        RollingStationConfiguration? configuration = null;
        StationConfiguration? stationConfiguration = null;
        RollingProgrammingManifest? manifest = null;
        RollingStationRuntimeState? rollingState = null;
        StationRuntimeState? stationState = null;
        RollingBufferSnapshot? buffer = null;
        RollingReplenishmentState? replenishmentState = null;

        try
        {
            configuration = _rollingConfigurationLoader.Load(configurationPath);
            stationConfiguration = _stationConfigurationLoader.Load(configuration.StationConfigPath);
            if (PathsEqual(configuration.RollingStatePath, stationConfiguration.StatePath))
            {
                throw new InvalidDataException(
                    "Rolling execution state must use a different path from CP2 station state.");
            }

            RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(
                stationConfiguration.MediaRoot);
            manifest = _planStore.LoadManifest(paths.ManifestPath);
            if (!string.Equals(manifest.PlannerId, configuration.PlannerId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Rolling station configuration and programming manifest planner lineages do not match.");
            }

            foreach (RollingCommittedBlock block in manifest.Blocks!)
            {
                _ = _blockResolver.ResolveManifestBlock(
                    paths,
                    manifest,
                    block.Sequence,
                    stationConfiguration.LibraryRoot);
            }

            rollingState = _rollingStateStore.ReadIfExists(configuration.RollingStatePath);
            stationState = _stationStateStore.ReadIfExists(stationConfiguration.StatePath);
            buffer = RollingBufferPolicy.Calculate(manifest, rollingState);
            ValidateExecutionConsistency(
                configuration,
                stationConfiguration,
                paths,
                manifest,
                rollingState,
                stationState,
                warnings);

            string replenishmentPath = RollingReplenishmentStateStore.GetPath(
                configuration.RollingStatePath);
            try
            {
                replenishmentState = _replenishmentStateStore.ReadIfExists(replenishmentPath);
                if (replenishmentState is not null
                    && !string.Equals(
                        replenishmentState.PlannerId,
                        configuration.PlannerId,
                        StringComparison.Ordinal))
                {
                    warnings.Add(
                        "Replenishment diagnostics belong to a different planner lineage and were ignored.");
                    replenishmentState = null;
                }
                else if (replenishmentState?.Health is RollingReplenishmentHealth.Blocked)
                {
                    warnings.Add(
                        "Future rolling programming generation is blocked; committed execution readiness is unchanged.");
                }
                else if (replenishmentState?.Health is RollingReplenishmentHealth.SafetyFailure)
                {
                    warnings.Add(
                        "Automatic replenishment stopped after a planning safety failure; committed execution readiness is reported separately.");
                }
            }
            catch (Exception exception) when (exception is
                FileNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Replenishment diagnostics are unavailable: {Safe(exception.Message)}");
            }
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException or InvalidDataException or
            IOException or UnauthorizedAccessException or InvalidOperationException or
            RollingStationSafetyException or RollingBlockNotAvailableException)
        {
            errors.Add(Safe(exception.Message));
        }

        bool ffmpegAvailable;
        try
        {
            ffmpegAvailable = !string.IsNullOrWhiteSpace(_locateFfmpeg());
        }
        catch (MediaToolNotFoundException)
        {
            ffmpegAvailable = false;
        }

        return new RollingStationValidationResult(
            configuration,
            stationConfiguration,
            manifest,
            rollingState,
            stationState,
            ffmpegAvailable,
            BroadcastDestination.GetStatus(configuredDestination),
            errors,
            warnings)
        {
            Buffer = buffer,
            ReplenishmentState = replenishmentState,
        };
    }

    public RollingStationStatusSnapshot GetStatus(
        string configurationPath,
        string? configuredDestination)
    {
        RollingStationValidationResult validation = ReadConsistentValidation(
            configurationPath,
            configuredDestination);
        long nextSequence = validation.Buffer?.NextRequiredSequence
            ?? validation.RollingState?.ActiveBlockSequence
            ?? checked((validation.RollingState?.LastCompletedBlockSequence ?? 0) + 1);
        bool nextCommitted = validation.Buffer is { } buffer
            ? buffer.HighestCommittedSequence >= nextSequence
            : validation.Manifest?.Blocks is { } blocks
                && nextSequence >= 1
                && nextSequence <= blocks.Count;
        StationStatusSnapshot? stationStatus = null;
        if (validation.StationConfiguration is not null
            && validation.StationState is not null)
        {
            stationStatus = new StationStatusService(
                _stationStateStore,
                _processExistence,
                _timeProvider).GetStatus(validation.StationConfiguration.StatePath);
        }

        return new RollingStationStatusSnapshot(
            validation,
            nextSequence,
            nextCommitted,
            stationStatus,
            _timeProvider.GetUtcNow());
    }

    private RollingStationValidationResult ReadConsistentValidation(
        string configurationPath,
        string? configuredDestination)
    {
        RollingStationValidationResult? latest = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            latest = Validate(configurationPath, configuredDestination);
            if (latest.Configuration is null || latest.StationConfiguration is null)
            {
                return latest;
            }

            RollingStationRuntimeState? stateAfter;
            RollingProgrammingManifest manifestAfter;
            try
            {
                stateAfter = _rollingStateStore.ReadIfExists(
                    latest.Configuration.RollingStatePath);
                RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(
                    latest.StationConfiguration.MediaRoot);
                manifestAfter = _planStore.LoadManifest(paths.ManifestPath);
            }
            catch (Exception exception) when (exception is
                FileNotFoundException or DirectoryNotFoundException or InvalidDataException or
                IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return latest;
            }
            if (latest.RollingState == stateAfter
                && ManifestMatches(latest.Manifest, manifestAfter))
            {
                return latest;
            }
        }

        return latest! with
        {
            Errors =
            [
                .. latest.Errors,
                "Rolling execution or manifest state changed repeatedly while status was sampled.",
            ],
        };
    }

    private static bool ManifestMatches(
        RollingProgrammingManifest? left,
        RollingProgrammingManifest right)
    {
        if (left is null
            || !string.Equals(left.PlannerId, right.PlannerId, StringComparison.Ordinal)
            || left.TargetPreparedBlockCount != right.TargetPreparedBlockCount
            || left.NextSequence != right.NextSequence
            || left.Blocks?.Count != right.Blocks?.Count)
        {
            return false;
        }

        return left.Blocks!.Zip(right.Blocks!).All(pair =>
            pair.First.Sequence == pair.Second.Sequence
            && string.Equals(pair.First.BlockId, pair.Second.BlockId, StringComparison.Ordinal)
            && string.Equals(
                pair.First.PlaylistSha256,
                pair.Second.PlaylistSha256,
                StringComparison.Ordinal));
    }

    private void ValidateExecutionConsistency(
        RollingStationConfiguration configuration,
        StationConfiguration stationConfiguration,
        RollingProgrammingPaths paths,
        RollingProgrammingManifest manifest,
        RollingStationRuntimeState? rollingState,
        StationRuntimeState? stationState,
        ICollection<string> warnings)
    {
        if (rollingState is null)
        {
            warnings.Add("Rolling coordinator execution state is UNINITIALIZED.");
            if (stationState is { SchemaVersion: StationRuntimeState.LegacySchemaVersion })
            {
                throw new InvalidDataException(
                    "Schema-v1 station state cannot supply trustworthy rolling cutover evidence.");
            }

            if (stationState is not null && _processExistence.Exists(stationState.StationPid))
            {
                throw new InvalidDataException(
                    "A station process is still running; initial rolling cutover is not safe.");
            }

            if (stationState is { StationState: StationState.Stopped })
            {
                warnings.Add(
                    "The static queue is STOPPED and resumable; initial rolling run requires explicit cutover acceptance.");
            }
            else if (stationState is not null
                && stationState.StationState != StationState.Completed)
            {
                throw new InvalidDataException(
                    "Interrupted static station state blocks initial rolling cutover.");
            }

            return;
        }

        if (!string.Equals(rollingState.PlannerId, configuration.PlannerId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Rolling execution state belongs to a different planner lineage.");
        }

        if (rollingState.ActiveBlockSequence is long activeSequence)
        {
            ResolvedRollingCommittedBlock active = _blockResolver.ResolveManifestBlock(
                paths,
                manifest,
                activeSequence,
                stationConfiguration.LibraryRoot);
            if (!string.Equals(active.Block.BlockId, rollingState.ActiveBlockId, StringComparison.Ordinal)
                || !string.Equals(active.QueueId, rollingState.ActiveQueueId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Active rolling claim does not match its committed block and runtime queue identity.");
            }

            if (stationState is null)
            {
                if (activeSequence != 1 || rollingState.LastCompletedBlockSequence is not null)
                {
                    throw new InvalidDataException(
                        "CP2 station state is missing for a non-genesis rolling claim.");
                }

                return;
            }

            bool activeQueue = string.Equals(
                stationState.QueueId,
                active.QueueId,
                StringComparison.Ordinal);
            if (activeQueue)
            {
                if (stationState.QueueItemCount != active.BroadcastPlan.Items.Count)
                {
                    throw new InvalidDataException(
                        "CP2 station item count disagrees with the active rolling claim.");
                }

                return;
            }

            if (activeSequence == 1
                && rollingState.LastCompletedBlockSequence is null
                && (stationState.StationState == StationState.Completed
                    || (stationState.StationState == StationState.Stopped
                        && rollingState.InitialCutoverAcceptedAtUtc is not null
                        && string.Equals(
                            rollingState.InitialCutoverSourceQueueId,
                            stationState.QueueId,
                            StringComparison.Ordinal))))
            {
                return;
            }

            if (rollingState.LastCompletedBlockSequence is long completedSequence)
            {
                ResolvedRollingCommittedBlock completed = _blockResolver.ResolveManifestBlock(
                    paths,
                    manifest,
                    completedSequence,
                    stationConfiguration.LibraryRoot);
                if (stationState.StationState == StationState.Completed
                    && _completionEvidence.IsPositiveCompletion(
                        stationState,
                        completed.QueueId,
                        completed.BroadcastPlan.Items.Count))
                {
                    return;
                }
            }

            throw new InvalidDataException(
                "CP2 station state contradicts the active rolling claim.");
        }

        if (rollingState.LastCompletedBlockSequence is null)
        {
            ValidatePreFirstClaimState(rollingState, stationState);
            return;
        }

        if (rollingState.LastCompletedBlockSequence is long lastCompletedSequence)
        {
            ResolvedRollingCommittedBlock completed = _blockResolver.ResolveManifestBlock(
                paths,
                manifest,
                lastCompletedSequence,
                stationConfiguration.LibraryRoot);
            if (stationState is null
                || !_completionEvidence.IsPositiveCompletion(
                    stationState,
                    completed.QueueId,
                    completed.BroadcastPlan.Items.Count))
            {
                throw new InvalidDataException(
                    "CP2 station state does not confirm the last logically completed rolling block.");
            }
        }
    }

    private void ValidatePreFirstClaimState(
        RollingStationRuntimeState rollingState,
        StationRuntimeState? stationState)
    {
        if (stationState is null)
        {
            if (rollingState.InitialCutoverAcceptedAtUtc is not null)
            {
                throw new InvalidDataException(
                    "Accepted stopped-static cutover evidence is no longer available.");
            }

            return;
        }

        if (stationState.SchemaVersion != StationRuntimeState.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "Schema-v1 station state cannot supply trustworthy rolling cutover evidence.");
        }

        if (_processExistence.Exists(stationState.StationPid))
        {
            throw new InvalidDataException(
                "A static station process owns execution before the first rolling claim.");
        }

        if (rollingState.InitialCutoverAcceptedAtUtc is not null)
        {
            if (stationState.StationState != StationState.Stopped
                || !string.Equals(
                    stationState.QueueId,
                    rollingState.InitialCutoverSourceQueueId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Accepted stopped-static cutover evidence changed before the first rolling claim.");
            }

            return;
        }

        if (stationState.StationState != StationState.Completed)
        {
            throw new InvalidDataException(
                "An unfinished static queue blocks the first rolling claim.");
        }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Safe(string value) =>
        StationSecretRedactor.RedactRtmpUrls(value) ?? "Rolling station validation failed.";
}
