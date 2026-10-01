using NzyteTv.Cli;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Core.Tests;

public sealed class RollingStationFormatterTests
{
    private const string Secret = "rtmps://example.invalid/live2/FAKE-SECRET";

    [Theory]
    [InlineData(BroadcastDestinationStatus.NotConfigured, "NOT CONFIGURED")]
    [InlineData(BroadcastDestinationStatus.Valid, "CONFIGURED / VALID")]
    [InlineData(BroadcastDestinationStatus.Invalid, "CONFIGURED / INVALID")]
    public void Validation_ReportsClassificationButNeverDestination(
        BroadcastDestinationStatus destinationStatus,
        string expected)
    {
        RollingStationValidationResult validation = CreateValidation(destinationStatus);

        string output = RollingStationFormatters.FormatValidation(validation);

        Assert.Contains($"Destination env:     {expected}", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
        Assert.DoesNotContain("stream key", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Automatic block replenishment: ACTIVE DURING ROLLING RUN",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Status_CombinesRollingAndCp2StateWithoutPrintingCompleteIdsOrSecrets()
    {
        RollingStationValidationResult validation = CreateValidation(
            BroadcastDestination.GetStatus(Secret));
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        var stationStatus = new StationStatusSnapshot(
            validation.StationState!,
            StationStatusKind.Stopped,
            false,
            false,
            true,
            true,
            now);

        string output = RollingStationFormatters.FormatStatus(new RollingStationStatusSnapshot(
            validation,
            NextRequiredSequence: 2,
            NextBlockCommitted: true,
            stationStatus,
            now));

        Assert.Contains("Coordinator:         STOPPED", output, StringComparison.Ordinal);
        Assert.Contains("Active block:        2", output, StringComparison.Ordinal);
        Assert.Contains("Last completed:      1", output, StringComparison.Ordinal);
        Assert.Contains("Highest committed:   4", output, StringComparison.Ordinal);
        Assert.Contains("Committed future:    2", output, StringComparison.Ordinal);
        Assert.Contains("Future target:       2", output, StringComparison.Ordinal);
        Assert.Contains("Required through:    4", output, StringComparison.Ordinal);
        Assert.Contains("Buffer deficit:      0", output, StringComparison.Ordinal);
        Assert.Contains("Automatic replenish: HEALTHY", output, StringComparison.Ordinal);
        Assert.Contains("Destination env:     CONFIGURED / VALID", output, StringComparison.Ordinal);
        Assert.Contains("YouTube monitoring:  NOT CONFIGURED", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('b', 64), output, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('c', 64), output, StringComparison.Ordinal);
    }

    private static RollingStationValidationResult CreateValidation(
        BroadcastDestinationStatus destinationStatus)
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        var rollingConfiguration = new RollingStationConfiguration
        {
            StationConfigPath = "/etc/nzyte-tv/station.json",
            PlannerId = "00112233445566778899aabbccddeeff",
            RollingStatePath = "/var/lib/nzyte-tv/rolling-state.json",
        };
        var stationConfiguration = new StationConfiguration
        {
            MediaRoot = "/srv/nzyte-tv/media",
            LibraryRoot = "/srv/nzyte-tv/library",
            StatePath = "/var/lib/nzyte-tv/state.json",
            Playlists = ["/srv/nzyte-tv/media/playlists/static.json"],
        };
        var rollingState = new RollingStationRuntimeState
        {
            PlannerId = rollingConfiguration.PlannerId,
            Phase = RollingStationPhase.Stopped,
            ActiveBlockSequence = 2,
            ActiveBlockId = new string('b', 64),
            ActiveQueueId = new string('c', 64),
            LastCompletedBlockSequence = 1,
            LastCompletedBlockId = new string('a', 64),
            ClaimedAtUtc = now,
            LastCompletedAtUtc = now - TimeSpan.FromHours(1),
            UpdatedAtUtc = now,
            LastTransitionError = $"safe redaction test {Secret}",
        };
        var stationState = new StationRuntimeState
        {
            StationState = StationState.Stopped,
            BroadcastState = StationBroadcastState.Stopped,
            StationPid = 41,
            StartedAtUtc = now - TimeSpan.FromHours(1),
            LastHeartbeatUtc = now,
            MediaRoot = stationConfiguration.MediaRoot,
            LibraryRoot = stationConfiguration.LibraryRoot,
            TotalPlaylistCount = 1,
            QueuedPlaylistCount = 1,
            QueueId = rollingState.ActiveQueueId,
            QueueItemCount = 3,
            CurrentGlobalIndex = 0,
            ResumeGlobalIndex = 0,
            LastStartMode = StationStartMode.Resume,
            ResumeCount = 1,
            LastResumeAtUtc = now,
        };
        return new RollingStationValidationResult(
            rollingConfiguration,
            stationConfiguration,
            Manifest: null,
            rollingState,
            stationState,
            FfmpegAvailable: true,
            destinationStatus,
            Errors: [],
            Warnings: [])
        {
            Buffer = new RollingBufferSnapshot(
                NextRequiredSequence: 2,
                AnchorSequence: 2,
                HighestCommittedSequence: 4,
                FutureBlockTarget: 2,
                CommittedFutureBlockCount: 2,
                RequiredHighestSequence: 4,
                BufferDeficit: 0,
                RollingBufferHealth.Healthy),
            ReplenishmentState = new RollingReplenishmentState
            {
                PlannerId = rollingConfiguration.PlannerId,
                Health = RollingReplenishmentHealth.Healthy,
                AnchorSequence = 2,
                HighestCommittedSequence = 4,
                RequiredHighestSequence = 4,
                FutureBlockTarget = 2,
                BufferDeficit = 0,
                LastSuccessAtUtc = now,
                ErrorClassification = RollingReplenishmentErrorClassification.None,
                UpdatedAtUtc = now,
            },
        };
    }
}
