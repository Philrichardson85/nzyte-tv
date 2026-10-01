using NzyteTv.Cli;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Core.Tests;

public sealed class RollingProgrammingFormatterTests
{
    [Fact]
    public void Status_ReportsRequiredPlanningDataAndNoExecutionOrSecretClaim()
    {
        const string secret = "rtmps://example.invalid/live2/FAKE-SECRET";
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot("/media");
        RollingProgrammingManifest manifest = CreateManifest();
        var validation = new RollingValidationResult(paths, manifest, [], []);
        var status = new RollingProgrammingStatus(
            paths,
            manifest,
            [new RollingBlockStatus(1, new string('b', 64), 21605, 7)],
            LatestProgrammingRevision: 8,
            StagingEntryCount: 0,
            OrphanedEntryCount: 0,
            PreparedActualDurationSeconds: 21605,
            validation);

        string output = RollingProgrammingFormatters.FormatStatus(status);

        Assert.Contains("Manifest schema:       1", output, StringComparison.Ordinal);
        Assert.Contains("Lineage purpose:       PRODUCTION DURATION", output, StringComparison.Ordinal);
        Assert.Contains("Prepared-block target: 3", output, StringComparison.Ordinal);
        Assert.Contains("Committed range:       1-1", output, StringComparison.Ordinal);
        Assert.Contains("Visible policy revision:  8", output, StringComparison.Ordinal);
        Assert.Contains("Validation health:     HEALTHY", output, StringComparison.Ordinal);
        Assert.Contains("Rolling execution/handoff: NOT PERFORMED BY PLANNER", output, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.DoesNotContain("NZYTE_TV_RTMP_URL", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TestLineage_IsProminentlyLabeledInInitializationAndStatus()
    {
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot("/media");
        RollingProgrammingManifest manifest = CreateManifest(240);
        var validation = new RollingValidationResult(paths, manifest, [], []);
        var status = new RollingProgrammingStatus(
            paths,
            manifest,
            [new RollingBlockStatus(1, new string('b', 64), 251, 7)],
            LatestProgrammingRevision: 7,
            StagingEntryCount: 0,
            OrphanedEntryCount: 0,
            PreparedActualDurationSeconds: 251,
            validation);

        string initialization = RollingProgrammingFormatters.FormatInitialization(
            new RollingInitializationResult(paths, manifest, true, false));
        string statusOutput = RollingProgrammingFormatters.FormatStatus(status);

        Assert.Contains("Block target duration: 00:04:00", initialization, StringComparison.Ordinal);
        Assert.Contains("TEST LINEAGE — NON-PRODUCTION DURATION", initialization, StringComparison.Ordinal);
        Assert.Contains("Block target duration: 00:04:00", statusOutput, StringComparison.Ordinal);
        Assert.Contains("actual 00:04:11", statusOutput, StringComparison.Ordinal);
        Assert.Contains("TEST LINEAGE — NON-PRODUCTION DURATION", statusOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("initialization")]
    [InlineData("maintenance")]
    [InlineData("validation")]
    public void OtherFormatters_StateThatExecutionHandoffIsNotImplemented(string formatter)
    {
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot("/media");
        RollingProgrammingManifest manifest = CreateManifest();
        string output = formatter switch
        {
            "initialization" => RollingProgrammingFormatters.FormatInitialization(
                new RollingInitializationResult(paths, manifest, true, false)),
            "maintenance" => RollingProgrammingFormatters.FormatMaintenance(
                new RollingMaintainResult(paths, manifest, 0, 0, true)),
            "validation" => RollingProgrammingFormatters.FormatValidation(
                new RollingValidationResult(paths, manifest, [], [])),
            _ => throw new InvalidOperationException(),
        };

        Assert.Contains("Rolling execution/handoff: NOT PERFORMED BY PLANNER", output, StringComparison.Ordinal);
    }

    private static RollingProgrammingManifest CreateManifest(double targetDurationSeconds = 21600)
    {
        var history = new RollingArtifactReference($"history/{new string('a', 64)}.json", new string('a', 64));
        var block = new RollingCommittedBlock
        {
            Sequence = 1,
            BlockId = new string('b', 64),
            Seed = 4,
            PlaylistPath = "blocks/one/playlist.json",
            PlaylistSha256 = new string('c', 64),
            DescriptorPath = "blocks/one/block.json",
            DescriptorSha256 = new string('d', 64),
            InputSnapshotPath = "blocks/one/input.json",
            InputSnapshotSha256 = new string('e', 64),
            TargetDurationSeconds = targetDurationSeconds,
            ActualDurationSeconds = targetDurationSeconds + 5,
            ScheduleStartUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            ScheduleEndUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)
                .AddSeconds(targetDurationSeconds + 5),
            ItemCount = 2,
            HistoryBefore = history,
            HistoryAfter = new RollingArtifactReference(
                $"history/{new string('f', 64)}.json",
                new string('f', 64)),
            CatalogSnapshotHash = new string('1', 64),
            ProgrammingSnapshotHash = new string('2', 64),
            InventorySnapshotHash = new string('3', 64),
            ProgrammingSchemaVersion = 1,
            ProgrammingRevision = 7,
            PlannerAlgorithmVersion = RollingProgrammingPolicy.PlannerAlgorithmVersion,
            GeneratedAtUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        };
        return new RollingProgrammingManifest
        {
            PlannerId = "00112233445566778899aabbccddeeff",
            BaseSeed = 10,
            TargetBlockDurationSeconds = targetDurationSeconds,
            TargetPreparedBlockCount = 3,
            NextSequence = 2,
            GenesisHistory = history,
            HistoryHead = block.HistoryAfter,
            Blocks = [block],
            InitializedAtUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        };
    }
}
