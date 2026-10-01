using NzyteTv.Cli;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Core.Tests;

public sealed class ProgrammingFormatterTests
{
    [Fact]
    public void StatusReportsRequiredPolicyWithoutSecrets()
    {
        const string secret = "rtmps://example.invalid/live2/FAKE-SECRET";
        ProgrammingConfiguration configuration = ProgrammingConfiguration.CreateDefault() with
        {
            Revision = 4,
            ActiveCampaign = new ActiveCampaign
            {
                Enabled = true,
                ContentGroupId = "free-fallin",
                WeightMultiplier = 2,
            },
            AssetOverrides = new Dictionary<string, AssetEditorialOverride>
            {
                ["hidden"] = new() { DoNotAir = true },
                ["favored"] = new() { WeightMultiplier = 1.5 },
            },
        };
        var paths = new ProgrammingPaths(
            "/media",
            "/media/library",
            "/media/catalog/song-catalog.json",
            "/media/catalog/programming.json");
        var result = new ProgrammingValidationResult(
            paths,
            configuration,
            new ProgrammingInventory([], []),
            [],
            []);

        string output = ProgrammingFormatters.FormatStatus(result);

        Assert.Contains("Config schema:         1", output, StringComparison.Ordinal);
        Assert.Contains("Revision:              4", output, StringComparison.Ordinal);
        Assert.Contains("free-fallin", output, StringComparison.Ordinal);
        Assert.Contains("2x", output, StringComparison.Ordinal);
        Assert.Contains("Do Not Air:        1", output, StringComparison.Ordinal);
        Assert.Contains("Custom weight:     1", output, StringComparison.Ordinal);
        Assert.Contains("120 minutes", output, StringComparison.Ordinal);
        Assert.Contains("NEVER", output, StringComparison.Ordinal);
        Assert.Contains("Maximum short run:   3 substantial pieces", output, StringComparison.Ordinal);
        Assert.Contains("MUSIC-HEAVY", output, StringComparison.Ordinal);
        Assert.Contains("Scheduler integration: ACTIVE", output, StringComparison.Ordinal);
        Assert.Contains("Rolling block planning: AVAILABLE", output, StringComparison.Ordinal);
        Assert.Contains("Rolling execution/handoff: SEPARATE OPT-IN STATION COMMAND", output, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.DoesNotContain("NZYTE_TV_RTMP_URL", output, StringComparison.Ordinal);
    }
}
