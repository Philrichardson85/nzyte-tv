namespace NzyteTv.Core.Tests;

public sealed class BroadcastRecoveryPolicyTests
{
    [Fact]
    public void Delays_AreBoundedAndFollowTheDocumentedDefaults()
    {
        var policy = new BroadcastRecoveryPolicy();

        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.GetDelay(5));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.GetDelay(99));
        Assert.Equal(TimeSpan.FromMinutes(5), policy.HealthyThreshold);
        Assert.Equal(10, policy.MaxConsecutiveRetries);
    }

    [Fact]
    public void PlaybackPosition_MapsAcrossFlattenedPlaylistBoundaries()
    {
        BroadcastPlanItem[] items =
        [
            new("one.json", 1, "one", "one.mp4", "one.mp4", 10),
            new("one.json", 2, "two", "two.mp4", "two.mp4", 20),
            new("two.json", 1, "three", "three.mp4", "three.mp4", 30),
        ];

        Assert.Equal(0, BroadcastPlaybackPosition.FindItemIndex(items, TimeSpan.Zero));
        Assert.Equal(1, BroadcastPlaybackPosition.FindItemIndex(items, TimeSpan.FromSeconds(10)));
        Assert.Equal(2, BroadcastPlaybackPosition.FindItemIndex(items, TimeSpan.FromSeconds(30)));
        Assert.Equal(3, BroadcastPlaybackPosition.FindItemIndex(items, TimeSpan.FromSeconds(60)));
    }
}
