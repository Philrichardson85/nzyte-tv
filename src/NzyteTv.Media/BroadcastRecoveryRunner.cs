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
        return await RunAsync(
            plan,
            destination,
            startItemIndex: 0,
            onFfmpegOutput,
            onUpdate,
            observer: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BroadcastRecoveryResult> RunAsync(
        BroadcastPlan plan,
        string destination,
        Action<string>? onFfmpegOutput,
        Action<BroadcastRecoveryUpdate>? onUpdate,
        IBroadcastRuntimeObserver? observer,
        CancellationToken cancellationToken)
    {
        return await RunAsync(
            plan,
            destination,
            startItemIndex: 0,
            onFfmpegOutput,
            onUpdate,
            observer,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BroadcastRecoveryResult> RunAsync(
        BroadcastPlan plan,
        string destination,
        int startItemIndex,
        Action<string>? onFfmpegOutput,
        Action<BroadcastRecoveryUpdate>? onUpdate,
        IBroadcastRuntimeObserver? observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsReady) throw new InvalidOperationException("Broadcast plan is not ready.");
        if (startItemIndex < 0 || startItemIndex >= plan.Items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startItemIndex));
        }

        int startIndex = startItemIndex;
        int consecutiveRetries = 0;
        BroadcastPlanItem? lastItem = plan.Items[startIndex];
        string lastDiagnostic = string.Empty;

        observer?.OnEvent(new BroadcastRuntimeEvent(
            BroadcastRuntimeEventKind.BroadcastStarted,
            lastItem,
            GlobalItemIndex: startIndex));

        while (startIndex < plan.Items.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset started = DateTimeOffset.UtcNow;
            int activeIndex = startIndex;
            bool reachedEndOfQueue = false;
            bool announced = false;
            int observedIndex = -1;
            int highestCompletedIndex = startIndex - 1;

            void ReportCompletedThrough(int completionIndex)
            {
                int finalIndex = Math.Min(completionIndex, plan.Items.Count - 1);
                while (highestCompletedIndex < finalIndex)
                {
                    highestCompletedIndex++;
                    observer?.OnEvent(new BroadcastRuntimeEvent(
                        BroadcastRuntimeEventKind.ItemCompleted,
                        plan.Items[highestCompletedIndex],
                        RecoveryAttempts: consecutiveRetries,
                        GlobalItemIndex: highestCompletedIndex));
                }
            }

            BroadcastAttemptResult attempt = await _broadcaster.BroadcastAttemptAsync(
                plan,
                destination,
                startIndex,
                onFfmpegOutput,
                progress =>
                {
                    int relative = BroadcastPlaybackPosition.FindItemIndex(plan.Items.Skip(startIndex).ToArray(), progress);
                    bool progressReachedEnd = relative >= plan.Items.Count - startIndex;
                    reachedEndOfQueue |= progressReachedEnd;
                    int mappedIndex = progressReachedEnd ? plan.Items.Count - 1 : startIndex + relative;
                    ReportCompletedThrough(progressReachedEnd ? plan.Items.Count - 1 : mappedIndex - 1);
                    activeIndex = Math.Max(activeIndex, mappedIndex);
                    lastItem = plan.Items[activeIndex];
                    if (!announced)
                    {
                        announced = true;
                        onUpdate?.Invoke(new BroadcastRecoveryUpdate(lastItem, 0, null, "Now playing"));
                    }

                    if (observedIndex != activeIndex)
                    {
                        observedIndex = activeIndex;
                        observer?.OnEvent(new BroadcastRuntimeEvent(
                            BroadcastRuntimeEventKind.ItemChanged,
                            lastItem,
                            RecoveryAttempts: consecutiveRetries,
                            GlobalItemIndex: activeIndex));
                    }
                },
                observer,
                cancellationToken).ConfigureAwait(false);

            if (attempt.FfmpegExitCode == 0)
            {
                ReportCompletedThrough(plan.Items.Count - 1);
                observer?.OnEvent(new BroadcastRuntimeEvent(
                    BroadcastRuntimeEventKind.BroadcastCompleted,
                    plan.Items[^1],
                    RecoveryAttempts: consecutiveRetries,
                    GlobalItemIndex: plan.Items.Count - 1));
                return new BroadcastRecoveryResult(0, consecutiveRetries, lastItem, lastDiagnostic);
            }

            // FFmpeg can report a transport error while closing a live FLV output after it
            // has already reported progress beyond the last queued asset. Do not replay it.
            if (reachedEndOfQueue)
            {
                observer?.OnEvent(new BroadcastRuntimeEvent(
                    BroadcastRuntimeEventKind.BroadcastCompleted,
                    lastItem,
                    RecoveryAttempts: consecutiveRetries,
                    GlobalItemIndex: plan.Items.Count - 1));
                return new BroadcastRecoveryResult(0, consecutiveRetries, lastItem, lastDiagnostic);
            }

            lastItem = plan.Items[activeIndex];
            lastDiagnostic = attempt.Diagnostic;
            BroadcastFailureKind kind = BroadcastFailureClassifier.Classify(attempt.FfmpegExitCode, lastDiagnostic, cancellationToken.IsCancellationRequested);
            if (kind is BroadcastFailureKind.Cancelled or BroadcastFailureKind.Permanent)
            {
                observer?.OnEvent(new BroadcastRuntimeEvent(
                    BroadcastRuntimeEventKind.BroadcastFailed,
                    lastItem,
                    RecoveryAttempts: consecutiveRetries,
                    GlobalItemIndex: activeIndex));
                return new BroadcastRecoveryResult(attempt.FfmpegExitCode, consecutiveRetries, lastItem, lastDiagnostic);
            }

            // A session that streamed for five minutes is healthy enough to earn a fresh retry budget.
            if (DateTimeOffset.UtcNow - started >= _policy.HealthyThreshold && consecutiveRetries > 0)
            {
                consecutiveRetries = 0;
                onUpdate?.Invoke(new BroadcastRecoveryUpdate(lastItem, 0, null, "Broadcast stable; recovery retry budget reset."));
                observer?.OnEvent(new BroadcastRuntimeEvent(
                    BroadcastRuntimeEventKind.RecoveryBudgetReset,
                    lastItem,
                    GlobalItemIndex: activeIndex));
            }

            if (consecutiveRetries >= _policy.MaxConsecutiveRetries)
            {
                observer?.OnEvent(new BroadcastRuntimeEvent(
                    BroadcastRuntimeEventKind.BroadcastFailed,
                    lastItem,
                    RecoveryAttempts: consecutiveRetries,
                    GlobalItemIndex: activeIndex));
                return new BroadcastRecoveryResult(attempt.FfmpegExitCode, consecutiveRetries, lastItem, lastDiagnostic);
            }

            consecutiveRetries++;
            TimeSpan delay = _policy.GetDelay(consecutiveRetries);
            onUpdate?.Invoke(new BroadcastRecoveryUpdate(lastItem, consecutiveRetries, delay, "Broadcast connection interrupted."));
            observer?.OnEvent(new BroadcastRuntimeEvent(
                BroadcastRuntimeEventKind.RecoveryStarted,
                lastItem,
                RecoveryAttempts: consecutiveRetries,
                GlobalItemIndex: activeIndex));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            onUpdate?.Invoke(new BroadcastRecoveryUpdate(lastItem, consecutiveRetries, null, "Broadcast connection restored."));
            startIndex = activeIndex; // restart the interrupted asset; never replay completed assets.
        }

        return new BroadcastRecoveryResult(0, consecutiveRetries, lastItem, lastDiagnostic);
    }
}
