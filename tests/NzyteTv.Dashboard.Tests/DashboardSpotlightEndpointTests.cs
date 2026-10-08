using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NzyteTv.Dashboard.Operations;
using NzyteTv.Operations.Contracts;

namespace NzyteTv.Dashboard.Tests;

public sealed partial class DashboardSpotlightEndpointTests
{
    [Fact]
    public async Task GetReturnsAllowlistedNoStoreSpotlightState()
    {
        await using var factory = CreateFactory(new FakeOperationsHelperClient());
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/v1/programming/spotlight");
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            ["artist", "catalogOptions", "contentGroupId", "enabled", "revision", "schemaVersion", "title", "weightMultiplier"],
            document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(["artist", "contentGroupId", "title"],
            document.RootElement.GetProperty("catalogOptions")[0].EnumerateObject()
                .Select(property => property.Name).Order().ToArray());
        string[] forbidden = ["path", "\"pid\"", "planner", "queue", "claim", "hash", "stderr", "diagnostic", "rtmp"];
        foreach (string value in forbidden) Assert.DoesNotContain(value, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpotlightPostRequiresValidAntiforgeryToken()
    {
        await using var factory = CreateFactory(new FakeOperationsHelperClient());
        using HttpClient client = factory.CreateClient(new() { HandleCookies = true });
        var body = new SetSpotlightRequest { ExpectedRevision = 5, ContentGroupId = "purple-rain", WeightMultiplier = 2.0 };
        using HttpResponseMessage missing = await client.PostAsJsonAsync("/api/v1/programming/spotlight", body);
        using var invalidRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/programming/spotlight")
        { Content = JsonContent.Create(body) };
        invalidRequest.Headers.Add("X-NZYTE-TV-CSRF", "invalid");
        using HttpResponseMessage invalid = await client.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task ValidAntiforgeryTokenAllowsSetAndDisable()
    {
        var helper = new FakeOperationsHelperClient();
        await using var factory = CreateFactory(helper);
        using HttpClient client = factory.CreateClient(new() { HandleCookies = true });
        string token = await GetTokenAsync(client);
        using HttpResponseMessage set = await PostAsync(client, "/api/v1/programming/spotlight",
            new SetSpotlightRequest { ExpectedRevision = 5, ContentGroupId = "free-fallin", WeightMultiplier = 3.0 }, token);
        using HttpResponseMessage disable = await PostAsync(client, "/api/v1/programming/spotlight/disable",
            new DisableSpotlightRequest { ExpectedRevision = 6 }, token);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.False(helper.State.Enabled);
        Assert.Equal(7, helper.State.Revision);
    }

    [Fact]
    public async Task InvalidContentTypeAndOversizedBodyAreRejected()
    {
        await using var factory = CreateFactory(new FakeOperationsHelperClient());
        using HttpClient client = factory.CreateClient(new() { HandleCookies = true });
        string token = await GetTokenAsync(client);
        using var textRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/programming/spotlight")
        { Content = new StringContent("{}", Encoding.UTF8, "text/plain") };
        textRequest.Headers.Add("X-NZYTE-TV-CSRF", token);
        using HttpResponseMessage wrongType = await client.SendAsync(textRequest);
        using var largeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/programming/spotlight")
        { Content = new StringContent(new string('x', 5000), Encoding.UTF8, "application/json") };
        largeRequest.Headers.Add("X-NZYTE-TV-CSRF", token);
        using HttpResponseMessage oversized = await client.SendAsync(largeRequest);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
    }

    [Fact]
    public async Task StaleRevisionMapsToConflict()
    {
        var helper = new FakeOperationsHelperClient
        { Exception = new OperationsHelperException(OperationsErrorCodes.StaleRevision, 409) };
        await using var factory = CreateFactory(helper);
        using HttpClient client = factory.CreateClient(new() { HandleCookies = true });
        string token = await GetTokenAsync(client);
        using HttpResponseMessage response = await PostAsync(client, "/api/v1/programming/spotlight",
            new SetSpotlightRequest { ExpectedRevision = 4, ContentGroupId = "purple-rain", WeightMultiplier = 2.0 }, token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task HelperUnavailableLeavesStatusAndPageOperational()
    {
        var helper = new FakeOperationsHelperClient
        { Exception = new OperationsHelperException(OperationsErrorCodes.HelperUnavailable) };
        await using var factory = CreateFactory(helper);
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage spotlight = await client.GetAsync("/api/v1/programming/spotlight");
        using HttpResponseMessage status = await client.GetAsync("/api/v1/status");
        using HttpResponseMessage page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, spotlight.StatusCode);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    [Fact]
    public async Task UnknownMutationRouteRemainsUnavailable()
    {
        await using var factory = CreateFactory(new FakeOperationsHelperClient());
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/programming/media-refresh", new { });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task SpotlightRejectsUnapprovedMutationMethods(string method)
    {
        await using var factory = CreateFactory(new FakeOperationsHelperClient());
        using HttpClient client = factory.CreateClient();
        foreach (string path in new[]
        {
            "/api/v1/programming/spotlight",
            "/api/v1/programming/spotlight/disable",
        })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }

    private static DashboardWebApplicationFactory CreateFactory(IOperationsHelperClient client) =>
        new(new FixedDashboardStatusProvider(DashboardSnapshotFactory.Create()), client);

    private static async Task<string> GetTokenAsync(HttpClient client)
    {
        string html = await client.GetStringAsync("/");
        Match match = AntiforgeryTokenPattern().Match(html);
        return Assert.Single(match.Groups[1].Captures).Value;
    }

    private static async Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string path, T body, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-NZYTE-TV-CSRF", token);
        return await client.SendAsync(request);
    }

    [GeneratedRegex("data-antiforgery-token=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenPattern();
}
