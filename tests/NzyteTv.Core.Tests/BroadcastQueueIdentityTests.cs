using NzyteTv.Core;

namespace NzyteTv.Core.Tests;

public sealed class BroadcastQueueIdentityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("nzytetv-queue-id-").FullName;

    [Fact]
    public void Create_SameOrderedQueueProducesSameIdentity()
    {
        BroadcastPlan first = Plan();
        BroadcastPlan second = Plan();

        string firstId = BroadcastQueueIdentity.Create(first);
        string secondId = BroadcastQueueIdentity.Create(second);

        Assert.Equal(firstId, secondId);
        Assert.Matches("^[0-9a-f]{64}$", firstId);
    }

    [Fact]
    public void Create_ChangedPlaylistContentChangesIdentity()
    {
        BroadcastPlan original = Plan();
        BroadcastPlan changed = original with
        {
            PlaylistContentHashes = [Hash('c'), Hash('b')],
        };

        Assert.NotEqual(
            BroadcastQueueIdentity.Create(original),
            BroadcastQueueIdentity.Create(changed));
    }

    [Fact]
    public void Create_ChangedPlaylistOrderingChangesIdentity()
    {
        BroadcastPlan original = Plan();
        BroadcastPlan reversed = new(
            original.LibraryRoot,
            original.PlaylistPaths.Reverse().ToArray(),
            original.Items.Reverse().ToArray(),
            [],
            original.ScheduledItemCount,
            original.ScheduledDurationSeconds)
        {
            PlaylistContentHashes = original.PlaylistContentHashes.Reverse().ToArray(),
        };

        Assert.NotEqual(
            BroadcastQueueIdentity.Create(original),
            BroadcastQueueIdentity.Create(reversed));
    }

    [Fact]
    public void Create_MaterialItemChangeChangesIdentity()
    {
        BroadcastPlan original = Plan();
        BroadcastPlan changed = original with
        {
            Items =
            [
                original.Items[0] with { DurationSeconds = 31 },
                original.Items[1],
            ],
        };

        Assert.NotEqual(
            BroadcastQueueIdentity.Create(original),
            BroadcastQueueIdentity.Create(changed));
    }

    [Fact]
    public void Create_DoesNotDependOnFileTimestampsPidOrDestination()
    {
        BroadcastPlan plan = Plan();
        string first = BroadcastQueueIdentity.Create(plan);
        foreach (string playlistPath in plan.PlaylistPaths)
        {
            File.SetLastWriteTimeUtc(playlistPath, DateTime.UtcNow.AddDays(10));
        }

        var unrelatedRuntimeState = new StationRuntimeState
        {
            StationPid = int.MaxValue,
            LastError = "rtmps://example.invalid/live2/EXAMPLE-KEY",
        };

        string second = BroadcastQueueIdentity.Create(plan);

        Assert.Equal(first, second);
        Assert.DoesNotContain("rtmp", second, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(unrelatedRuntimeState.StationPid.ToString(), second);
    }

    private BroadcastPlan Plan()
    {
        string library = Directory.CreateDirectory(Path.Combine(_root, "library")).FullName;
        string firstPlaylist = Path.Combine(_root, "first.json");
        string secondPlaylist = Path.Combine(_root, "second.json");
        File.WriteAllText(firstPlaylist, "first");
        File.WriteAllText(secondPlaylist, "second");
        return new BroadcastPlan(
            library,
            [firstPlaylist, secondPlaylist],
            [
                new BroadcastPlanItem(firstPlaylist, 1, "asset-1", "first.mp4", Path.Combine(library, "first.mp4"), 30, "First", "music-video"),
                new BroadcastPlanItem(secondPlaylist, 1, "asset-2", "second.mp4", Path.Combine(library, "second.mp4"), 30, "Second", "animated-visual"),
            ],
            [],
            2,
            60)
        {
            PlaylistContentHashes = [Hash('a'), Hash('b')],
        };
    }

    private static string Hash(char character) => new(character, 64);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
