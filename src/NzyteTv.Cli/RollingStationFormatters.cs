using System.Globalization;
using System.Text;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Cli;

public static class RollingStationFormatters
{
    public static string FormatValidation(RollingStationValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var output = new StringBuilder();
        output.AppendLine("NZYTE TV Rolling Station Validation");
        output.AppendLine();
        output.AppendLine($"Configuration:       {(result.Configuration is null ? "INVALID" : "VALID")}");
        output.AppendLine($"Static station:      {(result.StationConfiguration is null ? "INVALID" : "VALID")}");
        output.AppendLine($"Planner manifest:    {(result.Manifest is null ? "INVALID" : "VALID")}");
        output.AppendLine($"Committed blocks:    {result.Manifest?.Blocks?.Count ?? 0}");
        output.AppendLine($"Execution state:     {FormatPhase(result.RollingState)}");
        output.AppendLine($"CP2 state:           {result.StationState?.StationState.ToString().ToUpperInvariant() ?? "NOT PRESENT"}");
        output.AppendLine($"FFmpeg:              {(result.FfmpegAvailable ? "AVAILABLE" : "NOT AVAILABLE")}");
        output.AppendLine($"Destination env:     {FormatDestination(result.DestinationStatus)}");
        output.AppendLine($"Buffer health:       {FormatBufferHealth(result.Buffer)}");
        output.AppendLine($"Replenishment:       {FormatReplenishmentHealth(result.ReplenishmentState)}");
        output.AppendLine();
        output.AppendLine("Status:");
        output.AppendLine($"    {(result.IsReady ? "READY" : "NOT READY")}");
        foreach (string warning in result.Warnings)
        {
            output.AppendLine($"    WARNING: {warning}");
        }

        foreach (string error in result.Errors)
        {
            output.AppendLine($"    ERROR: {StationSecretRedactor.RedactRtmpUrls(error)}");
        }

        output.AppendLine();
        output.AppendLine("Rolling execution/handoff: CONFIGURED BY CHECKPOINT 3B2-A");
        output.AppendLine("Automatic block replenishment: ACTIVE DURING ROLLING RUN");
        return output.ToString();
    }

    public static string FormatStatus(RollingStationStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RollingStationValidationResult validation = snapshot.Validation;
        RollingStationRuntimeState? state = validation.RollingState;
        var output = new StringBuilder();
        output.AppendLine("NZYTE TV Rolling Station");
        output.AppendLine();
        output.AppendLine($"Coordinator:         {FormatPhase(state)}");
        output.AppendLine($"Planner lineage:     {Prefix(validation.Configuration?.PlannerId)}");
        output.AppendLine($"Committed blocks:    {validation.Manifest?.Blocks?.Count ?? 0}");
        output.AppendLine($"Next required:       {snapshot.NextRequiredSequence.ToString(CultureInfo.InvariantCulture)}");
        output.AppendLine($"Next committed:      {(snapshot.NextBlockCommitted ? "YES" : "NO")}");
        if (validation.Buffer is RollingBufferSnapshot buffer)
        {
            output.AppendLine($"Highest committed:   {buffer.HighestCommittedSequence.ToString(CultureInfo.InvariantCulture)}");
            output.AppendLine($"Committed future:    {buffer.CommittedFutureBlockCount.ToString(CultureInfo.InvariantCulture)}");
            output.AppendLine($"Future target:       {buffer.FutureBlockTarget.ToString(CultureInfo.InvariantCulture)}");
            output.AppendLine($"Required through:    {buffer.RequiredHighestSequence.ToString(CultureInfo.InvariantCulture)}");
            output.AppendLine($"Buffer deficit:      {buffer.BufferDeficit.ToString(CultureInfo.InvariantCulture)}");
            output.AppendLine($"Buffer health:       {buffer.Health.ToString().ToUpperInvariant()}");
        }
        if (state?.ActiveBlockSequence is long activeSequence)
        {
            output.AppendLine($"Active block:        {activeSequence} ({Prefix(state.ActiveBlockId)})");
            output.AppendLine($"Claimed:             {FormatTimestamp(state.ClaimedAtUtc)}");
        }

        if (state?.LastCompletedBlockSequence is long completedSequence)
        {
            output.AppendLine($"Last completed:      {completedSequence} ({Prefix(state.LastCompletedBlockId)})");
            output.AppendLine($"Completed at:        {FormatTimestamp(state.LastCompletedAtUtc)}");
        }

        if (state?.FailureDisposition is RollingStationFailureDisposition disposition)
        {
            output.AppendLine($"Failure disposition: {disposition.ToString().ToUpperInvariant()}");
        }

        output.AppendLine();
        output.AppendLine("CP2 station:");
        output.AppendLine($"  Status:            {snapshot.StationStatus?.Status.ToString().ToUpperInvariant() ?? "NOT PRESENT"}");
        output.AppendLine($"  Queue state:       {validation.StationState?.StationState.ToString().ToUpperInvariant() ?? "NOT PRESENT"}");
        if (validation.StationState?.CurrentSequence is int sequence)
        {
            output.AppendLine($"  Current sequence:  {sequence}");
        }

        output.AppendLine();
        output.AppendLine($"Validation:          {(validation.IsReady ? "HEALTHY" : "NOT READY")}");
        output.AppendLine($"FFmpeg:              {(validation.FfmpegAvailable ? "AVAILABLE" : "NOT AVAILABLE")}");
        output.AppendLine($"Destination env:     {FormatDestination(validation.DestinationStatus)}");
        output.AppendLine("YouTube monitoring:  NOT CONFIGURED");
        output.AppendLine($"Automatic replenish: {FormatReplenishmentHealth(validation.ReplenishmentState)}");
        if (validation.ReplenishmentState is RollingReplenishmentState replenishment)
        {
            output.AppendLine($"Last replenish:      {FormatTimestamp(replenishment.LastSuccessAtUtc)}");
            if (!string.IsNullOrWhiteSpace(replenishment.LastError))
            {
                output.AppendLine(
                    $"Last replenish error: {StationSecretRedactor.RedactRtmpUrls(replenishment.LastError)}");
            }
        }
        if (!string.IsNullOrWhiteSpace(state?.LastTransitionError))
        {
            output.AppendLine($"Last transition:     {StationSecretRedactor.RedactRtmpUrls(state.LastTransitionError)}");
        }

        foreach (string warning in validation.Warnings)
        {
            output.AppendLine($"WARNING: {warning}");
        }

        foreach (string error in validation.Errors)
        {
            output.AppendLine($"ERROR: {StationSecretRedactor.RedactRtmpUrls(error)}");
        }

        return output.ToString();
    }

    private static string FormatPhase(RollingStationRuntimeState? state) =>
        state?.Phase.ToString().ToUpperInvariant() ?? "UNINITIALIZED";

    private static string Prefix(string? value) => string.IsNullOrWhiteSpace(value)
        ? "NONE"
        : value[..Math.Min(12, value.Length)];

    private static string FormatTimestamp(DateTimeOffset? value) => value is null
        ? "NONE"
        : value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string FormatBufferHealth(RollingBufferSnapshot? buffer) =>
        buffer?.Health.ToString().ToUpperInvariant() ?? "UNKNOWN";

    private static string FormatReplenishmentHealth(RollingReplenishmentState? state) =>
        state?.Health switch
        {
            RollingReplenishmentHealth.Healthy => "HEALTHY",
            RollingReplenishmentHealth.Degraded => "DEGRADED",
            RollingReplenishmentHealth.Blocked => "BLOCKED",
            RollingReplenishmentHealth.SafetyFailure => "SAFETY FAILURE",
            null => "NOT YET OBSERVED",
            _ => "UNKNOWN",
        };

    private static string FormatDestination(BroadcastDestinationStatus status) => status switch
    {
        BroadcastDestinationStatus.NotConfigured => "NOT CONFIGURED",
        BroadcastDestinationStatus.Valid => "CONFIGURED / VALID",
        BroadcastDestinationStatus.Invalid => "CONFIGURED / INVALID",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown destination status."),
    };
}
