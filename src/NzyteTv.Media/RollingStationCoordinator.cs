using NzyteTv.Core;

namespace NzyteTv.Media;

public enum RollingCoordinatorCheckpoint
{
    BeforeClaimPersistence,
    AfterClaimPersistence,
    BeforeExecutionStatePersistence,
    AfterExecutionStatePersistence,
    BeforeExecutorInvocation,
    AfterExecutorReturn,
    PositiveCompletionObserved,
    BeforeCompletionSeal,
    AfterCompletionSeal,
    BeforeRollingCompletionPersistence,
    AfterRollingCompletionPersistence,
    BeforeNextBlockClaim,
}

public interface IRollingCoordinatorFaultInjector
{
    void Reach(RollingCoordinatorCheckpoint checkpoint, long sequence);
}

public sealed class RollingCoordinatorSimulatedCrashException(string message)
    : Exception(message);

public sealed class NoOpRollingCoordinatorFaultInjector : IRollingCoordinatorFaultInjector
{
    public static NoOpRollingCoordinatorFaultInjector Instance { get; } = new();

    public void Reach(RollingCoordinatorCheckpoint checkpoint, long sequence)
    {
    }
}

public sealed record RollingStationRunResult(
    int ExitCode,
    RollingStationRuntimeState? FinalState,
    string? Error = null);

public interface IRollingStationCoordinator
{
    Task<RollingStationRunResult> RunAsync(
        string configurationPath,
        bool acceptStoppedStaticCutover,
        CancellationToken cancellationToken);
}

public sealed class RollingStationCoordinator : IRollingStationCoordinator
{
    private readonly IRollingBlockExecutor _executor;
    private readonly IRollingStationConfigurationLoader _rollingConfigurationLoader;
    private readonly IStationConfigurationLoader _stationConfigurationLoader;
    private readonly IRollingStationStateStore _rollingStateStore;
    private readonly IStationStateStore _stationStateStore;
    private readonly IRollingPlanStore _planStore;
    private readonly IRollingCommittedBlockResolver _blockResolver;
    private readonly IStationCompletionEvidenceService _completionEvidence;
    private readonly IRollingCoordinatorLockProvider _lockProvider;
    private readonly IProcessExistence _processExistence;
    private readonly IRollingCoordinatorFaultInjector _faultInjector;
    private readonly TimeProvider _timeProvider;
    private readonly Func<int> _processId;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public RollingStationCoordinator(
        IRollingBlockExecutor executor,
        IRollingStationConfigurationLoader? rollingConfigurationLoader = null,
        IStationConfigurationLoader? stationConfigurationLoader = null,
        IRollingStationStateStore? rollingStateStore = null,
        IStationStateStore? stationStateStore = null,
        IRollingPlanStore? planStore = null,
        IRollingCommittedBlockResolver? blockResolver = null,
        IStationCompletionEvidenceService? completionEvidence = null,
        IRollingCoordinatorLockProvider? lockProvider = null,
        IProcessExistence? processExistence = null,
        IRollingCoordinatorFaultInjector? faultInjector = null,
        TimeProvider? timeProvider = null,
        Func<int>? processId = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        _executor = executor;
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
        _timeProvider = timeProvider ?? TimeProvider.System;
        _completionEvidence = completionEvidence ?? new StationCompletionEvidenceService(
            _stationStateStore,
            processExistence: _processExistence,
            timeProvider: _timeProvider);
        _lockProvider = lockProvider ?? new RollingCoordinatorLockProvider();
        _faultInjector = faultInjector ?? NoOpRollingCoordinatorFaultInjector.Instance;
        _processId = processId ?? (() => Environment.ProcessId);
        _delay = delay ?? ((value, token) => Task.Delay(value, token));
    }

    public async Task<RollingStationRunResult> RunAsync(
        string configurationPath,
        bool acceptStoppedStaticCutover,
        CancellationToken cancellationToken)
    {
        RollingStationConfiguration rollingConfiguration;
        StationConfiguration stationConfiguration;
        try
        {
            (rollingConfiguration, stationConfiguration) = LoadConfiguration(configurationPath);
        }
        catch (RollingMediaUnavailableException exception)
        {
            return new RollingStationRunResult(1, null, Safe(exception.Message));
        }
        catch (Exception exception) when (IsInitialConfigurationFailure(exception))
        {
            return new RollingStationRunResult(
                StationExitCodes.PermanentStartupFailure,
                null,
                Safe(exception.Message));
        }

        RollingStationRuntimeState? state = null;
        try
        {
            using IRollingCoordinatorLock coordinatorLock = _lockProvider.Acquire(
                stationConfiguration.StatePath);
            state = _rollingStateStore.ReadIfExists(rollingConfiguration.RollingStatePath);
            if (state is not null
                && !string.Equals(
                    state.PlannerId,
                    rollingConfiguration.PlannerId,
                    StringComparison.Ordinal))
            {
                throw new RollingStationSafetyException(
                    "Rolling execution state belongs to a different planner lineage.");
            }

            if (state is not null && acceptStoppedStaticCutover)
            {
                throw new RollingStationSafetyException(
                    "--accept-stopped-static-cutover is valid only before rolling execution state exists.");
            }

            int waitIndex = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RollingProgrammingManifest manifest = LoadManifest(
                    stationConfiguration.MediaRoot,
                    stationConfiguration.LibraryRoot,
                    rollingConfiguration.PlannerId);
                RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(
                    stationConfiguration.MediaRoot);

                if (HasActiveClaim(state))
                {
                    ActiveRunOutcome outcome = await RunActiveClaimAsync(
                        rollingConfiguration,
                        stationConfiguration,
                        paths,
                        manifest,
                        state!,
                        cancellationToken).ConfigureAwait(false);
                    state = outcome.State;
                    if (outcome.ExitCode is int exitCode)
                    {
                        return new RollingStationRunResult(exitCode, state, outcome.Error);
                    }

                    waitIndex = 0;
                    continue;
                }

                long nextSequence = checked((state?.LastCompletedBlockSequence ?? 0) + 1);
                ResolvedRollingCommittedBlock nextBlock;
                try
                {
                    nextBlock = _blockResolver.ResolveManifestBlock(
                        paths,
                        manifest,
                        nextSequence,
                        stationConfiguration.LibraryRoot);
                }
                catch (RollingBlockNotAvailableException)
                {
                    InitialCutoverAudit waitingCutover = state is null
                        ? ValidateInitialCutover(
                            stationConfiguration.StatePath,
                            acceptStoppedStaticCutover)
                        : await ValidateBoundaryAsync(
                            stationConfiguration,
                            paths,
                            manifest,
                            state,
                            cancellationToken).ConfigureAwait(false);
                    state = await EnterWaitingAsync(
                        rollingConfiguration,
                        state,
                        waitingCutover,
                        cancellationToken).ConfigureAwait(false);
                    acceptStoppedStaticCutover = false;
                    TimeSpan delay = RollingStationRuntimePolicy.MissingBlockPollIntervals[
                        Math.Min(waitIndex, RollingStationRuntimePolicy.MissingBlockPollIntervals.Count - 1)];
                    waitIndex++;
                    await _delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                InitialCutoverAudit cutover = state is null
                    ? ValidateInitialCutover(
                        stationConfiguration.StatePath,
                        acceptStoppedStaticCutover)
                    : await ValidateBoundaryAsync(
                        stationConfiguration,
                        paths,
                        manifest,
                        state,
                        cancellationToken).ConfigureAwait(false);
                _faultInjector.Reach(
                    RollingCoordinatorCheckpoint.BeforeNextBlockClaim,
                    nextSequence);
                state = await ClaimAsync(
                    rollingConfiguration,
                    stationConfiguration,
                    manifest,
                    nextBlock,
                    state,
                    cutover,
                    cancellationToken).ConfigureAwait(false);
                acceptStoppedStaticCutover = false;
                waitIndex = 0;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (state is not null
                && HasActiveClaim(state)
                && state.Phase is (RollingStationPhase.Claimed or RollingStationPhase.Executing))
            {
                state = await WriteStoppedAsync(
                    rollingConfiguration.RollingStatePath,
                    state).ConfigureAwait(false);
            }

            return new RollingStationRunResult(0, state);
        }
        catch (RollingStationBusyException exception)
        {
            state = await TryWriteFailureAsync(
                rollingConfiguration.RollingStatePath,
                state,
                RollingStationFailureDisposition.Permanent,
                exception.Message).ConfigureAwait(false);
            return new RollingStationRunResult(
                StationExitCodes.PermanentStartupFailure,
                state,
                Safe(exception.Message));
        }
        catch (Exception exception) when (IsSafetyFailure(exception))
        {
            state = await TryWriteFailureAsync(
                rollingConfiguration.RollingStatePath,
                state,
                RollingStationFailureDisposition.Permanent,
                exception.Message).ConfigureAwait(false);
            return new RollingStationRunResult(
                StationExitCodes.PermanentStartupFailure,
                state,
                Safe(exception.Message));
        }
        catch (Exception exception) when (exception is IOException)
        {
            state = await TryWriteFailureAsync(
                rollingConfiguration.RollingStatePath,
                state,
                RollingStationFailureDisposition.Restartable,
                exception.Message).ConfigureAwait(false);
            return new RollingStationRunResult(1, state, Safe(exception.Message));
        }
    }

    private async Task<ActiveRunOutcome> RunActiveClaimAsync(
        RollingStationConfiguration rollingConfiguration,
        StationConfiguration stationConfiguration,
        RollingProgrammingPaths paths,
        RollingProgrammingManifest manifest,
        RollingStationRuntimeState state,
        CancellationToken cancellationToken)
    {
        long sequence = state.ActiveBlockSequence!.Value;
        ResolvedRollingCommittedBlock active;
        try
        {
            active = _blockResolver.ResolveManifestBlock(
                paths,
                manifest,
                sequence,
                stationConfiguration.LibraryRoot);
        }
        catch (RollingBlockNotAvailableException exception)
        {
            throw new RollingStationSafetyException(
                $"Previously claimed rolling block {sequence} is no longer present in the manifest.",
                exception);
        }

        if (!string.Equals(active.Block.BlockId, state.ActiveBlockId, StringComparison.Ordinal)
            || !string.Equals(active.QueueId, state.ActiveQueueId, StringComparison.Ordinal))
        {
            throw new RollingStationSafetyException(
                $"Claimed rolling block {sequence} no longer matches its immutable block or runtime queue identity.");
        }

        StationRuntimeState? stationState = ReadStationState(stationConfiguration.StatePath);
        if (stationState is not null
            && _completionEvidence.IsPositiveCompletion(
                stationState,
                active.QueueId,
                active.BroadcastPlan.Items.Count))
        {
            RollingStationRuntimeState advanced = await CompleteBlockAsync(
                rollingConfiguration,
                stationConfiguration,
                state,
                active,
                controlledExecutionFinished: false,
                cancellationToken).ConfigureAwait(false);
            return new ActiveRunOutcome(advanced, null, null);
        }

        EnsureClaimCanExecute(
            stationConfiguration,
            paths,
            manifest,
            state,
            active,
            stationState);

        RollingStationRuntimeState executing = state with
        {
            Phase = RollingStationPhase.Executing,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
            FailureDisposition = null,
            LastTransitionError = null,
        };
        _faultInjector.Reach(
            RollingCoordinatorCheckpoint.BeforeExecutionStatePersistence,
            sequence);
        await PersistTransitionAsync(
            rollingConfiguration.RollingStatePath,
            state,
            executing,
            CancellationToken.None).ConfigureAwait(false);
        _faultInjector.Reach(
            RollingCoordinatorCheckpoint.AfterExecutionStatePersistence,
            sequence);

        StationConfiguration executionConfiguration = stationConfiguration with
        {
            Playlists = [active.PlaylistPath],
        };
        StationRunResult result;
        try
        {
            _faultInjector.Reach(
                RollingCoordinatorCheckpoint.BeforeExecutorInvocation,
                sequence);
            result = await _executor.RunAsync(
                executionConfiguration,
                active.BroadcastPlan,
                cancellationToken).ConfigureAwait(false);
            _faultInjector.Reach(
                RollingCoordinatorCheckpoint.AfterExecutorReturn,
                sequence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RollingStationRuntimeState stopped = await WriteStoppedAsync(
                rollingConfiguration.RollingStatePath,
                executing).ConfigureAwait(false);
            return new ActiveRunOutcome(stopped, 0, null);
        }
        catch (StationStartupException exception)
        {
            StationRuntimeState? latest = ReadStationState(stationConfiguration.StatePath);
            if (latest is not null && IsLiveActiveStation(latest))
            {
                throw new RollingStationBusyException(
                    "Another supervised station process acquired the CP2 execution lock.",
                    exception);
            }

            throw new RollingStationSafetyException(Safe(exception.Message), exception);
        }
        catch (Exception exception) when (exception is not
            (RollingStationSafetyException or RollingCoordinatorSimulatedCrashException))
        {
            RollingStationRuntimeState failed = await WriteFailureAsync(
                rollingConfiguration.RollingStatePath,
                executing,
                RollingStationFailureDisposition.Restartable,
                exception.Message).ConfigureAwait(false);
            return new ActiveRunOutcome(failed, 1, Safe(exception.Message));
        }

        StationRuntimeState durable = _stationStateStore.Read(stationConfiguration.StatePath);
        if (_completionEvidence.IsPositiveCompletion(
            durable,
            active.QueueId,
            active.BroadcastPlan.Items.Count))
        {
            RollingStationRuntimeState advanced = await CompleteBlockAsync(
                rollingConfiguration,
                stationConfiguration,
                executing,
                active,
                controlledExecutionFinished: true,
                cancellationToken).ConfigureAwait(false);
            return new ActiveRunOutcome(advanced, null, null);
        }

        if (cancellationToken.IsCancellationRequested
            || durable.StationState == StationState.Stopped)
        {
            RollingStationRuntimeState stopped = await WriteStoppedAsync(
                rollingConfiguration.RollingStatePath,
                executing).ConfigureAwait(false);
            return new ActiveRunOutcome(stopped, 0, null);
        }

        if (result.ExitCode != 0 || durable.StationState == StationState.Failed)
        {
            string error = result.Error ?? durable.LastError ?? "Rolling block execution failed.";
            RollingStationRuntimeState failed = await WriteFailureAsync(
                rollingConfiguration.RollingStatePath,
                executing,
                RollingStationFailureDisposition.Restartable,
                error).ConfigureAwait(false);
            return new ActiveRunOutcome(failed, 1, Safe(error));
        }

        throw new RollingStationSafetyException(
            "Station execution returned success without durable positive block-completion evidence.");
    }

    private async Task<RollingStationRuntimeState> CompleteBlockAsync(
        RollingStationConfiguration rollingConfiguration,
        StationConfiguration stationConfiguration,
        RollingStationRuntimeState state,
        ResolvedRollingCommittedBlock active,
        bool controlledExecutionFinished,
        CancellationToken cancellationToken)
    {
        long sequence = active.Block.Sequence;
        _faultInjector.Reach(
            RollingCoordinatorCheckpoint.PositiveCompletionObserved,
            sequence);
        _faultInjector.Reach(
            RollingCoordinatorCheckpoint.BeforeCompletionSeal,
            sequence);
        _ = await _completionEvidence.ConfirmAndSealAsync(
            stationConfiguration.StatePath,
            active.QueueId,
            active.BroadcastPlan.Items.Count,
            _processId(),
            controlledExecutionFinished,
            CancellationToken.None).ConfigureAwait(false);
        _faultInjector.Reach(
            RollingCoordinatorCheckpoint.AfterCompletionSeal,
            sequence);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        RollingStationRuntimeState advanced = state with
        {
            Phase = RollingStationPhase.Advancing,
            ActiveBlockSequence = null,
            ActiveBlockId = null,
            ActiveQueueId = null,
            ClaimedAtUtc = null,
            LastCompletedBlockSequence = sequence,
            LastCompletedBlockId = active.Block.BlockId,
            LastCompletedAtUtc = now,
            UpdatedAtUtc = now,
            FailureDisposition = null,
            LastTransitionError = null,
        };
        _faultInjector.Reach(
            RollingCoordinatorCheckpoint.BeforeRollingCompletionPersistence,
            sequence);
        await PersistTransitionAsync(
            rollingConfiguration.RollingStatePath,
            state,
            advanced,
            CancellationToken.None).ConfigureAwait(false);
        _faultInjector.Reach(
            RollingCoordinatorCheckpoint.AfterRollingCompletionPersistence,
            sequence);
        return advanced;
    }

    private async Task<RollingStationRuntimeState> ClaimAsync(
        RollingStationConfiguration rollingConfiguration,
        StationConfiguration stationConfiguration,
        RollingProgrammingManifest manifest,
        ResolvedRollingCommittedBlock block,
        RollingStationRuntimeState? priorState,
        InitialCutoverAudit cutover,
        CancellationToken cancellationToken)
    {
        long sequence = block.Block.Sequence;
        if (block.Block.ParentBlockId is null != (sequence == 1)
            || (priorState?.LastCompletedBlockId is string completedId
                && !string.Equals(block.Block.ParentBlockId, completedId, StringComparison.Ordinal)))
        {
            throw new RollingStationSafetyException(
                $"Rolling block {sequence} does not follow the last logically completed block.");
        }

        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(
            stationConfiguration.MediaRoot);
        RollingProgrammingManifest latest = _planStore.LoadManifest(paths.ManifestPath);
        EnsureLineage(latest, rollingConfiguration.PlannerId);
        ResolvedRollingCommittedBlock rechecked = _blockResolver.ResolveManifestBlock(
            paths,
            latest,
            sequence,
            stationConfiguration.LibraryRoot);
        if (rechecked.Block != block.Block
            || !string.Equals(rechecked.QueueId, block.QueueId, StringComparison.Ordinal))
        {
            throw new RollingStationSafetyException(
                $"Rolling block {sequence} changed while its claim was being prepared.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        var claimed = new RollingStationRuntimeState
        {
            PlannerId = rollingConfiguration.PlannerId,
            Phase = RollingStationPhase.Claimed,
            ActiveBlockSequence = sequence,
            ActiveBlockId = block.Block.BlockId,
            ActiveQueueId = block.QueueId,
            LastCompletedBlockSequence = priorState?.LastCompletedBlockSequence,
            LastCompletedBlockId = priorState?.LastCompletedBlockId,
            LastCompletedAtUtc = priorState?.LastCompletedAtUtc,
            ClaimedAtUtc = now,
            UpdatedAtUtc = now,
            InitialCutoverSourceQueueId = priorState?.InitialCutoverSourceQueueId
                ?? cutover.SourceQueueId,
            InitialCutoverAcceptedAtUtc = priorState?.InitialCutoverAcceptedAtUtc
                ?? cutover.AcceptedAtUtc,
        };
        _faultInjector.Reach(
            RollingCoordinatorCheckpoint.BeforeClaimPersistence,
            sequence);
        await PersistTransitionAsync(
            rollingConfiguration.RollingStatePath,
            priorState,
            claimed,
            CancellationToken.None).ConfigureAwait(false);
        _faultInjector.Reach(
            RollingCoordinatorCheckpoint.AfterClaimPersistence,
            sequence);
        return claimed;
    }

    private InitialCutoverAudit ValidateInitialCutover(
        string stationStatePath,
        bool acceptStoppedStaticCutover)
    {
        StationRuntimeState? stationState = ReadStationState(stationStatePath);
        if (stationState is null)
        {
            if (acceptStoppedStaticCutover)
            {
                throw new RollingStationSafetyException(
                    "--accept-stopped-static-cutover requires an existing STOPPED schema-v2 station state.");
            }

            return InitialCutoverAudit.None;
        }

        if (stationState.SchemaVersion != StationRuntimeState.CurrentSchemaVersion)
        {
            throw new RollingStationSafetyException(
                "Schema-v1 station state cannot supply trustworthy rolling cutover evidence.");
        }

        if (_processExistence.Exists(stationState.StationPid))
        {
            throw new RollingStationSafetyException(
                "A station process is still running; rolling cutover was refused.");
        }

        if (stationState.StationState == StationState.Completed)
        {
            if (acceptStoppedStaticCutover)
            {
                throw new RollingStationSafetyException(
                    "--accept-stopped-static-cutover may be used only with STOPPED station state.");
            }

            return InitialCutoverAudit.None;
        }

        if (stationState.StationState == StationState.Stopped)
        {
            if (!acceptStoppedStaticCutover)
            {
                throw new RollingStationSafetyException(
                    "An unfinished STOPPED static queue remains resumable. " +
                    "Rolling cutover requires --accept-stopped-static-cutover.");
            }

            return new InitialCutoverAudit(
                stationState.QueueId!,
                _timeProvider.GetUtcNow());
        }

        throw new RollingStationSafetyException(
            "Interrupted nonterminal static station state cannot be abandoned by rolling cutover.");
    }

    private async Task<InitialCutoverAudit> ValidateBoundaryAsync(
        StationConfiguration stationConfiguration,
        RollingProgrammingPaths paths,
        RollingProgrammingManifest manifest,
        RollingStationRuntimeState state,
        CancellationToken cancellationToken)
    {
        if (state.LastCompletedBlockSequence is not long completedSequence
            || state.LastCompletedBlockId is null)
        {
            ValidatePreFirstClaimBoundary(stationConfiguration.StatePath, state);
            return InitialCutoverAudit.None;
        }

        ResolvedRollingCommittedBlock completedBlock;
        try
        {
            completedBlock = _blockResolver.ResolveManifestBlock(
                paths,
                manifest,
                completedSequence,
                stationConfiguration.LibraryRoot);
        }
        catch (RollingBlockNotAvailableException exception)
        {
            throw new RollingStationSafetyException(
                "The last completed rolling block is no longer present in the manifest.",
                exception);
        }
        if (!string.Equals(
            completedBlock.Block.BlockId,
            state.LastCompletedBlockId,
            StringComparison.Ordinal))
        {
            throw new RollingStationSafetyException(
                "Last completed rolling block no longer matches the manifest.");
        }

        StationRuntimeState stationState = _stationStateStore.Read(stationConfiguration.StatePath);
        if (!_completionEvidence.IsPositiveCompletion(
            stationState,
            completedBlock.QueueId,
            completedBlock.BroadcastPlan.Items.Count))
        {
            if (IsLiveActiveStation(stationState))
            {
                throw new RollingStationBusyException(
                    "A static station acquired execution ownership during the rolling block boundary.");
            }

            throw new RollingStationSafetyException(
                "CP2 station state no longer agrees with the last completed rolling block.");
        }

        if (stationState.StationState != StationState.Completed)
        {
            _ = await _completionEvidence.ConfirmAndSealAsync(
                stationConfiguration.StatePath,
                completedBlock.QueueId,
                completedBlock.BroadcastPlan.Items.Count,
                _processId(),
                controlledExecutionFinished: false,
                CancellationToken.None).ConfigureAwait(false);
        }

        return InitialCutoverAudit.None;
    }

    private void ValidatePreFirstClaimBoundary(
        string stationStatePath,
        RollingStationRuntimeState rollingState)
    {
        StationRuntimeState? stationState = ReadStationState(stationStatePath);
        if (stationState is null)
        {
            if (rollingState.InitialCutoverAcceptedAtUtc is not null)
            {
                throw new RollingStationSafetyException(
                    "The accepted STOPPED static cutover state is no longer available for verification.");
            }

            return;
        }

        if (stationState.SchemaVersion != StationRuntimeState.CurrentSchemaVersion)
        {
            throw new RollingStationSafetyException(
                "Schema-v1 station state cannot supply trustworthy rolling cutover evidence.");
        }

        if (_processExistence.Exists(stationState.StationPid))
        {
            throw new RollingStationBusyException(
                "A static station acquired execution ownership before the first rolling block claim.");
        }

        if (rollingState.InitialCutoverAcceptedAtUtc is not null)
        {
            if (stationState.StationState != StationState.Stopped
                || !string.Equals(
                    stationState.QueueId,
                    rollingState.InitialCutoverSourceQueueId,
                    StringComparison.Ordinal))
            {
                throw new RollingStationSafetyException(
                    "The accepted STOPPED static cutover evidence changed before the first rolling claim.");
            }

            return;
        }

        if (stationState.StationState != StationState.Completed)
        {
            throw new RollingStationSafetyException(
                "An unfinished static queue blocks the first rolling block claim.");
        }
    }

    private void EnsureClaimCanExecute(
        StationConfiguration stationConfiguration,
        RollingProgrammingPaths paths,
        RollingProgrammingManifest manifest,
        RollingStationRuntimeState rollingState,
        ResolvedRollingCommittedBlock active,
        StationRuntimeState? stationState)
    {
        if (stationState is null)
        {
            if (active.Block.Sequence == 1
                && rollingState.LastCompletedBlockSequence is null)
            {
                return;
            }

            throw new RollingStationSafetyException(
                "CP2 station state is missing for a non-genesis rolling claim.");
        }

        bool sameQueue = string.Equals(
            stationState.QueueId,
            active.QueueId,
            StringComparison.Ordinal);
        if (sameQueue)
        {
            if (IsLiveActiveStation(stationState))
            {
                throw new RollingStationBusyException(
                    "A live station supervisor already owns the claimed rolling queue.");
            }

            return;
        }

        if (active.Block.Sequence == 1
            && rollingState.LastCompletedBlockSequence is null)
        {
            bool acceptedStopped = stationState.StationState == StationState.Stopped
                && string.Equals(
                    rollingState.InitialCutoverSourceQueueId,
                    stationState.QueueId,
                    StringComparison.Ordinal)
                && rollingState.InitialCutoverAcceptedAtUtc is not null;
            if (stationState.StationState == StationState.Completed || acceptedStopped)
            {
                if (_processExistence.Exists(stationState.StationPid))
                {
                    throw new RollingStationBusyException(
                        "A live station process still owns the pre-cutover station state.");
                }

                return;
            }
        }

        if (rollingState.LastCompletedBlockSequence is long completedSequence)
        {
            ResolvedRollingCommittedBlock prior;
            try
            {
                prior = _blockResolver.ResolveManifestBlock(
                    paths,
                    manifest,
                    completedSequence,
                    stationConfiguration.LibraryRoot);
            }
            catch (RollingBlockNotAvailableException exception)
            {
                throw new RollingStationSafetyException(
                    "The last completed rolling block is no longer present in the manifest.",
                    exception);
            }

            if (stationState.StationState == StationState.Completed
                && _completionEvidence.IsPositiveCompletion(
                    stationState,
                    prior.QueueId,
                    prior.BroadcastPlan.Items.Count))
            {
                return;
            }
        }

        if (IsLiveActiveStation(stationState))
        {
            throw new RollingStationBusyException(
                "A different live supervised station queue owns the CP2 state path.");
        }

        throw new RollingStationSafetyException(
            "CP2 station state belongs to a different incomplete or contradictory queue.");
    }

    private async Task<RollingStationRuntimeState> EnterWaitingAsync(
        RollingStationConfiguration configuration,
        RollingStationRuntimeState? state,
        InitialCutoverAudit cutover,
        CancellationToken cancellationToken)
    {
        if (state?.Phase == RollingStationPhase.WaitingForBlock)
        {
            return state;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        RollingStationRuntimeState waiting = state is null
            ? new RollingStationRuntimeState
            {
                PlannerId = configuration.PlannerId,
                Phase = RollingStationPhase.WaitingForBlock,
                UpdatedAtUtc = now,
                InitialCutoverSourceQueueId = cutover.SourceQueueId,
                InitialCutoverAcceptedAtUtc = cutover.AcceptedAtUtc,
            }
            : state with
            {
                Phase = RollingStationPhase.WaitingForBlock,
                ActiveBlockSequence = null,
                ActiveBlockId = null,
                ActiveQueueId = null,
                ClaimedAtUtc = null,
                UpdatedAtUtc = now,
                FailureDisposition = null,
                LastTransitionError = null,
                InitialCutoverSourceQueueId = state.InitialCutoverSourceQueueId
                    ?? cutover.SourceQueueId,
                InitialCutoverAcceptedAtUtc = state.InitialCutoverAcceptedAtUtc
                    ?? cutover.AcceptedAtUtc,
            };
        await PersistTransitionAsync(
            configuration.RollingStatePath,
            state,
            waiting,
            CancellationToken.None).ConfigureAwait(false);
        return waiting;
    }

    private async Task<RollingStationRuntimeState> WriteStoppedAsync(
        string path,
        RollingStationRuntimeState state)
    {
        RollingStationRuntimeState stopped = state with
        {
            Phase = RollingStationPhase.Stopped,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
            FailureDisposition = null,
            LastTransitionError = null,
        };
        await PersistTransitionAsync(path, state, stopped, CancellationToken.None)
            .ConfigureAwait(false);
        return stopped;
    }

    private async Task<RollingStationRuntimeState> WriteFailureAsync(
        string path,
        RollingStationRuntimeState state,
        RollingStationFailureDisposition disposition,
        string error)
    {
        RollingStationRuntimeState failed = state with
        {
            Phase = RollingStationPhase.Failed,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
            FailureDisposition = disposition,
            LastTransitionError = Safe(error),
        };
        await PersistTransitionAsync(path, state, failed, CancellationToken.None)
            .ConfigureAwait(false);
        return failed;
    }

    private Task PersistTransitionAsync(
        string path,
        RollingStationRuntimeState? current,
        RollingStationRuntimeState next,
        CancellationToken cancellationToken)
    {
        if (!RollingStationStateMachine.CanTransition(current?.Phase, next.Phase))
        {
            throw new RollingStationSafetyException(
                $"Illegal rolling station phase transition from " +
                $"{current?.Phase.ToString() ?? "UNINITIALIZED"} to {next.Phase}.");
        }

        return _rollingStateStore.WriteAsync(path, next, cancellationToken);
    }

    private async Task<RollingStationRuntimeState?> TryWriteFailureAsync(
        string path,
        RollingStationRuntimeState? state,
        RollingStationFailureDisposition disposition,
        string error)
    {
        if (state is null)
        {
            return null;
        }

        try
        {
            return await WriteFailureAsync(path, state, disposition, error).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return state;
        }
    }

    private (RollingStationConfiguration Rolling, StationConfiguration Station) LoadConfiguration(
        string path)
    {
        RollingStationConfiguration rolling = _rollingConfigurationLoader.Load(path);
        StationConfiguration station;
        try
        {
            station = _stationConfigurationLoader.Load(rolling.StationConfigPath);
        }
        catch (Exception exception) when (
            exception is (DirectoryNotFoundException or FileNotFoundException)
            && File.Exists(rolling.RollingStatePath))
        {
            throw new RollingMediaUnavailableException(
                "The configured rolling station media paths are unavailable. The media mount may be offline.",
                exception);
        }

        if (PathsEqual(rolling.RollingStatePath, station.StatePath))
        {
            throw new InvalidDataException(
                "Rolling execution state must use a different path from CP2 station state.");
        }

        return (rolling, station);
    }

    private RollingProgrammingManifest LoadManifest(
        string mediaRoot,
        string libraryRoot,
        string expectedPlannerId)
    {
        if (!Directory.Exists(mediaRoot) || !Directory.Exists(libraryRoot))
        {
            throw new RollingMediaUnavailableException(
                "The rolling station media root or library is unavailable. The media mount may be offline.");
        }

        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(mediaRoot);
        RollingProgrammingManifest manifest;
        try
        {
            manifest = _planStore.LoadManifest(paths.ManifestPath);
        }
        catch (FileNotFoundException exception)
        {
            throw new RollingStationSafetyException(
                "The rolling programming manifest is missing; execution cannot infer committed blocks.",
                exception);
        }

        EnsureLineage(manifest, expectedPlannerId);
        return manifest;
    }

    private static void EnsureLineage(
        RollingProgrammingManifest manifest,
        string expectedPlannerId)
    {
        if (!string.Equals(manifest.PlannerId, expectedPlannerId, StringComparison.Ordinal))
        {
            throw new RollingStationSafetyException(
                "Rolling programming manifest belongs to a different planner lineage.");
        }
    }

    private StationRuntimeState? ReadStationState(string path) =>
        _stationStateStore.ReadIfExists(path);

    private bool IsLiveActiveStation(StationRuntimeState state) =>
        state.StationState is (StationState.Starting or StationState.Broadcasting or StationState.Stopping)
        && _processExistence.Exists(state.StationPid);

    private static bool HasActiveClaim(RollingStationRuntimeState? state) =>
        state?.ActiveBlockSequence is not null;

    private static bool IsInitialConfigurationFailure(Exception exception) => exception is
        FileNotFoundException or
        DirectoryNotFoundException or
        RollingStationSafetyException or
        InvalidDataException or
        InvalidOperationException or
        UnauthorizedAccessException or
        ArgumentException;

    private static bool IsSafetyFailure(Exception exception) => exception is
        RollingStationSafetyException or
        RollingBlockNotAvailableException or
        StationStateLockUnavailableException or
        InvalidDataException or
        InvalidOperationException or
        UnauthorizedAccessException or
        ArgumentException;

    private static string Safe(string value) =>
        StationSecretRedactor.RedactRtmpUrls(value) ?? "Rolling station operation failed.";

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record ActiveRunOutcome(
        RollingStationRuntimeState State,
        int? ExitCode,
        string? Error);

    private sealed record InitialCutoverAudit(
        string? SourceQueueId,
        DateTimeOffset? AcceptedAtUtc)
    {
        public static InitialCutoverAudit None { get; } = new(null, null);
    }
}

public sealed class RollingStationBusyException(string message, Exception? innerException = null)
    : Exception(message, innerException);
