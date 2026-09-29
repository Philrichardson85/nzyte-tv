using System.Globalization;
using System.Text;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Cli;

public static class StationFormatters
{
    public static string FormatValidation(StationValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var output = new StringBuilder();
        output.AppendLine("NZYTE TV Station Validation");
        output.AppendLine();
        output.AppendLine("Config:              VALID");
        output.AppendLine("Media root:          AVAILABLE");
        output.AppendLine("Library root:        AVAILABLE");
        output.AppendLine($"Playlists:           {result.Configuration.Playlists.Count}");
        output.AppendLine($"Playlist media:      {(result.BroadcastPlan.IsReady ? "READY" : "NOT READY")}");
        output.AppendLine($"FFmpeg:              {(result.FfmpegAvailable ? "AVAILABLE" : "NOT AVAILABLE")}");
        output.AppendLine($"Destination env:     {FormatDestinationStatus(result.DestinationStatus)}");
        output.AppendLine();
        output.AppendLine("Status:");
        output.AppendLine($"    {(result.IsReady ? "READY" : "NOT READY")}");
        foreach (BroadcastPlanIssue issue in result.BroadcastPlan.Issues)
        {
            output.AppendLine(
                $"    - playlist {Path.GetFileName(issue.PlaylistPath)}, sequence {issue.Sequence}: {issue.Detail}");
        }

        return output.ToString();
    }

    public static string FormatStatus(StationStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        StationRuntimeState state = snapshot.State;
        var output = new StringBuilder();
        output.AppendLine("NZYTE TV Station");
        output.AppendLine();
        output.AppendLine($"Station:              {snapshot.Status.ToString().ToUpperInvariant()}");
        output.AppendLine($"Started:              {FormatTimestamp(state.StartedAtUtc)}");
        output.AppendLine($"Last heartbeat:       {FormatTimestamp(state.LastHeartbeatUtc)}");
        if (snapshot.Status == StationStatusKind.Stale)
        {
            output.AppendLine("Warning:              STALE state; heartbeat or station PID is not current");
        }

        if (state.StoppedAtUtc is DateTimeOffset stoppedAt)
        {
            output.AppendLine($"Stopped:              {FormatTimestamp(stoppedAt)}");
        }

        if (state.CompletedAtUtc is DateTimeOffset completedAt)
        {
            output.AppendLine($"Completed:            {FormatTimestamp(completedAt)}");
        }

        output.AppendLine();
        output.AppendLine("Broadcast:");
        output.AppendLine($"  State:              {state.BroadcastState.ToString().ToUpperInvariant()}");
        if (!string.IsNullOrWhiteSpace(state.CurrentPlaylist))
        {
            output.AppendLine($"  Playlist:           {Path.GetFileName(state.CurrentPlaylist)}");
        }

        if (state.CurrentSequence is int sequence)
        {
            string sequenceText = state.CurrentPlaylistItemCount is int itemCount
                ? $"{sequence} / {itemCount}"
                : sequence.ToString(CultureInfo.InvariantCulture);
            output.AppendLine($"  Sequence:           {sequenceText}");
        }

        if (!string.IsNullOrWhiteSpace(state.AssetId))
        {
            output.AppendLine($"  Asset ID:           {state.AssetId}");
        }

        if (!string.IsNullOrWhiteSpace(state.Title))
        {
            output.AppendLine($"  Current title:      {state.Title}");
        }

        if (!string.IsNullOrWhiteSpace(state.Type))
        {
            output.AppendLine($"  Type:               {state.Type}");
        }

        output.AppendLine();
        output.AppendLine("FFmpeg:");
        output.AppendLine($"  State:              {(snapshot.FfmpegProcessExists ? "RUNNING" : "NOT RUNNING")}");
        if (state.FfmpegPid is int ffmpegPid)
        {
            output.AppendLine($"  PID:                {ffmpegPid}");
        }

        output.AppendLine($"  Recovery attempts:  {state.RecoveryAttempts}");
        output.AppendLine();
        output.AppendLine("Media:");
        output.AppendLine($"  Root:               {(snapshot.MediaRootAvailable ? "AVAILABLE" : "NOT AVAILABLE")}");
        output.AppendLine($"  Library:            {(snapshot.LibraryRootAvailable ? "AVAILABLE" : "NOT AVAILABLE")}");
        output.AppendLine();
        output.AppendLine("Queue:");
        output.AppendLine($"  Playlists:          {state.TotalPlaylistCount}");
        output.AppendLine($"  Queued after current: {state.QueuedPlaylistCount}");
        output.AppendLine();
        output.AppendLine("Persistence:");
        output.AppendLine($"  State schema:       {state.SchemaVersion}");
        if (state.SchemaVersion == StationRuntimeState.CurrentSchemaVersion)
        {
            output.AppendLine($"  Start mode:         {state.LastStartMode?.ToString().ToUpperInvariant() ?? "UNKNOWN"}");
            output.AppendLine($"  Resume count:       {state.ResumeCount}");
            if (state.LastResumeAtUtc is DateTimeOffset lastResumeAt)
            {
                output.AppendLine($"  Last resume:        {FormatTimestamp(lastResumeAt)}");
            }

            output.AppendLine($"  Last completed:     {FormatQueuePosition(state, state.LastCompletedGlobalIndex)}");
            output.AppendLine($"  Resume position:    {FormatQueuePosition(state, state.ResumeGlobalIndex)}");
        }
        else
        {
            output.AppendLine("  Durable resume:     NOT AVAILABLE (schema 1)");
        }

        output.AppendLine();
        output.AppendLine("YouTube monitoring:   NOT CONFIGURED");
        if (!string.IsNullOrWhiteSpace(state.LastError))
        {
            output.AppendLine($"Last error:           {StationSecretRedactor.RedactRtmpUrls(state.LastError)}");
        }

        return output.ToString();
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string FormatDestinationStatus(BroadcastDestinationStatus status) => status switch
    {
        BroadcastDestinationStatus.NotConfigured => "NOT CONFIGURED",
        BroadcastDestinationStatus.Valid => "CONFIGURED / VALID",
        BroadcastDestinationStatus.Invalid => "CONFIGURED / INVALID",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown destination status."),
    };

    private static string FormatQueuePosition(StationRuntimeState state, int? globalIndex)
    {
        if (globalIndex is not int index)
        {
            return "NONE";
        }

        if (state.CurrentGlobalIndex == index
            && state.CurrentSequence is int sequence
            && !string.IsNullOrWhiteSpace(state.CurrentPlaylist))
        {
            return $"{Path.GetFileName(state.CurrentPlaylist)} sequence {sequence}";
        }

        return $"global item {index + 1}";
    }
}
