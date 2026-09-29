using System.Text.Json.Serialization;

namespace NzyteTv.Core;

public sealed record StationConfiguration
{
    public const int CurrentSchemaVersion = 1;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonRequired]
    public string MediaRoot { get; init; } = string.Empty;

    [JsonRequired]
    public string LibraryRoot { get; init; } = string.Empty;

    [JsonRequired]
    public string StatePath { get; init; } = string.Empty;

    [JsonRequired]
    public IReadOnlyList<string> Playlists { get; init; } = [];
}

public enum StationState
{
    Starting,
    Broadcasting,
    Stopping,
    Stopped,
    Completed,
    Failed,
}

public enum StationBroadcastState
{
    Starting,
    Broadcasting,
    Recovering,
    Stopping,
    Stopped,
    Completed,
    Failed,
}

public sealed record StationRuntimeState
{
    public const int LegacySchemaVersion = 1;

    public const int CurrentSchemaVersion = 2;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonRequired]
    public StationState StationState { get; init; } = StationState.Starting;

    [JsonRequired]
    public int StationPid { get; init; }

    [JsonRequired]
    public DateTimeOffset StartedAtUtc { get; init; }

    [JsonRequired]
    public DateTimeOffset LastHeartbeatUtc { get; init; }

    [JsonRequired]
    public string MediaRoot { get; init; } = string.Empty;

    [JsonRequired]
    public string LibraryRoot { get; init; } = string.Empty;

    public string? CurrentPlaylist { get; init; }

    public int? CurrentPlaylistIndex { get; init; }

    public int? CurrentSequence { get; init; }

    public int? CurrentPlaylistItemCount { get; init; }

    public string? AssetId { get; init; }

    public string? Title { get; init; }

    public string? Type { get; init; }

    public int? FfmpegPid { get; init; }

    [JsonRequired]
    public StationBroadcastState BroadcastState { get; init; } = StationBroadcastState.Starting;

    public int RecoveryAttempts { get; init; }

    [JsonRequired]
    public int QueuedPlaylistCount { get; init; }

    [JsonRequired]
    public int TotalPlaylistCount { get; init; }

    public string? LastError { get; init; }

    public DateTimeOffset? StoppedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public string? QueueId { get; init; }

    public int? QueueItemCount { get; init; }

    public int? CurrentGlobalIndex { get; init; }

    public int? LastCompletedGlobalIndex { get; init; }

    public int? ResumeGlobalIndex { get; init; }

    public StationStartMode? LastStartMode { get; init; }

    public int ResumeCount { get; init; }

    public DateTimeOffset? LastResumeAtUtc { get; init; }
}

public enum StationStartMode
{
    Fresh,
    Resume,
}

public enum StationStatusKind
{
    Starting,
    Running,
    Stopping,
    Stopped,
    Completed,
    Failed,
    Stale,
}

public sealed record StationStatusSnapshot(
    StationRuntimeState State,
    StationStatusKind Status,
    bool StationProcessExists,
    bool FfmpegProcessExists,
    bool MediaRootAvailable,
    bool LibraryRootAvailable,
    DateTimeOffset ObservedAtUtc);

public static class StationRuntimePolicy
{
    public const string DefaultStatePath = "/var/lib/nzyte-tv/state.json";

    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan StaleHeartbeatThreshold = TimeSpan.FromSeconds(30);
}

public static class StationExitCodes
{
    public const int PermanentStartupFailure = 78;
}

public enum BroadcastRuntimeEventKind
{
    BroadcastStarted,
    ItemChanged,
    ItemCompleted,
    FfmpegProcessStarted,
    FfmpegProcessStopped,
    RecoveryStarted,
    RecoveryBudgetReset,
    BroadcastCompleted,
    BroadcastFailed,
}

public sealed record BroadcastRuntimeEvent(
    BroadcastRuntimeEventKind Kind,
    BroadcastPlanItem? Item = null,
    int? FfmpegPid = null,
    int RecoveryAttempts = 0,
    int? GlobalItemIndex = null);

public interface IBroadcastRuntimeObserver
{
    void OnEvent(BroadcastRuntimeEvent runtimeEvent);
}
