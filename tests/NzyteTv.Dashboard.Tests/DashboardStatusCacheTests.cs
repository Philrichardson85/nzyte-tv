using Microsoft.Extensions.Logging.Abstractions;
using NzyteTv.Dashboard.Configuration;
using NzyteTv.Dashboard.Status;

namespace NzyteTv.Dashboard.Tests;

public sealed class DashboardStatusCacheTests
{
    [Fact]
    public void RefreshFailure_IsSafeAndLaterRefreshRecovers()
    {
        DashboardStatusSnapshot first = DashboardSnapshotFactory.Create("First title");
        DashboardStatusSnapshot recovered = DashboardSnapshotFactory.Create("Recovered title");
        var source = new SequencedSnapshotSource(
            () => first,
            () => throw new IOException("Internal refresh failure"),
            () => recovered);
        using DashboardStatusCache cache = CreateCache(source);

        cache.Refresh();
        Assert.Same(first, cache.GetStatus());

        cache.Refresh();
        DashboardStatusSnapshot failed = cache.GetStatus();
        Assert.Equal(DashboardSnapshotQuality.Unavailable, failed.Quality);
        Assert.Contains(DashboardIssueCode.SnapshotRefreshFailed, failed.Issues);
        Assert.DoesNotContain("Internal refresh failure", failed.ToString(), StringComparison.Ordinal);

        cache.Refresh();
        Assert.Same(recovered, cache.GetStatus());
        Assert.Equal(3, source.ReadCount);
    }

    [Fact]
    public async Task RepeatedStatusRequests_UseCachedSnapshotWithoutRefreshingSource()
    {
        DashboardStatusSnapshot snapshot = DashboardSnapshotFactory.Create();
        var source = new SequencedSnapshotSource(() => snapshot);
        using DashboardStatusCache cache = CreateCache(source);
        cache.Refresh();
        await using var factory = new DashboardWebApplicationFactory(cache);
        using HttpClient client = factory.CreateClient();

        for (int request = 0; request < 5; request++)
        {
            using HttpResponseMessage response = await client.GetAsync("/api/v1/status");
            response.EnsureSuccessStatusCode();
        }

        Assert.Equal(1, source.ReadCount);
    }

    private static DashboardStatusCache CreateCache(IDashboardStatusSnapshotSource source) => new(
        source,
        new DashboardOptions { StatusRefreshSeconds = 5 },
        new FixedTimeProvider(DashboardStateFixture.Now),
        new DashboardRuntimeInfo("cache-test", DashboardStateFixture.Now.AddHours(-1)),
        NullLogger<DashboardStatusCache>.Instance);

    private sealed class SequencedSnapshotSource(
        params Func<DashboardStatusSnapshot>[] reads) : IDashboardStatusSnapshotSource
    {
        private readonly Queue<Func<DashboardStatusSnapshot>> _reads = new(reads);

        public int ReadCount { get; private set; }

        public DashboardStatusSnapshot ReadStatus()
        {
            ReadCount++;
            return _reads.Dequeue()();
        }
    }
}
