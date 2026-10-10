using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NzyteTv.Operations.Contracts;
using NzyteTv.Operations.MediaLibrary;

namespace NzyteTv.Operations.Tests;

public sealed class MediaLibraryEndpointTests
{
    [Fact]
    public async Task OldB1ConfigurationStartsWithDisabledMediaAndWorkingSpotlight()
    {
        await using var factory = new OperationsWebApplicationFactory(new FakeSpotlightOperationsService());
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage media = await client.GetAsync("/api/v1/media-library");
        using HttpResponseMessage spotlight = await client.GetAsync("/api/v1/spotlight");
        string body = await media.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, media.StatusCode);
        Assert.Contains("\"featureState\":\"disabled\"", body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, spotlight.StatusCode);
        using HttpResponseMessage spotlightUpdate = await client.PostAsJsonAsync(
            "/api/v1/spotlight",
            new SetSpotlightRequest
            {
                ExpectedRevision = 4,
                ContentGroupId = "purple-rain",
                WeightMultiplier = 2.0,
            });
        Assert.Equal(HttpStatusCode.OK, spotlightUpdate.StatusCode);
        using HttpResponseMessage refresh = await client.PostAsJsonAsync(
            "/api/v1/media-library/refresh",
            new MediaLibraryRefreshRequest { SchemaVersion = 1, ExpectedMetadataRevision = 1 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refresh.StatusCode);
    }

    [Fact]
    public async Task SummaryAndOperationReturnAllowlistedNoStoreContracts()
    {
        var media = new FakeMediaLibraryOperationCoordinator();
        await using var factory = new OperationsWebApplicationFactory(
            new FakeSpotlightOperationsService(), media);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage summary = await client.GetAsync("/api/v1/media-library");
        using HttpResponseMessage operation = await client.GetAsync(
            $"/api/v1/media-library/operations/{media.Operation.OperationId}");
        string json = await summary.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        Assert.Equal(HttpStatusCode.OK, operation.StatusCode);
        Assert.Contains("no-store", summary.Headers.CacheControl?.ToString());
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(
            ["featureState", "generationId", "metadataRevision", "schemaVersion"],
            document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        AssertSafe(json);
    }

    [Fact]
    public async Task ValidRefreshReturnsAcceptedOperationLocation()
    {
        var media = new FakeMediaLibraryOperationCoordinator();
        await using var factory = new OperationsWebApplicationFactory(
            new FakeSpotlightOperationsService(), media);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/media-library/refresh",
            new MediaLibraryRefreshRequest { SchemaVersion = 1, ExpectedMetadataRevision = 5 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.EndsWith(media.Operation.OperationId, response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OperationsErrorCodes.StaleRevision, HttpStatusCode.Conflict)]
    [InlineData(OperationsErrorCodes.RefreshBusy, HttpStatusCode.Conflict)]
    [InlineData(OperationsErrorCodes.OperationNotFound, HttpStatusCode.NotFound)]
    [InlineData(OperationsErrorCodes.InvalidOperationId, HttpStatusCode.BadRequest)]
    [InlineData(OperationsErrorCodes.FeatureDisabled, HttpStatusCode.ServiceUnavailable)]
    [InlineData(OperationsErrorCodes.FeatureUnavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task FixedCoordinatorErrorsMapDeterministically(string code, HttpStatusCode status)
    {
        var media = new FakeMediaLibraryOperationCoordinator
        {
            Exception = new MediaLibraryOperationException(code),
        };
        await using var factory = new OperationsWebApplicationFactory(
            new FakeSpotlightOperationsService(), media);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/media-library");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(status, response.StatusCode);
        Assert.Contains(code, body, StringComparison.Ordinal);
        AssertSafe(body);
    }

    [Theory]
    [InlineData("text/plain", "{}", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("application/json", "not-json", HttpStatusCode.BadRequest)]
    [InlineData("application/json", "{}", HttpStatusCode.BadRequest)]
    [InlineData("application/json", "{\"schemaVersion\":2,\"expectedMetadataRevision\":5}", HttpStatusCode.BadRequest)]
    [InlineData("application/json", "{\"schemaVersion\":1,\"expectedMetadataRevision\":0}", HttpStatusCode.BadRequest)]
    [InlineData("application/json", "{\"schemaVersion\":1,\"expectedMetadataRevision\":5,\"path\":\"/tmp/x\"}", HttpStatusCode.BadRequest)]
    [InlineData("application/json", "{\"schemaVersion\":1,\"schemaVersion\":1,\"expectedMetadataRevision\":5}", HttpStatusCode.BadRequest)]
    public async Task RefreshBodyIsStrict(string contentType, string body, HttpStatusCode status)
    {
        await using var factory = new OperationsWebApplicationFactory(
            new FakeSpotlightOperationsService(), new FakeMediaLibraryOperationCoordinator());
        using HttpClient client = factory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, contentType);

        using HttpResponseMessage response = await client.PostAsync("/api/v1/media-library/refresh", content);

        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task RefreshBodyLimitIsOneKiB()
    {
        await using var factory = new OperationsWebApplicationFactory(
            new FakeSpotlightOperationsService(), new FakeMediaLibraryOperationCoordinator());
        using HttpClient client = factory.CreateClient();
        using var content = new StringContent(new string('x', 1100), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/api/v1/media-library/refresh", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task BootstrapAndArbitraryMediaRoutesAreNotMapped()
    {
        await using var factory = new OperationsWebApplicationFactory(
            new FakeSpotlightOperationsService(), new FakeMediaLibraryOperationCoordinator());
        using HttpClient client = factory.CreateClient();
        foreach (string path in new[]
        {
            "/api/v1/media-library/bootstrap",
            "/api/v1/media-library/migrate",
            "/api/v1/media-library/files",
        })
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(path, new { });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    private static void AssertSafe(string value)
    {
        foreach (string forbidden in new[]
        {
            "C:\\secret\\media", "/srv/private/path", "RTMP", "stack trace", "environment=",
        })
        {
            Assert.DoesNotContain(forbidden, value, StringComparison.OrdinalIgnoreCase);
        }
    }
}
