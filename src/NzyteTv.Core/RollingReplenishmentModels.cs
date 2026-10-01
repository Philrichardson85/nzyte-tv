using System.Text.Json.Serialization;

namespace NzyteTv.Core;

public enum RollingBufferHealth
{
    Healthy,
    Low,
    Empty,
}

public enum RollingReplenishmentHealth
{
    Healthy,
    Degraded,
    Blocked,
    SafetyFailure,
}

public enum RollingReplenishmentErrorClassification
{
    None,
    Transient,
    PlanningBlocked,
    SafetyFailure,
}

public sealed record RollingBufferSnapshot(
    long NextRequiredSequence,
    long AnchorSequence,
    long HighestCommittedSequence,
    int FutureBlockTarget,
    long CommittedFutureBlockCount,
    long RequiredHighestSequence,
    long BufferDeficit,
    RollingBufferHealth Health);

public sealed record RollingReplenishmentState
{
    public const int CurrentSchemaVersion = 1;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonRequired]
    public string PlannerId { get; init; } = string.Empty;

    [JsonRequired]
    public RollingReplenishmentHealth Health { get; init; }

    public long AnchorSequence { get; init; }

    public long HighestCommittedSequence { get; init; }

    public long RequiredHighestSequence { get; init; }

    public int FutureBlockTarget { get; init; }

    public long BufferDeficit { get; init; }

    public DateTimeOffset? LastAttemptAtUtc { get; init; }

    public DateTimeOffset? LastSuccessAtUtc { get; init; }

    public DateTimeOffset? LastErrorAtUtc { get; init; }

    [JsonRequired]
    public RollingReplenishmentErrorClassification ErrorClassification { get; init; }

    public string? LastError { get; init; }

    [JsonRequired]
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public static class RollingReplenishmentPolicy
{
    public static readonly TimeSpan ConsistencyCheckInterval = TimeSpan.FromSeconds(45);

    public static readonly IReadOnlyList<TimeSpan> RetryIntervals =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];
}

public static class RollingBufferPolicy
{
    public static RollingBufferSnapshot Calculate(
        RollingProgrammingManifest manifest,
        RollingStationRuntimeState? executionState)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.TargetPreparedBlockCount < 1)
        {
            throw new InvalidDataException(
                "Rolling manifest targetPreparedBlockCount must be at least one.");
        }

        IReadOnlyList<RollingCommittedBlock> blocks = manifest.Blocks
            ?? throw new InvalidDataException("Rolling manifest blocks are missing.");
        for (int index = 0; index < blocks.Count; index++)
        {
            if (blocks[index].Sequence != index + 1L)
            {
                throw new InvalidDataException(
                    "Rolling manifest block sequences are not contiguous from sequence one.");
            }
        }

        long expectedNext = checked(blocks.Count + 1L);
        if (manifest.NextSequence != expectedNext)
        {
            throw new InvalidDataException(
                "Rolling manifest nextSequence does not follow its committed prefix.");
        }

        ValidateExecutionIdentity(executionState);
        long nextRequired = checked((executionState?.LastCompletedBlockSequence ?? 0) + 1);
        long anchor = executionState?.ActiveBlockSequence ?? nextRequired;
        long highest = manifest.NextSequence - 1;
        if ((executionState?.LastCompletedBlockSequence ?? 0) > highest)
        {
            throw new InvalidDataException(
                "The last completed rolling block is not present in the committed manifest prefix.");
        }

        if (executionState?.ActiveBlockSequence is not null && anchor > highest)
        {
            throw new InvalidDataException(
                "The active rolling block is not present in the committed manifest prefix.");
        }

        int futureTarget = manifest.TargetPreparedBlockCount - 1;
        long requiredHighest = checked(anchor + futureTarget);
        long deficit = Math.Max(0, requiredHighest - highest);
        long committedFuture = Math.Max(0, highest - anchor);
        RollingBufferHealth health = highest < anchor || committedFuture == 0 && futureTarget > 0
            ? RollingBufferHealth.Empty
            : committedFuture >= futureTarget
                ? RollingBufferHealth.Healthy
                : RollingBufferHealth.Low;
        return new RollingBufferSnapshot(
            nextRequired,
            anchor,
            highest,
            futureTarget,
            committedFuture,
            requiredHighest,
            deficit,
            health);
    }

    private static void ValidateExecutionIdentity(RollingStationRuntimeState? state)
    {
        if (state is null)
        {
            return;
        }

        bool hasAnyActive = state.ActiveBlockSequence is not null
            || state.ActiveBlockId is not null
            || state.ActiveQueueId is not null
            || state.ClaimedAtUtc is not null;
        bool hasCompleteActive = state.ActiveBlockSequence is > 0
            && IsSha256(state.ActiveBlockId)
            && IsSha256(state.ActiveQueueId)
            && state.ClaimedAtUtc is not null;
        if (hasAnyActive != hasCompleteActive)
        {
            throw new InvalidDataException(
                "Rolling execution state contains a partial active-block identity.");
        }

        bool requiresActive = state.Phase is
            RollingStationPhase.Claimed or
            RollingStationPhase.Executing or
            RollingStationPhase.Stopped;
        bool forbidsActive = state.Phase is
            RollingStationPhase.Advancing or
            RollingStationPhase.WaitingForBlock;
        if ((requiresActive && !hasCompleteActive) || (forbidsActive && hasAnyActive))
        {
            throw new InvalidDataException(
                "Rolling execution phase contradicts its active-block identity.");
        }

        if (hasCompleteActive
            && state.ActiveBlockSequence != checked((state.LastCompletedBlockSequence ?? 0) + 1))
        {
            throw new InvalidDataException(
                "Rolling active sequence does not follow the last completed sequence.");
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
