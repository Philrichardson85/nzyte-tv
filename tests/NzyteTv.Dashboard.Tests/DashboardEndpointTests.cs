using System.Net;
using System.Text.Json;
using NzyteTv.Dashboard.Status;

namespace NzyteTv.Dashboard.Tests;

public sealed class DashboardEndpointTests
{
    [Fact]
    public async Task StatusEndpoint_ReturnsVersionedAllowlistedNoStoreJson()
    {
        await using var factory = new DashboardWebApplicationFactory(
            DashboardSnapshotFactory.Create());
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/status");
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("healthy", document.RootElement.GetProperty("quality").GetString());
        Assert.Equal(
            "running",
            document.RootElement.GetProperty("station").GetProperty("status").GetString());
        AssertPropertyNames(
            document.RootElement,
            "schemaVersion", "observedAtUtc", "quality", "dashboard", "station",
            "broadcast", "rolling", "playback", "issues");
        AssertPropertyNames(
            document.RootElement.GetProperty("dashboard"),
            "version", "uptimeSeconds");
        AssertPropertyNames(
            document.RootElement.GetProperty("station"),
            "availability", "status", "processEvidence", "heartbeatAtUtc",
            "heartbeatAgeSeconds", "sessionStartedAtUtc", "sessionUptimeSeconds");
        AssertPropertyNames(
            document.RootElement.GetProperty("broadcast"),
            "state", "ffmpegState", "recoveryAttempts");
        AssertPropertyNames(
            document.RootElement.GetProperty("rolling"),
            "availability", "phase", "activeBlockSequence", "lastCompletedBlockSequence",
            "nextRequiredBlockSequence", "committedFutureBlockCount", "futureBlockTarget",
            "bufferDeficit", "bufferHealth", "replenishmentHealth");
        AssertPropertyNames(
            document.RootElement.GetProperty("playback"),
            "availability", "title", "itemType", "currentItemNumber", "totalItemCount");

        string[] forbidden =
        [
            "pid", "path", "plannerId", "queueId", "claim", "hash", "environment",
            "diagnostic", "stderr", "lastError", "lastTransitionError", "rtmp://", "rtmps://",
        ];
        foreach (string value in forbidden)
        {
            Assert.DoesNotContain(value, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task MissingStateSnapshot_RemainsAValidSuccessfulResponse()
    {
        DashboardStatusSnapshot unavailable = DashboardStatusSnapshot.CreateUnavailable(
            DashboardStateFixture.Now,
            new DashboardRuntimeInfo("test", DashboardStateFixture.Now),
            DashboardIssueCode.StationStateMissing);
        await using var factory = new DashboardWebApplicationFactory(unavailable);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/status");
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument snapshot = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "unavailable",
            snapshot.RootElement.GetProperty("quality").GetString());
        Assert.Equal(
            "unknown",
            snapshot.RootElement.GetProperty("station").GetProperty("status").GetString());
    }

    [Fact]
    public async Task HealthEndpoint_DescribesOnlyDashboardLiveness()
    {
        await using var factory = new DashboardWebApplicationFactory(
            DashboardSnapshotFactory.Create());
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/healthz");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("dashboard-ok", body);
        Assert.DoesNotContain("station", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("broadcast", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task OperationalMutationMethods_AreNotMapped(string method)
    {
        await using var factory = new DashboardWebApplicationFactory(
            DashboardSnapshotFactory.Create());
        using HttpClient client = factory.CreateClient();
        foreach (string path in new[] { "/", "/api/v1/status", "/healthz" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.True(
                response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound,
                $"Unexpected status for {method} {path}: {response.StatusCode}");
        }
    }

    [Fact]
    public async Task Responses_IncludeRestrictiveSecurityHeaders()
    {
        await using var factory = new DashboardWebApplicationFactory(
            DashboardSnapshotFactory.Create());
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/");
        string csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api/v1/status")]
    [InlineData("/healthz")]
    public async Task HeadRequests_AreConsistentlyRejectedAsGetOnly(string path)
    {
        await using var factory = new DashboardWebApplicationFactory(
            DashboardSnapshotFactory.Create());
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, path);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(["GET"], response.Content.Headers.Allow);
    }

    private static void AssertPropertyNames(JsonElement element, params string[] expected)
    {
        string[] actual = element.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] sortedExpected = expected.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(sortedExpected, actual);
    }
}
