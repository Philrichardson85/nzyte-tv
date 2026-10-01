using System.Text.Json.Serialization;

namespace NzyteTv.Core;

public sealed record RollingStationConfiguration
{
    public const int CurrentSchemaVersion = 1;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonRequired]
    public string StationConfigPath { get; init; } = string.Empty;

    [JsonRequired]
    public string PlannerId { get; init; } = string.Empty;

    [JsonRequired]
    public string RollingStatePath { get; init; } = string.Empty;
}

public enum RollingStationPhase
{
    Claimed,
    Executing,
    Stopped,
    Advancing,
    WaitingForBlock,
    Failed,
}

public enum RollingStationFailureDisposition
{
    Restartable,
    Permanent,
}

public sealed record RollingStationRuntimeState
{
    public const int CurrentSchemaVersion = 1;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonRequired]
    public string PlannerId { get; init; } = string.Empty;

    [JsonRequired]
    public RollingStationPhase Phase { get; init; }

    public long? ActiveBlockSequence { get; init; }

    public string? ActiveBlockId { get; init; }

    public string? ActiveQueueId { get; init; }

    public long? LastCompletedBlockSequence { get; init; }

    public string? LastCompletedBlockId { get; init; }

    public DateTimeOffset? ClaimedAtUtc { get; init; }

    public DateTimeOffset? LastCompletedAtUtc { get; init; }

    [JsonRequired]
    public DateTimeOffset UpdatedAtUtc { get; init; }

    public RollingStationFailureDisposition? FailureDisposition { get; init; }

    public string? LastTransitionError { get; init; }

    public string? InitialCutoverSourceQueueId { get; init; }

    public DateTimeOffset? InitialCutoverAcceptedAtUtc { get; init; }
}

public static class RollingStationRuntimePolicy
{
    public static readonly IReadOnlyList<TimeSpan> MissingBlockPollIntervals =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];
}

public static class RollingStationStateMachine
{
    public static bool CanTransition(
        RollingStationPhase? current,
        RollingStationPhase next) => current switch
        {
            null => next is RollingStationPhase.Claimed or RollingStationPhase.WaitingForBlock,
            RollingStationPhase.Claimed => next is
                RollingStationPhase.Executing or
                RollingStationPhase.Stopped or
                RollingStationPhase.Advancing or
                RollingStationPhase.Failed,
            RollingStationPhase.Executing => next is
                RollingStationPhase.Executing or
                RollingStationPhase.Stopped or
                RollingStationPhase.Advancing or
                RollingStationPhase.Failed,
            RollingStationPhase.Stopped => next is
                RollingStationPhase.Executing or
                RollingStationPhase.Advancing or
                RollingStationPhase.Failed,
            RollingStationPhase.Advancing => next is
                RollingStationPhase.Claimed or
                RollingStationPhase.WaitingForBlock or
                RollingStationPhase.Failed,
            RollingStationPhase.WaitingForBlock => next is
                RollingStationPhase.WaitingForBlock or
                RollingStationPhase.Claimed or
                RollingStationPhase.Failed,
            RollingStationPhase.Failed => next is
                RollingStationPhase.Executing or
                RollingStationPhase.Advancing or
                RollingStationPhase.WaitingForBlock or
                RollingStationPhase.Claimed or
                RollingStationPhase.Failed,
            _ => false,
        };
}
