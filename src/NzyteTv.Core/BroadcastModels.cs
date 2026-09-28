namespace NzyteTv.Core;

public enum BroadcastPlanIssueKind
{
    MissingFile,
    InvalidPath,
    UnreadyAsset,
}

public sealed record BroadcastPlanIssue(
    BroadcastPlanIssueKind Kind,
    string PlaylistPath,
    int Sequence,
    string Detail);

public sealed record BroadcastPlanItem(
    string PlaylistPath,
    int Sequence,
    string AssetId,
    string RelativePath,
    string MediaPath,
    double DurationSeconds,
    string Title = "",
    string Type = "");

public sealed record BroadcastPlan(
    string LibraryRoot,
    IReadOnlyList<string> PlaylistPaths,
    IReadOnlyList<BroadcastPlanItem> Items,
    IReadOnlyList<BroadcastPlanIssue> Issues,
    int ScheduledItemCount,
    double ScheduledDurationSeconds)
{
    public int PlaylistCount => PlaylistPaths.Count;

    public int MissingFileCount => Issues.Count(issue => issue.Kind == BroadcastPlanIssueKind.MissingFile);

    public int InvalidPathCount => Issues.Count(issue => issue.Kind == BroadcastPlanIssueKind.InvalidPath);

    public int UnreadyAssetCount => Issues.Count(issue => issue.Kind == BroadcastPlanIssueKind.UnreadyAsset);

    public bool IsReady => Issues.Count == 0 && Items.Count == ScheduledItemCount;
}

/// <summary>Policy for a single uninterrupted broadcast connection.</summary>
public sealed record BroadcastRecoveryPolicy(
    int MaxConsecutiveRetries = 10,
    TimeSpan? MaximumDelay = null,
    TimeSpan? HealthySessionThreshold = null)
{
    public TimeSpan MaxDelay { get; init; } = MaximumDelay ?? TimeSpan.FromSeconds(60);

    public TimeSpan HealthyThreshold { get; init; } = HealthySessionThreshold ?? TimeSpan.FromMinutes(5);

    public TimeSpan GetDelay(int retryNumber)
    {
        if (retryNumber <= 0) throw new ArgumentOutOfRangeException(nameof(retryNumber));
        double seconds = retryNumber switch { 1 => 2, 2 => 5, 3 => 10, 4 => 20, 5 => 30, _ => 60 };
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }
}

public static class BroadcastPlaybackPosition
{
    /// <summary>Maps FFmpeg output time to the item in a remaining flattened queue.</summary>
    public static int FindItemIndex(IReadOnlyList<BroadcastPlanItem> items, TimeSpan outputTime)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) throw new ArgumentException("At least one item is required.", nameof(items));
        double elapsed = Math.Max(0, outputTime.TotalSeconds);
        double offset = 0;
        for (int index = 0; index < items.Count; index++)
        {
            offset += items[index].DurationSeconds;
            if (elapsed < offset) return index;
        }

        return items.Count;
    }
}
