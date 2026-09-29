using NzyteTv.Cli;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Core.Tests;

public sealed class StationFormatterTests
{
    private const string Secret = "rtmps://example.invalid/live2/SECRET-KEY";

    [Theory]
    [InlineData(BroadcastDestinationStatus.NotConfigured, "Destination env:     NOT CONFIGURED", "    READY")]
    [InlineData(BroadcastDestinationStatus.Valid, "Destination env:     CONFIGURED / VALID", "    READY")]
    [InlineData(BroadcastDestinationStatus.Invalid, "Destination env:     CONFIGURED / INVALID", "    NOT READY")]
    public void Validation_ReportsDestinationStatusWithoutValue(
        BroadcastDestinationStatus destinationStatus,
        string expectedDestination,
        string expectedOverallStatus)
    {
        var configuration = new StationConfiguration
        {
            SchemaVersion = 1,
            MediaRoot = "/media",
            LibraryRoot = "/library",
            StatePath = "/state.json",
            Playlists = ["/playlist.json"],
        };
        var plan = new BroadcastPlan(
            configuration.LibraryRoot,
            configuration.Playlists,
            [new BroadcastPlanItem("/playlist.json", 1, "asset", "asset.mp4", "/library/asset.mp4", 60)],
            [],
            1,
            60);

        string output = StationFormatters.FormatValidation(new StationValidationResult(
            configuration,
            plan,
            FfmpegAvailable: true,
            DestinationStatus: destinationStatus));

        Assert.Contains(expectedDestination, output, StringComparison.Ordinal);
        Assert.Contains(expectedOverallStatus, output, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_ConfiguredDestinationValueIsNotAvailableToFormatterOutput()
    {
        var configuration = new StationConfiguration
        {
            SchemaVersion = 1,
            MediaRoot = "/media",
            LibraryRoot = "/library",
            StatePath = "/state.json",
            Playlists = ["/playlist.json"],
        };
        var plan = new BroadcastPlan(
            configuration.LibraryRoot,
            configuration.Playlists,
            [new BroadcastPlanItem("/playlist.json", 1, "asset", "asset.mp4", "/library/asset.mp4", 60)],
            [],
            1,
            60);
        BroadcastDestinationStatus destinationStatus = BroadcastDestination.GetStatus(Secret);

        string output = StationFormatters.FormatValidation(new StationValidationResult(
            configuration,
            plan,
            FfmpegAvailable: true,
            DestinationStatus: destinationStatus));

        Assert.Contains("Destination env:     CONFIGURED / VALID", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_ReportsRuntimeDetailsAndNeverContainsDestination()
    {
        DateTimeOffset now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var state = new StationRuntimeState
        {
            StationState = StationState.Broadcasting,
            BroadcastState = StationBroadcastState.Broadcasting,
            StationPid = 100,
            StartedAtUtc = now - TimeSpan.FromMinutes(2),
            LastHeartbeatUtc = now,
            MediaRoot = "/media",
            LibraryRoot = "/library",
            CurrentPlaylist = "/playlists/production-01.json",
            CurrentPlaylistIndex = 1,
            CurrentSequence = 137,
            CurrentPlaylistItemCount = 325,
            AssetId = "asset-137",
            Title = "Pray",
            Type = "animated-visual",
            FfmpegPid = 4219,
            RecoveryAttempts = 1,
            TotalPlaylistCount = 2,
            QueuedPlaylistCount = 1,
            LastError = $"output failed for {Secret}",
        };
        var snapshot = new StationStatusSnapshot(
            state,
            StationStatusKind.Running,
            StationProcessExists: true,
            FfmpegProcessExists: true,
            MediaRootAvailable: true,
            LibraryRootAvailable: true,
            now);

        string output = StationFormatters.FormatStatus(snapshot);

        Assert.Contains("Station:              RUNNING", output, StringComparison.Ordinal);
        Assert.Contains("production-01.json", output, StringComparison.Ordinal);
        Assert.Contains("137 / 325", output, StringComparison.Ordinal);
        Assert.Contains("Pray", output, StringComparison.Ordinal);
        Assert.Contains("PID:                4219", output, StringComparison.Ordinal);
        Assert.Contains("YouTube monitoring:   NOT CONFIGURED", output, StringComparison.Ordinal);
        Assert.Contains("Last error:           output failed for [REDACTED]", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_StaleStateIsProminent()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var state = new StationRuntimeState
        {
            StationState = StationState.Broadcasting,
            BroadcastState = StationBroadcastState.Broadcasting,
            StationPid = 100,
            StartedAtUtc = now,
            LastHeartbeatUtc = now,
            MediaRoot = "/media",
            LibraryRoot = "/library",
            TotalPlaylistCount = 1,
            QueuedPlaylistCount = 1,
        };

        string output = StationFormatters.FormatStatus(new StationStatusSnapshot(
            state,
            StationStatusKind.Stale,
            false,
            false,
            false,
            false,
            now));

        Assert.Contains("Station:              STALE", output, StringComparison.Ordinal);
        Assert.Contains("Warning:              STALE", output, StringComparison.Ordinal);
    }
}
