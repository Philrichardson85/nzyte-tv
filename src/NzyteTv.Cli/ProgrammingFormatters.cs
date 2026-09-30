using System.Globalization;
using System.Text;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Cli;

public static class ProgrammingFormatters
{
    public static string FormatInitialization(ProgrammingConfigurationInitializationResult result)
    {
        var output = new StringBuilder();
        output.AppendLine("NZYTE TV Programming Initialization");
        output.AppendLine();
        output.AppendLine($"Configuration:        {result.Path}");
        output.AppendLine($"Result:               {(result.Created ? "CREATED" : "EXISTING / UNCHANGED")}");
        output.AppendLine($"Schema:               {result.Configuration.SchemaVersion}");
        output.AppendLine($"Revision:             {result.Configuration.Revision}");
        output.AppendLine();
        output.AppendLine("Scheduler integration: ACTIVE");
        return output.ToString();
    }

    public static string FormatValidation(ProgrammingValidationResult result)
    {
        var output = new StringBuilder();
        output.AppendLine("NZYTE TV Programming Validation");
        output.AppendLine();
        output.AppendLine($"Configuration:        {result.Paths.ConfigurationPath}");
        output.AppendLine($"Config:               {(result.Configuration is null ? "INVALID" : "VALID")}");
        output.AppendLine($"Catalog:              {(File.Exists(result.Paths.CatalogPath) ? "AVAILABLE" : "NOT AVAILABLE")}");
        output.AppendLine($"Library:              {(Directory.Exists(result.Paths.LibraryRoot) ? "AVAILABLE" : "NOT AVAILABLE")}");
        if (result.Inventory is not null)
        {
            output.AppendLine($"Known assets:          {result.Inventory.Assets.Select(asset => asset.AssetId).Distinct(StringComparer.Ordinal).Count()}");
            output.AppendLine($"Eligible assets:       {result.Inventory.Assets.Count(asset => asset.IsTechnicallyPlaylistEligible)}");
        }

        AppendDiagnostics(output, "Errors", result.Errors);
        AppendDiagnostics(output, "Warnings", result.Warnings);
        output.AppendLine();
        output.AppendLine("Status:");
        output.AppendLine($"    {(result.IsValid ? "VALID" : "INVALID")}");
        return output.ToString();
    }

    public static string FormatStatus(ProgrammingValidationResult result)
    {
        var output = new StringBuilder();
        output.AppendLine("NZYTE TV Programming");
        output.AppendLine();
        output.AppendLine($"Configuration:        {result.Paths.ConfigurationPath}");
        if (result.Configuration is not ProgrammingConfiguration configuration)
        {
            output.AppendLine("Scheduler integration: NOT ACTIVE");
            AppendDiagnostics(output, "Errors", result.Errors);
            return output.ToString();
        }

        ActiveCampaign campaign = configuration.ActiveCampaign!;
        IReadOnlyDictionary<string, AssetEditorialOverride> overrides = configuration.AssetOverrides!;
        output.AppendLine($"Config schema:         {configuration.SchemaVersion}");
        output.AppendLine($"Revision:              {configuration.Revision}");
        output.AppendLine();
        output.AppendLine("Campaign:");
        output.AppendLine($"    Content group:     {(campaign.Enabled ? campaign.ContentGroupId : "NONE")}");
        output.AppendLine($"    Multiplier:        {campaign.WeightMultiplier.ToString("0.###", CultureInfo.InvariantCulture)}x");
        output.AppendLine();
        output.AppendLine("Editorial overrides:");
        output.AppendLine($"    Total:             {overrides.Count}");
        output.AppendLine($"    Do Not Air:        {overrides.Count(item => item.Value.DoNotAir)}");
        output.AppendLine($"    Custom weight:     {overrides.Count(item => item.Value.WeightMultiplier != 1.0)}");
        output.AppendLine();
        output.AppendLine("Repetition:");
        output.AppendLine($"    Exact asset:       {configuration.Repetition!.ExactAssetCooldownMinutes} minutes preferred");
        output.AppendLine("    Same-song adjacent: NEVER when a valid alternative exists");
        output.AppendLine($"    Same-song lookback:{configuration.Repetition.SameContentGroupLookback,3} substantial pieces");
        output.AppendLine();
        output.AppendLine("Cadence:");
        output.AppendLine($"    Bumper:            {configuration.StationImaging!.MinimumSubstantialPieces}-{configuration.StationImaging.MaximumSubstantialPieces} substantial pieces");
        output.AppendLine($"    Promo:             {configuration.PromoCadence!.MinimumIntervalMinutes}-{configuration.PromoCadence.MaximumIntervalMinutes} minutes");
        output.AppendLine();
        output.AppendLine("Programming personalities:");
        foreach (ProgrammingPersonality personality in configuration.Personalities!)
        {
            output.AppendLine($"    {personality.Name!.ToUpperInvariant()} ({personality.Lanes!.Count} lanes)");
        }

        output.AppendLine();
        output.AppendLine($"Release-age rotation: {(configuration.ReleaseAgeHotRotationEnabled ? "ENABLED" : "OFF")}");
        output.AppendLine("Scheduler integration: ACTIVE");
        output.AppendLine("Rolling future blocks: NOT IMPLEMENTED");
        AppendDiagnostics(output, "Errors", result.Errors);
        AppendDiagnostics(output, "Warnings", result.Warnings);
        return output.ToString();
    }

    public static string FormatMutation(ProgrammingMutationResult result)
    {
        var output = new StringBuilder();
        output.AppendLine("NZYTE TV Programming Update");
        output.AppendLine();
        output.AppendLine(result.Description);
        output.AppendLine($"Configuration:        {result.Paths.ConfigurationPath}");
        output.AppendLine($"Revision:             {result.Configuration.Revision}");
        output.AppendLine($"Changed:              {(result.Changed ? "YES" : "NO")}");
        return output.ToString();
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
