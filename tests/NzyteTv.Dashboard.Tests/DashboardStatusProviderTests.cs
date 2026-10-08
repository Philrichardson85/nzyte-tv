using NzyteTv.Core;
using NzyteTv.Dashboard.Status;

namespace NzyteTv.Dashboard.Tests;

public sealed class DashboardStatusProviderTests
{
    [Fact]
    public async Task FreshStation_ProjectsSafeCurrentStatusAndFutureBuffer()
    {
        using var fixture = new DashboardStateFixture();
        await fixture.WriteAllAsync();

        DashboardStatusSnapshot snapshot = fixture.CreateProvider(101, 202).ReadStatus();

        Assert.Equal(DashboardSnapshotQuality.Healthy, snapshot.Quality);
        Assert.Equal(DashboardStationStatus.Running, snapshot.Station.Status);
        Assert.Equal(DashboardProcessEvidence.Running, snapshot.Station.ProcessEvidence);
        Assert.Equal(5, snapshot.Station.HeartbeatAgeSeconds);
        Assert.Equal(3600, snapshot.Station.SessionUptimeSeconds);
        Assert.Equal(DashboardFfmpegState.Running, snapshot.Broadcast.FfmpegState);
        Assert.Equal("Safe title", snapshot.Playback.Title);
        Assert.Equal(2, snapshot.Playback.CurrentItemNumber);
        Assert.Equal(4, snapshot.Playback.TotalItemCount);
        Assert.Equal(3, snapshot.Rolling.ActiveBlockSequence);
        Assert.Equal(2, snapshot.Rolling.CommittedFutureBlockCount);
        Assert.Equal(DashboardBufferHealth.Healthy, snapshot.Rolling.BufferHealth);
        Assert.Empty(snapshot.Issues);
    }

    [Fact]
    public async Task StaleStation_DoesNotClaimLiveProcessFfmpegOrPlayback()
    {
        using var fixture = new DashboardStateFixture();
        await fixture.WriteAllAsync(
            station: fixture.CreateStation() with
            {
                LastHeartbeatUtc = DashboardStateFixture.Now
                    - StationRuntimePolicy.StaleHeartbeatThreshold
                    - TimeSpan.FromSeconds(1),
            });

        DashboardStatusSnapshot snapshot = fixture.CreateProvider(101, 202).ReadStatus();

        Assert.Equal(DashboardStationStatus.Stale, snapshot.Station.Status);
        Assert.Equal(DashboardProcessEvidence.Unknown, snapshot.Station.ProcessEvidence);
        Assert.Equal(DashboardBroadcastState.Unknown, snapshot.Broadcast.State);
        Assert.Equal(DashboardFfmpegState.Unknown, snapshot.Broadcast.FfmpegState);
        Assert.Equal(DashboardPlaybackAvailability.NotPlaying, snapshot.Playback.Availability);
        Assert.Contains(DashboardIssueCode.StationHeartbeatStale, snapshot.Issues);
    }

    [Theory]
    [InlineData(StationState.Stopped, StationBroadcastState.Stopped, DashboardStationStatus.Stopped)]
    [InlineData(StationState.Failed, StationBroadcastState.Failed, DashboardStationStatus.Failed)]
    public async Task TerminalStation_ReportsTerminalStateWithoutProcessClaims(
        StationState stationState,
        StationBroadcastState broadcastState,
        DashboardStationStatus expected)
    {
        using var fixture = new DashboardStateFixture();
        await fixture.WriteAllAsync(
            station: fixture.CreateStation() with
            {
                StationState = stationState,
                BroadcastState = broadcastState,
                FfmpegPid = null,
            });

        DashboardStatusSnapshot snapshot = fixture.CreateProvider().ReadStatus();

        Assert.Equal(expected, snapshot.Station.Status);
        Assert.Equal(DashboardProcessEvidence.NotRunning, snapshot.Station.ProcessEvidence);
        Assert.Equal(DashboardFfmpegState.NotRunning, snapshot.Broadcast.FfmpegState);
        Assert.Equal(DashboardPlaybackAvailability.NotPlaying, snapshot.Playback.Availability);
    }

    [Fact]
    public async Task RecoveringStation_PreservesAcceptedLocalRecoveryState()
    {
        using var fixture = new DashboardStateFixture();
        await fixture.WriteAllAsync(
            station: fixture.CreateStation() with
            {
                BroadcastState = StationBroadcastState.Recovering,
                RecoveryAttempts = 3,
                FfmpegPid = null,
            });

        DashboardStatusSnapshot snapshot = fixture.CreateProvider(101).ReadStatus();

        Assert.Equal(DashboardBroadcastState.Recovering, snapshot.Broadcast.State);
        Assert.Equal(3, snapshot.Broadcast.RecoveryAttempts);
        Assert.Equal(DashboardFfmpegState.NotRunning, snapshot.Broadcast.FfmpegState);
    }

    [Fact]
    public void MissingState_IsUnavailableAndNeverMeansStopped()
    {
        using var fixture = new DashboardStateFixture();

        DashboardStatusSnapshot snapshot = fixture.CreateProvider().ReadStatus();

        Assert.Equal(DashboardSnapshotQuality.Unavailable, snapshot.Quality);
        Assert.Equal(DashboardAvailability.Unavailable, snapshot.Station.Availability);
        Assert.Equal(DashboardStationStatus.Unknown, snapshot.Station.Status);
        Assert.Equal(DashboardFfmpegState.Unknown, snapshot.Broadcast.FfmpegState);
        Assert.Contains(DashboardIssueCode.StationStateMissing, snapshot.Issues);
        Assert.Contains(DashboardIssueCode.RollingStateMissing, snapshot.Issues);
        Assert.Contains(DashboardIssueCode.ReplenishmentStateMissing, snapshot.Issues);
    }

    [Fact]
    public async Task MalformedStationState_IsSafeAndDoesNotExposeItsContent()
    {
        using var fixture = new DashboardStateFixture();
        await fixture.RollingStore.WriteAsync(
            fixture.Options.RollingStatePath,
            fixture.CreateRolling(),
            CancellationToken.None);
        await fixture.ReplenishmentStore.WriteAsync(
            fixture.Options.ReplenishmentStatePath,
            fixture.CreateReplenishment(),
            CancellationToken.None);
        await File.WriteAllTextAsync(
            fixture.Options.StationStatePath,
            "{ invalid rtmps://example.invalid/live/SECRET /var/private }");

        DashboardStatusSnapshot snapshot = fixture.CreateProvider().ReadStatus();
        string serialized = System.Text.Json.JsonSerializer.Serialize(snapshot);

        Assert.Equal(DashboardAvailability.Unavailable, snapshot.Station.Availability);
        Assert.Contains(DashboardIssueCode.StationStateInvalid, snapshot.Issues);
        Assert.DoesNotContain("SECRET", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("/var/private", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingStationProcess_MakesActiveStateStale()
    {
        using var fixture = new DashboardStateFixture();
        await fixture.WriteAllAsync();

        DashboardStatusSnapshot snapshot = fixture.CreateProvider(202).ReadStatus();

        Assert.Equal(DashboardStationStatus.Stale, snapshot.Station.Status);
        Assert.Equal(DashboardProcessEvidence.Unknown, snapshot.Station.ProcessEvidence);
        Assert.Equal(DashboardBroadcastState.Unknown, snapshot.Broadcast.State);
        Assert.Contains(DashboardIssueCode.StationProcessMissing, snapshot.Issues);
    }

    [Fact]
    public async Task MissingRollingOrReplenishmentState_ReportsUnknownInsteadOfZero()
    {
        using var rollingMissing = new DashboardStateFixture();
        await rollingMissing.StationStore.WriteAsync(
            rollingMissing.Options.StationStatePath,
            rollingMissing.CreateStation(),
            CancellationToken.None);
        await rollingMissing.ReplenishmentStore.WriteAsync(
            rollingMissing.Options.ReplenishmentStatePath,
            rollingMissing.CreateReplenishment(),
            CancellationToken.None);

        DashboardStatusSnapshot noRolling = rollingMissing.CreateProvider(101, 202).ReadStatus();
        Assert.Equal(DashboardAvailability.Unavailable, noRolling.Rolling.Availability);
        Assert.Null(noRolling.Rolling.CommittedFutureBlockCount);

        using var replenishmentMissing = new DashboardStateFixture();
        await replenishmentMissing.StationStore.WriteAsync(
            replenishmentMissing.Options.StationStatePath,
            replenishmentMissing.CreateStation(),
            CancellationToken.None);
        await replenishmentMissing.RollingStore.WriteAsync(
            replenishmentMissing.Options.RollingStatePath,
            replenishmentMissing.CreateRolling(),
            CancellationToken.None);

        DashboardStatusSnapshot noReplenishment =
            replenishmentMissing.CreateProvider(101, 202).ReadStatus();
        Assert.Null(noReplenishment.Rolling.CommittedFutureBlockCount);
        Assert.Equal(DashboardBufferHealth.Unknown, noReplenishment.Rolling.BufferHealth);
        Assert.Contains(
            DashboardIssueCode.ReplenishmentStateMissing,
            noReplenishment.Issues);
    }

    [Fact]
    public async Task ReplenishmentMismatchOrStaleAnchor_HidesAdvisoryCounts()
    {
        using var lineageMismatch = new DashboardStateFixture();
        await lineageMismatch.WriteAllAsync(
            replenishment: lineageMismatch.CreateReplenishment() with
            {
                PlannerId = "ffeeddccbbaa99887766554433221100",
            });

        DashboardStatusSnapshot mismatched =
            lineageMismatch.CreateProvider(101, 202).ReadStatus();
        Assert.Null(mismatched.Rolling.CommittedFutureBlockCount);
        Assert.Contains(DashboardIssueCode.ReplenishmentLineageMismatch, mismatched.Issues);

        using var staleAnchor = new DashboardStateFixture();
        await staleAnchor.WriteAllAsync(
            replenishment: staleAnchor.CreateReplenishment() with
            {
                AnchorSequence = 2,
                RequiredHighestSequence = 4,
                BufferDeficit = 0,
            });

        DashboardStatusSnapshot stale = staleAnchor.CreateProvider(101, 202).ReadStatus();
        Assert.Null(stale.Rolling.CommittedFutureBlockCount);
        Assert.Contains(DashboardIssueCode.ReplenishmentStateInconsistent, stale.Issues);
    }

    [Fact]
    public async Task ContradictoryQueueIdentity_HidesPlayback()
    {
        using var fixture = new DashboardStateFixture();
        await fixture.WriteAllAsync(
            station: fixture.CreateStation() with { QueueId = new string('d', 64) });

        DashboardStatusSnapshot snapshot = fixture.CreateProvider(101, 202).ReadStatus();

        Assert.Equal(DashboardPlaybackAvailability.Unavailable, snapshot.Playback.Availability);
        Assert.Null(snapshot.Playback.Title);
        Assert.Contains(DashboardIssueCode.RuntimeStateInconsistent, snapshot.Issues);
    }

    [Fact]
    public async Task FutureHeartbeat_IsNotUsedForFreshnessOrUptime()
    {
        using var fixture = new DashboardStateFixture();
        await fixture.WriteAllAsync(
            station: fixture.CreateStation() with
            {
                LastHeartbeatUtc = DashboardStateFixture.Now.AddSeconds(1),
            });

        DashboardStatusSnapshot snapshot = fixture.CreateProvider(101, 202).ReadStatus();

        Assert.Equal(DashboardStationStatus.Stale, snapshot.Station.Status);
        Assert.Null(snapshot.Station.HeartbeatAgeSeconds);
        Assert.Contains(DashboardIssueCode.StationClockInvalid, snapshot.Issues);
    }

    [Fact]
    public async Task FutureBuffer_CalculatesLowHealthWithoutReadingManifest()
    {
        using var fixture = new DashboardStateFixture();
        await fixture.WriteAllAsync(
            replenishment: fixture.CreateReplenishment() with
            {
                HighestCommittedSequence = 4,
                BufferDeficit = 1,
                Health = RollingReplenishmentHealth.Degraded,
            });

        DashboardStatusSnapshot snapshot = fixture.CreateProvider(101, 202).ReadStatus();

        Assert.Equal(1, snapshot.Rolling.CommittedFutureBlockCount);
        Assert.Equal(1, snapshot.Rolling.BufferDeficit);
        Assert.Equal(DashboardBufferHealth.Low, snapshot.Rolling.BufferHealth);
        Assert.Equal(
            DashboardReplenishmentHealth.Degraded,
            snapshot.Rolling.ReplenishmentHealth);
    }

    [Fact]
    public void RollingTransitionDuringSampling_HidesInconsistentRollingProjection()
    {
        using var fixture = new DashboardStateFixture();
        RollingStationRuntimeState before = fixture.CreateRolling();
        RollingStationRuntimeState after = before with
        {
            ActiveBlockSequence = 4,
            ActiveBlockId = new string('d', 64),
            ActiveQueueId = new string('e', 64),
            LastCompletedBlockSequence = 3,
            LastCompletedBlockId = before.ActiveBlockId,
            LastCompletedAtUtc = DashboardStateFixture.Now.AddSeconds(-1),
            UpdatedAtUtc = DashboardStateFixture.Now,
        };
        var reader = new TransitioningStateReader(
            fixture.CreateStation(),
            before,
            after,
            fixture.CreateReplenishment());
        var provider = new DashboardStatusProvider(
            reader,
            new FixedProcessExistence([101, 202]),
            new FixedTimeProvider(DashboardStateFixture.Now),
            new DashboardRuntimeInfo("test", DashboardStateFixture.Now.AddHours(-1)));

        DashboardStatusSnapshot snapshot = provider.ReadStatus();

        Assert.Equal(2, reader.RollingReadCount);
        Assert.Equal(DashboardSnapshotQuality.Degraded, snapshot.Quality);
        Assert.Equal(DashboardAvailability.Unavailable, snapshot.Rolling.Availability);
        Assert.Null(snapshot.Rolling.ActiveBlockSequence);
        Assert.Null(snapshot.Rolling.CommittedFutureBlockCount);
        Assert.Equal(DashboardBufferHealth.Unknown, snapshot.Rolling.BufferHealth);
        Assert.Contains(DashboardIssueCode.RuntimeStateInconsistent, snapshot.Issues);
    }

    private sealed class TransitioningStateReader(
        StationRuntimeState station,
        RollingStationRuntimeState rollingBefore,
        RollingStationRuntimeState rollingAfter,
        RollingReplenishmentState replenishment) : IDashboardStateReader
    {
        public int RollingReadCount { get; private set; }

        public StationRuntimeState? ReadStationState() => station;

        public RollingStationRuntimeState? ReadRollingState() =>
            RollingReadCount++ == 0 ? rollingBefore : rollingAfter;

        public RollingReplenishmentState? ReadReplenishmentState() => replenishment;
    }
}
