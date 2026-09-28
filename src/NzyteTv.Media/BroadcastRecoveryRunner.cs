using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record BroadcastRecoveryUpdate(
    BroadcastPlanItem Item,
    int Attempt,
    TimeSpan? Delay,
    string Message);

public sealed record BroadcastRecoveryResult(int ExitCode, int Attempts, BroadcastPlanItem? LastItem, string LastDiagnostic);

/// <summary>Owns only ephemeral broadcast playback state; playlists and scheduler history are never written.</summary>
public sealed class BroadcastRecoveryRunner
{
    private readonly FfmpegBroadcaster _broadcaster;
    private readonly BroadcastRecoveryPolicy _policy;

    public BroadcastRecoveryRunner(FfmpegBroadcaster broadcaster, BroadcastRecoveryPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);
        _broadcaster = broadcaster;
        _policy = policy ?? new BroadcastRecoveryPolicy();
    }

    public async Task<BroadcastRecoveryResult> RunAsync(
        BroadcastPlan plan,
        string destination,
        Action<string>? onFfmpegOutput,
        Action<BroadcastRecoveryUpdate>? onUpdate,
        CancellationToken cancellationToken)
    {
        int startIndex = 0;
        int consecutiveRetries = 0;
        BroadcastPlanItem? lastItem = plan.Items[0];
        string lastDiagnostic = string.Empty;

        while (startIndex < plan.Items.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset started = DateTimeOffset.UtcNow;
            int activeIndex = startIndex;
            bool reachedEndOfQueue = false;
            bool announced = false;
            BroadcastAttemptResult attempt = await _broadcaster.BroadcastAttemptAsync(
                plan,
                destination,
                startIndex,
                onFfmpegOutput,
                progress =>
                {
                    int relative = BroadcastPlaybackPosition.FindItemIndex(plan.Items.Skip(startIndex).ToArray(), progress);
                    reachedEndOfQueue = relative >= plan.Items.Count - startIndex;
                    activeIndex = reachedEndOfQueue ? plan.Items.Count - 1 : startIndex + relative;
                    lastItem = plan.Items[activeIndex];
                    if (!announced)
                    {
                        announced = true;
                        onUpdate?.Invoke(new BroadcastRecoveryUpdate(lastItem, 0, null, "Now playing"));
                    }
                },
                cancellationToken).ConfigureAwait(false);

            if (attempt.FfmpegExitCode == 0)
            {
                return new BroadcastRecoveryResult(0, consecutiveRetries, lastItem, lastDiagnostic);
            }

            // FFmpeg can report a transport error while closing a live FLV output after it
            // has already reported progress beyond the last queued asset. Do not replay it.
            if (reachedEndOfQueue)
            {
                return new BroadcastRecoveryResult(0, consecutiveRetries, lastItem, lastDiagnostic);
            }

            lastItem = plan.Items[activeIndex];
            lastDiagnostic = attempt.Diagnostic;
            BroadcastFailureKind kind = BroadcastFailureClassifier.Classify(attempt.FfmpegExitCode, lastDiagnostic, cancellationToken.IsCancellationRequested);
            if (kind is BroadcastFailureKind.Cancelled or BroadcastFailureKind.Permanent)
            {
                return new BroadcastRecoveryResult(attempt.FfmpegExitCode, consecutiveRetries, lastItem, lastDiagnostic);
            }

            // A session that streamed for five minutes is healthy enough to earn a fresh retry budget.
            if (DateTimeOffset.UtcNow - started >= _policy.HealthyThreshold && consecutiveRetries > 0)
            {
                consecutiveRetries = 0;
                onUpdate?.Invoke(new BroadcastRecoveryUpdate(lastItem, 0, null, "Broadcast stable; recovery retry budget reset."));
            }

            if (consecutiveRetries >= _policy.MaxConsecutiveRetries)
            {
                return new BroadcastRecoveryResult(attempt.FfmpegExitCode, consecutiveRetries, lastItem, lastDiagnostic);
            }

            consecutiveRetries++;
            TimeSpan delay = _policy.GetDelay(consecutiveRetries);
            onUpdate?.Invoke(new BroadcastRecoveryUpdate(lastItem, consecutiveRetries, delay, "Broadcast connection interrupted."));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            onUpdate?.Invoke(new BroadcastRecoveryUpdate(lastItem, consecutiveRetries, null, "Broadcast connection restored."));
            startIndex = activeIndex; // restart the interrupted asset; never replay completed assets.
        }

        return new BroadcastRecoveryResult(0, consecutiveRetries, lastItem, lastDiagnostic);
    }
}
