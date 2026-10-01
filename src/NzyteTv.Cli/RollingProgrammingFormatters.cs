using System.Globalization;
using System.Text;
using NzyteTv.Media;

namespace NzyteTv.Cli;

public static class RollingProgrammingFormatters
{
    public static string FormatInitialization(RollingInitializationResult result)
    {
        var output = new StringBuilder();
        output.AppendLine("NZYTE TV Rolling Programming Initialization");
        output.AppendLine();
        output.AppendLine($"Rolling root:          {result.Paths.RollingRoot}");
        output.AppendLine($"Result:                {(result.Created ? "CREATED" : "EXISTING / UNCHANGED")}");
        output.AppendLine($"Manifest schema:       {result.Manifest.SchemaVersion}");
        output.AppendLine($"Planner lineage:       {result.Manifest.PlannerId}");
        output.AppendLine($"Base seed:             {result.Manifest.BaseSeed.ToString(CultureInfo.InvariantCulture)}");
        output.AppendLine($"Genesis history:       {(result.ImportedHistory ? "IMPORTED" : "EMPTY")}");
        output.AppendLine($"Prepared-block target: {result.Manifest.TargetPreparedBlockCount}");
        output.AppendLine();
        output.AppendLine("Rolling execution/handoff: NOT IMPLEMENTED");
        return output.ToString();
    }

    public static string FormatMaintenance(RollingMaintainResult result)
    {
        var output = new StringBuilder();
        output.AppendLine("NZYTE TV Rolling Programming Maintenance");
        output.AppendLine();
        output.AppendLine($"Rolling root:          {result.Paths.RollingRoot}");
        output.AppendLine($"Generated this run:    {result.GeneratedBlockCount}");
        output.AppendLine($"Adopted after recovery:{result.AdoptedBlockCount,3}");
        output.AppendLine($"Committed blocks:      {result.Manifest.Blocks!.Count}");
        output.AppendLine($"Prepared-block target: {result.Manifest.TargetPreparedBlockCount}");
        output.AppendLine($"Next sequence:         {result.Manifest.NextSequence}");
        output.AppendLine($"Status:                {(result.TargetSatisfied ? "PREPARED" : "INCOMPLETE")}");
        output.AppendLine();
        output.AppendLine("Rolling execution/handoff: NOT IMPLEMENTED");
        return output.ToString();
    }

    public static string FormatValidation(RollingValidationResult result)
    {
        var output = new StringBuilder();
        output.AppendLine("NZYTE TV Rolling Programming Validation");
        output.AppendLine();
        output.AppendLine($"Manifest:              {result.Paths.ManifestPath}");
        output.AppendLine($"Schema:                {(result.Manifest is null ? "UNAVAILABLE" : result.Manifest.SchemaVersion)}");
        output.AppendLine($"Committed blocks:      {result.Manifest?.Blocks?.Count ?? 0}");
        AppendDiagnostics(output, "Errors", result.Errors);
        AppendDiagnostics(output, "Warnings", result.Warnings);
        output.AppendLine();
        output.AppendLine("Status:");
        output.AppendLine($"    {(result.IsValid ? "VALID" : "INVALID")}");
        output.AppendLine();
        output.AppendLine("Rolling execution/handoff: NOT IMPLEMENTED");
        return output.ToString();
    }

    public static string FormatStatus(RollingProgrammingStatus status)
    {
        var output = new StringBuilder();
        output.AppendLine("NZYTE TV Rolling Programming");
        output.AppendLine();
        output.AppendLine($"Rolling root:          {status.Paths.RollingRoot}");
        if (status.Manifest is not { } manifest)
        {
            output.AppendLine("Manifest:              NOT INITIALIZED / INVALID");
            AppendDiagnostics(output, "Errors", status.Validation.Errors);
            output.AppendLine();
            output.AppendLine("Rolling execution/handoff: NOT IMPLEMENTED");
            return output.ToString();
        }

        output.AppendLine($"Manifest schema:       {manifest.SchemaVersion}");
        output.AppendLine($"Planner lineage:       {manifest.PlannerId}");
        output.AppendLine($"Base seed:             {manifest.BaseSeed.ToString(CultureInfo.InvariantCulture)}");
        output.AppendLine($"Block target duration: {FormatDuration(manifest.TargetBlockDurationSeconds)}");
        output.AppendLine($"Prepared-block target: {manifest.TargetPreparedBlockCount}");
        output.AppendLine($"Committed blocks:      {manifest.Blocks!.Count}");
        output.AppendLine($"Committed range:       {FormatRange(manifest)}");
        output.AppendLine($"Next sequence:         {manifest.NextSequence}");
        output.AppendLine($"History head:          {Prefix(manifest.HistoryHead!.Sha256)}");
        output.AppendLine($"Prepared nominal:      {FormatDuration(manifest.Blocks.Count * manifest.TargetBlockDurationSeconds)}");
        output.AppendLine($"Prepared actual:       {FormatDuration(status.PreparedActualDurationSeconds)}");
        output.AppendLine($"Visible policy revision:{FormatRevision(status.LatestProgrammingRevision),3}");
        output.AppendLine($"Staging entries:       {status.StagingEntryCount}");
        output.AppendLine($"Uncommitted/quarantine:{status.OrphanedEntryCount,3}");
        output.AppendLine();
        output.AppendLine("Committed blocks:");
        foreach (RollingBlockStatus block in status.Blocks)
        {
            output.AppendLine(
                $"    {block.Sequence,4}: {Prefix(block.BlockId)}  " +
                $"actual {FormatDuration(block.ActualDurationSeconds)}  " +
                $"policy {FormatRevision(block.ProgrammingRevision)}");
        }

        if (status.Blocks.Count == 0)
        {
            output.AppendLine("    (none)");
        }

        AppendDiagnostics(output, "Errors", status.Validation.Errors);
        AppendDiagnostics(output, "Warnings", status.Validation.Warnings);
        output.AppendLine();
        output.AppendLine($"Validation health:     {(status.Validation.IsValid ? "HEALTHY" : "INVALID")}");
        output.AppendLine("Rolling execution/handoff: NOT IMPLEMENTED");
        return output.ToString();
    }

    private static string FormatRange(NzyteTv.Core.RollingProgrammingManifest manifest) =>
        manifest.Blocks!.Count == 0
            ? "NONE"
            : $"{manifest.Blocks[0].Sequence}-{manifest.Blocks[^1].Sequence}";

    private static string Prefix(string value) => value[..Math.Min(12, value.Length)];

    private static string FormatRevision(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "LEGACY";

    private static string FormatDuration(double seconds)
    {
        TimeSpan duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalDays >= 1
            ? duration.ToString("d'.'hh':'mm':'ss", CultureInfo.InvariantCulture)
            : duration.ToString("hh':'mm':'ss", CultureInfo.InvariantCulture);
    }

    private static void AppendDiagnostics(
        StringBuilder output,
        string heading,
        IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        output.AppendLine();
        output.AppendLine($"{heading}:");
        foreach (string value in values)
        {
            output.AppendLine($"    - {value}");
        }
    }
}
