using System.Text;
using NzyteTv.Core;

namespace NzyteTv.Cli;

public static class BroadcastSummaryFormatter
{
    public static string Format(BroadcastPlan plan, bool destinationConfigured)
    {
        ArgumentNullException.ThrowIfNull(plan);
        TimeSpan duration = TimeSpan.FromSeconds(Math.Max(0, plan.ScheduledDurationSeconds));
        var output = new StringBuilder();
        output.AppendLine("NZYTE TV Broadcast Plan");
        output.AppendLine();
        output.AppendLine($"Playlists:                  {plan.PlaylistCount}");
        output.AppendLine($"Scheduled items:            {plan.ScheduledItemCount}");
        output.AppendLine($"Duration:                   {(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}");
        output.AppendLine($"Missing files:              {plan.MissingFileCount}");
        output.AppendLine($"Invalid paths:              {plan.InvalidPathCount}");
        output.AppendLine($"Unready assets:             {plan.UnreadyAssetCount}");
        output.AppendLine($"Library root:               {plan.LibraryRoot}");
        if (destinationConfigured)
        {
            output.AppendLine("Destination configured:     YES");
        }

        output.AppendLine();
        output.AppendLine("Status:");
        output.AppendLine($"    {(plan.IsReady ? "READY" : "NOT READY")}");
        foreach (BroadcastPlanIssue issue in plan.Issues)
        {
            output.AppendLine(
                $"    - playlist {Path.GetFileName(issue.PlaylistPath)}, sequence {issue.Sequence}: {issue.Detail}");
        }

        return output.ToString();
    }
}
