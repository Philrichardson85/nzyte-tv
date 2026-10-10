using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using NzyteTv.Dashboard.Operations;
using NzyteTv.Operations.Contracts;

namespace NzyteTv.Dashboard.Tests;

public sealed partial class DashboardMediaLibraryEndpointTests
{
    [Fact]
    public async Task GetSummaryAndOperationProxySafeContracts()
    {
        var helper = new FakeOperationsHelperClient();
        await using var factory = CreateFactory(helper);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage summary = await client.GetAsync("/api/v1/media-library");
        using HttpResponseMessage operation = await client.GetAsync(
            $"/api/v1/media-library/operations/{helper.MediaOperation.OperationId}");

        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        Assert.Equal(HttpStatusCode.OK, operation.StatusCode);
        Assert.Contains("no-store", summary.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("/srv/", await operation.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshRequiresAntiforgeryAndReturnsAccepted()
    {
        var helper = new FakeOperationsHelperClient();
        await using var factory = CreateFactory(helper);
        using HttpClient client = factory.CreateClient(new() { HandleCookies = true });
        var body = new MediaLibraryRefreshRequest { SchemaVersion = 1, ExpectedMetadataRevision = 7 };

        using HttpResponseMessage missing = await client.PostAsJsonAsync("/api/v1/media-library/refresh", body);
        string token = await GetTokenAsync(client);
        using HttpResponseMessage accepted = await PostAsync(client, body, token);

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.EndsWith(helper.MediaOperation.OperationId, accepted.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OperationsErrorCodes.StaleRevision, HttpStatusCode.Conflict)]
    [InlineData(OperationsErrorCodes.RefreshBusy, HttpStatusCode.Conflict)]
    [InlineData(OperationsErrorCodes.OperationNotFound, HttpStatusCode.NotFound)]
    [InlineData(OperationsErrorCodes.InvalidOperationId, HttpStatusCode.BadRequest)]
    [InlineData(OperationsErrorCodes.FeatureDisabled, HttpStatusCode.ServiceUnavailable)]
    [InlineData(OperationsErrorCodes.FeatureUnavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task HelperCodesMapWithoutLeakingDetails(string code, HttpStatusCode expected)
    {
        var helper = new FakeOperationsHelperClient
        {
            Exception = new OperationsHelperException(code, innerException: new IOException(
                "C:\\secret\\media /srv/private/path environment=hidden stack trace")),
        };
        await using var factory = CreateFactory(helper);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/media-library");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expected, response.StatusCode);
        Assert.Contains(code, body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/srv/", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshRejectsWrongTypeDuplicateUnknownMalformedAndOversizedBodies()
    {
        await using var factory = CreateFactory(new FakeOperationsHelperClient());
        using HttpClient client = factory.CreateClient(new() { HandleCookies = true });
        string token = await GetTokenAsync(client);
        var cases = new (string ContentType, string Body, HttpStatusCode Status)[]
        {
            ("text/plain", "{}", HttpStatusCode.UnsupportedMediaType),
            ("application/json", "not-json", HttpStatusCode.BadRequest),
            ("application/json", "{}", HttpStatusCode.BadRequest),
            ("application/json", "{\"schemaVersion\":2,\"expectedMetadataRevision\":7}", HttpStatusCode.BadRequest),
            ("application/json", "{\"schemaVersion\":1,\"expectedMetadataRevision\":0}", HttpStatusCode.BadRequest),
            ("application/json", "{\"schemaVersion\":1,\"schemaVersion\":1,\"expectedMetadataRevision\":7}", HttpStatusCode.BadRequest),
            ("application/json", "{\"schemaVersion\":1,\"expectedMetadataRevision\":7,\"path\":\"/tmp/x\"}", HttpStatusCode.BadRequest),
            ("application/json", new string('x', 1100), HttpStatusCode.RequestEntityTooLarge),
        };

        foreach ((string contentType, string body, HttpStatusCode status) in cases)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media-library/refresh")
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            };
            request.Headers.Add("X-NZYTE-TV-CSRF", token);
            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.Equal(status, response.StatusCode);
        }
    }

    [Fact]
    public async Task HelperUnavailableDoesNotBreakStationPageOrStatus()
    {
        var helper = new FakeOperationsHelperClient
        {
            Exception = new OperationsHelperException(OperationsErrorCodes.HelperUnavailable),
        };
        await using var factory = CreateFactory(helper);
        using HttpClient client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.GetAsync("/api/v1/media-library")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task BootstrapHasNoDashboardRoute()
    {
        await using var factory = CreateFactory(new FakeOperationsHelperClient());
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/media-library/bootstrap", new { });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static DashboardWebApplicationFactory CreateFactory(IOperationsHelperClient client) =>
        new(new FixedDashboardStatusProvider(DashboardSnapshotFactory.Create()), client);

    private static async Task<string> GetTokenAsync(HttpClient client)
    {
        string html = await client.GetStringAsync("/");
        Match match = AntiforgeryTokenPattern().Match(html);
        return Assert.Single(match.Groups[1].Captures).Value;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, MediaLibraryRefreshRequest body, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media-library/refresh")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-NZYTE-TV-CSRF", token);
        return await client.SendAsync(request);
    }

    [GeneratedRegex("data-antiforgery-token=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenPattern();
}
