using NzyteTv.Core;

namespace NzyteTv.Core.Tests;

public sealed class RollingProgrammingIdentityTests
{
    [Fact]
    public void SeedDerivation_HasFixedGoldenVector()
    {
        int seed = RollingProgrammingSeed.Derive(
            "00112233445566778899aabbccddeeff",
            123456789,
            42);

        Assert.Equal(-236084376, seed);
    }

    [Fact]
    public void SeedDerivation_ChangesBySequenceAndRepeatsExactly()
    {
        const string planner = "00112233445566778899aabbccddeeff";

        int first = RollingProgrammingSeed.Derive(planner, -27, 1);
        int retry = RollingProgrammingSeed.Derive(planner, -27, 1);
        int second = RollingProgrammingSeed.Derive(planner, -27, 2);

        Assert.Equal(first, retry);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BlockIdentity_IsDeterministicAndIgnoresGenerationTimestamp()
    {
        RollingBlockIdentityInput first = CreateInput();
        RollingBlockIdentityInput second = first with
        {
            Playlist = ClonePlaylist(
                first.Playlist,
                generatedAtUtc: first.Playlist.GeneratedAtUtc.AddDays(10)),
        };

        Assert.Equal(
            RollingBlockIdentity.Calculate(first),
            RollingBlockIdentity.Calculate(second));
    }

    [Fact]
    public void BlockIdentity_UsesOrderedScheduleMaterial()
    {
        RollingBlockIdentityInput first = CreateInput();
        PlaylistDocument changedPlaylist = ClonePlaylist(
            first.Playlist,
            items:
            [
                first.Playlist.Items[0] with { AssetId = "changed" },
                first.Playlist.Items[1],
            ]);

        Assert.NotEqual(
            RollingBlockIdentity.Calculate(first),
            RollingBlockIdentity.Calculate(first with { Playlist = changedPlaylist }));
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("catalog")]
    [InlineData("programming")]
    [InlineData("inventory")]
    [InlineData("history-before")]
    [InlineData("history-after")]
    [InlineData("algorithm")]
    public void BlockIdentity_UsesRelevantChainAndSnapshotInputs(string field)
    {
        RollingBlockIdentityInput input = CreateInput();
        RollingBlockIdentityInput changed = field switch
        {
            "parent" => input with { ParentBlockId = Hash('b') },
            "catalog" => input with { CatalogSnapshotHash = Hash('b') },
            "programming" => input with { ProgrammingSnapshotHash = Hash('b') },
            "inventory" => input with { InventorySnapshotHash = Hash('b') },
            "history-before" => input with { HistoryBeforeHash = Hash('b') },
            "history-after" => input with { HistoryAfterHash = Hash('b') },
            "algorithm" => input with { PlannerAlgorithmVersion = "changed" },
            _ => throw new InvalidOperationException(),
        };

        Assert.NotEqual(
            RollingBlockIdentity.Calculate(input),
            RollingBlockIdentity.Calculate(changed));
    }

    [Fact]
    public void BlockIdentity_DoesNotAcceptAbsolutePublicationPathsAsInput()
    {
        string[] forbidden = ["Path", "Pid", "Destination", "GeneratedAt"];
        Assert.DoesNotContain(
            typeof(RollingBlockIdentityInput).GetProperties(),
            property => forbidden.Any(value => property.Name.Contains(value, StringComparison.Ordinal)));
    }

    private static RollingBlockIdentityInput CreateInput() => new(
        "00112233445566778899aabbccddeeff",
        Sequence: 2,
        ParentBlockId: Hash('a'),
        Seed: 123,
        TargetDurationSeconds: 21600,
        Playlist: new PlaylistDocument
        {
            GeneratedAtUtc = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            ScheduleStartUtc = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            Seed = 123,
            TargetDurationSeconds = 21600,
            ActualDurationSeconds = 21610,
            OverrunSeconds = 10,
            Items =
            [
                new PlaylistItem(1, "a", "song-a", "A", "music-video", null, "Music/A.mp4", 100, 0),
                new PlaylistItem(2, "b", "song-b", "B", "vlog", null, "Vlogs/B.mp4", 200, 100),
            ],
        },
        HistoryBeforeHash: Hash('c'),
        HistoryAfterHash: Hash('d'),
        CatalogSnapshotHash: Hash('e'),
        ProgrammingSnapshotHash: Hash('f'),
        InventorySnapshotHash: Hash('1'),
        PlannerAlgorithmVersion: RollingProgrammingPolicy.PlannerAlgorithmVersion);

    private static PlaylistDocument ClonePlaylist(
        PlaylistDocument source,
        DateTimeOffset? generatedAtUtc = null,
        IReadOnlyList<PlaylistItem>? items = null) => new()
        {
            SchemaVersion = source.SchemaVersion,
            GeneratedAtUtc = generatedAtUtc ?? source.GeneratedAtUtc,
            ScheduleStartUtc = source.ScheduleStartUtc,
            Seed = source.Seed,
            TargetDurationSeconds = source.TargetDurationSeconds,
            ActualDurationSeconds = source.ActualDurationSeconds,
            OverrunSeconds = source.OverrunSeconds,
            Items = items ?? source.Items,
            ExcludedAssets = source.ExcludedAssets,
            Summary = source.Summary,
        };

    private static string Hash(char value) => new(value, 64);
}
