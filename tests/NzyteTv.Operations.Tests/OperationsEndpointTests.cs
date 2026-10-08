using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NzyteTv.Operations.Contracts;

namespace NzyteTv.Operations.Tests;

public sealed class OperationsEndpointTests
{
    [Fact]
    public async Task GetSpotlightReturnsOnlyAllowlistedState()
    {
        await using var factory = new OperationsWebApplicationFactory(new FakeSpotlightOperationsService());
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/spotlight");
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            ["artist", "catalogOptions", "contentGroupId", "enabled", "revision", "schemaVersion", "title", "weightMultiplier"],
            document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rtmp", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllowlistedMutationsAcceptStrictJson()
    {
        await using var factory = new OperationsWebApplicationFactory(new FakeSpotlightOperationsService());
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage set = await client.PostAsJsonAsync(
            "/api/v1/spotlight",
            new SetSpotlightRequest { ExpectedRevision = 4, ContentGroupId = "purple-rain", WeightMultiplier = 3.0 });
        using HttpResponseMessage disable = await client.PostAsJsonAsync(
            "/api/v1/spotlight/disable",
            new DisableSpotlightRequest { ExpectedRevision = 5 });

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/execute")]
    [InlineData("/api/v1/systemd")]
    [InlineData("/api/v1/media-refresh")]
    public async Task UnknownOperationsAreNotMapped(string path)
    {
        await using var factory = new OperationsWebApplicationFactory(new FakeSpotlightOperationsService());
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(path, JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownPathOrCommandFieldsAreRejected()
    {
        await using var factory = new OperationsWebApplicationFactory(new FakeSpotlightOperationsService());
        using HttpClient client = factory.CreateClient();
        using var content = new StringContent(
            "{\"expectedRevision\":4,\"contentGroupId\":\"purple-rain\",\"weightMultiplier\":2.0,\"path\":\"/tmp/x\",\"command\":\"systemctl\"}",
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.PostAsync("/api/v1/spotlight", content);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(OperationsErrorCodes.MalformedRequest, body, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/x", body, StringComparison.Ordinal);
        Assert.DoesNotContain("systemctl", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-json", "application/json", HttpStatusCode.BadRequest, OperationsErrorCodes.MalformedRequest)]
    [InlineData("{}", "text/plain", HttpStatusCode.UnsupportedMediaType, OperationsErrorCodes.UnsupportedContentType)]
    public async Task InvalidRequestsReturnFixedCodes(
        string body, string contentType, HttpStatusCode expectedStatus, string expectedCode)
    {
        await using var factory = new OperationsWebApplicationFactory(new FakeSpotlightOperationsService());
        using HttpClient client = factory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, contentType);
        using HttpResponseMessage response = await client.PostAsync("/api/v1/spotlight", content);
        string responseBody = await response.Content.ReadAsStringAsync();
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Contains(expectedCode, responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedRequestIsRejected()
    {
        await using var factory = new OperationsWebApplicationFactory(new FakeSpotlightOperationsService());
        using HttpClient client = factory.CreateClient();
        string body = "{\"expectedRevision\":4,\"contentGroupId\":\"" + new string('x', 5000) + "\",\"weightMultiplier\":2}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/api/v1/spotlight", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task UnexpectedExceptionDoesNotLeakText()
    {
        const string secret = "rtmps://example.invalid/live/DO-NOT-LEAK";
        await using var factory = new OperationsWebApplicationFactory(
            new FakeSpotlightOperationsService { Exception = new IOException(secret) });
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/v1/spotlight");
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(OperationsErrorCodes.InternalError, body, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
    }
}
