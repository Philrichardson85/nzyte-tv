using NzyteTv.Core;
using NzyteTv.Dashboard.Configuration;
using NzyteTv.Dashboard.Status;
using NzyteTv.Media;

namespace NzyteTv.Dashboard.Tests;

internal sealed class DashboardStateFixture : IDisposable
{
    public static readonly DateTimeOffset Now =
        new(2026, 10, 7, 16, 0, 0, TimeSpan.Zero);

    public const string PlannerId = "00112233445566778899aabbccddeeff";
    public const string QueueId =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nzytetv-dashboard-tests-{Guid.NewGuid():N}");

    public DashboardStateFixture()
    {
        Directory.CreateDirectory(_root);
        Options = new DashboardOptions
        {
            StationStatePath = Path.Combine(_root, "state.json"),
            RollingStatePath = Path.Combine(_root, "rolling-state.json"),
            ReplenishmentStatePath = Path.Combine(
                _root,
                "rolling-state.json.replenishment.json"),
        };
    }

    public DashboardOptions Options { get; }

    public StationStateStore StationStore { get; } = new();

    public RollingStationStateStore RollingStore { get; } = new();

    public RollingReplenishmentStateStore ReplenishmentStore { get; } = new();

    public StationRuntimeState CreateStation() => new()
    {
        StationState = StationState.Broadcasting,
        StationPid = 101,
        StartedAtUtc = Now.AddHours(-1),
        LastHeartbeatUtc = Now.AddSeconds(-5),
        MediaRoot = "/safe/media",
        LibraryRoot = "/safe/library",
        CurrentPlaylist = "/safe/playlists/block.json",
        CurrentPlaylistIndex = 1,
        CurrentSequence = 2,
        CurrentPlaylistItemCount = 4,
        AssetId = "asset-safe",
        Title = "Safe title",
        Type = "music-video",
        FfmpegPid = 202,
        BroadcastState = StationBroadcastState.Broadcasting,
        RecoveryAttempts = 0,
        QueuedPlaylistCount = 0,
        TotalPlaylistCount = 1,
        QueueId = QueueId,
        QueueItemCount = 4,
        CurrentGlobalIndex = 1,
        LastCompletedGlobalIndex = 0,
        ResumeGlobalIndex = 1,
        LastStartMode = StationStartMode.Fresh,
    };

    public RollingStationRuntimeState CreateRolling() => new()
    {
        PlannerId = PlannerId,
        Phase = RollingStationPhase.Executing,
        ActiveBlockSequence = 3,
        ActiveBlockId = new string('b', 64),
        ActiveQueueId = QueueId,
        LastCompletedBlockSequence = 2,
        LastCompletedBlockId = new string('c', 64),
        ClaimedAtUtc = Now.AddHours(-1),
        LastCompletedAtUtc = Now.AddHours(-1),
        UpdatedAtUtc = Now.AddMinutes(-1),
    };

    public RollingReplenishmentState CreateReplenishment() => new()
    {
        PlannerId = PlannerId,
        Health = RollingReplenishmentHealth.Healthy,
        AnchorSequence = 3,
        HighestCommittedSequence = 5,
        RequiredHighestSequence = 5,
        FutureBlockTarget = 2,
        BufferDeficit = 0,
        LastAttemptAtUtc = Now.AddMinutes(-10),
        LastSuccessAtUtc = Now.AddMinutes(-10),
        ErrorClassification = RollingReplenishmentErrorClassification.None,
        UpdatedAtUtc = Now.AddMinutes(-10),
    };

    public async Task WriteAllAsync(
        StationRuntimeState? station = null,
        RollingStationRuntimeState? rolling = null,
        RollingReplenishmentState? replenishment = null)
    {
        await StationStore.WriteAsync(
            Options.StationStatePath,
            station ?? CreateStation(),
            CancellationToken.None);
        await RollingStore.WriteAsync(
            Options.RollingStatePath,
            rolling ?? CreateRolling(),
            CancellationToken.None);
        await ReplenishmentStore.WriteAsync(
            Options.ReplenishmentStatePath,
            replenishment ?? CreateReplenishment(),
            CancellationToken.None);
    }

    public DashboardStatusProvider CreateProvider(params int[] liveProcessIds) => new(
        new DashboardStateReader(Options),
        new FixedProcessExistence(liveProcessIds),
        new FixedTimeProvider(Now),
        new DashboardRuntimeInfo("3B3-A-test", Now.AddHours(-2)));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

}

internal sealed class FixedProcessExistence(IEnumerable<int> processIds) : IProcessExistence
{
    private readonly HashSet<int> _processIds = processIds.ToHashSet();

    public bool Exists(int processId) => _processIds.Contains(processId);
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class FixedDashboardStatusProvider(DashboardStatusSnapshot snapshot)
    : IDashboardStatusProvider
{
    public DashboardStatusSnapshot GetStatus() => snapshot;
}

internal static class DashboardSnapshotFactory
{
    public static DashboardStatusSnapshot Create(string? title = "Safe title") => new(
        DashboardStatusSnapshot.CurrentSchemaVersion,
        DashboardStateFixture.Now,
        DashboardSnapshotQuality.Healthy,
        new DashboardInfo("3B3-A-test", 600),
        new DashboardStationInfo(
            DashboardAvailability.Available,
            DashboardStationStatus.Running,
            DashboardProcessEvidence.Running,
            DashboardStateFixture.Now.AddSeconds(-5),
            5,
            DashboardStateFixture.Now.AddHours(-1),
            3600),
        new DashboardBroadcastInfo(
            DashboardBroadcastState.Broadcasting,
            DashboardFfmpegState.Running,
            0),
        new DashboardRollingInfo(
            DashboardAvailability.Available,
            DashboardRollingPhase.Executing,
            3,
            2,
            3,
            2,
            2,
            0,
            DashboardBufferHealth.Healthy,
            DashboardReplenishmentHealth.Healthy),
        new DashboardPlaybackInfo(
            DashboardPlaybackAvailability.Available,
            title,
            "music-video",
            2,
            4),
        []);
}
