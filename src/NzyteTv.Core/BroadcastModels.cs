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
    double DurationSeconds);

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
