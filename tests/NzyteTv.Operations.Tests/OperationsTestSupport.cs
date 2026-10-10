using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NzyteTv.Operations.Contracts;
using NzyteTv.Operations.MediaLibrary;
using NzyteTv.Operations.Spotlight;

namespace NzyteTv.Operations.Tests;

internal sealed class OperationsWebApplicationFactory(
    ISpotlightOperationsService service,
    IMediaLibraryOperationCoordinator? mediaCoordinator = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISpotlightOperationsService>();
            services.AddSingleton(service);
            if (mediaCoordinator is not null)
            {
                services.RemoveAll<IMediaLibraryOperationCoordinator>();
                services.AddSingleton(mediaCoordinator);
            }
        });
    }
}

internal sealed class FakeMediaLibraryOperationCoordinator : IMediaLibraryOperationCoordinator
{
    public Exception? Exception { get; set; }
    public MediaLibrarySummaryResponse Summary { get; set; } = new(
        OperationsProtocol.SchemaVersion, MediaLibraryFeatureState.Ready, 5, "000000000005", null, null);
    public MediaLibraryOperationResponse Operation { get; set; } = new(
        OperationsProtocol.SchemaVersion, Guid.Empty.ToString("N"), MediaLibraryOperationState.NoChanges,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 5, 5, "000000000005", "000000000005",
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);

    public Task<MediaLibrarySummaryResponse> GetSummaryAsync(CancellationToken cancellationToken) =>
        Result(Summary);

    public Task<MediaLibraryRefreshAcceptedResponse> StartRefreshAsync(
        MediaLibraryRefreshRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SchemaVersion != OperationsProtocol.SchemaVersion
            || request.ExpectedMetadataRevision <= 0)
        {
            return Task.FromException<MediaLibraryRefreshAcceptedResponse>(
                new MediaLibraryOperationException(OperationsErrorCodes.ValidationFailed));
        }
        return Result(new MediaLibraryRefreshAcceptedResponse(
            OperationsProtocol.SchemaVersion, Operation.OperationId));
    }

    public Task<MediaLibraryOperationResponse> GetOperationAsync(
        string operationId,
        CancellationToken cancellationToken) => Result(Operation with { OperationId = operationId });

    private Task<T> Result<T>(T value) => Exception is null
        ? Task.FromResult(value)
        : Task.FromException<T>(Exception);
}

internal sealed class FakeSpotlightOperationsService : ISpotlightOperationsService
{
    public Exception? Exception { get; set; }
    public SpotlightStateResponse State { get; set; } = CreateState();

    public SpotlightStateResponse Get() => Exception is null ? State : throw Exception;

    public Task<SpotlightStateResponse> SetAsync(SetSpotlightRequest request, CancellationToken cancellationToken)
    {
        if (Exception is not null) throw Exception;
        State = State with
        {
            Revision = State.Revision + 1,
            Enabled = true,
            ContentGroupId = request.ContentGroupId,
            Title = "Purple Rain",
            Artist = "Prince",
            WeightMultiplier = request.WeightMultiplier,
        };
        return Task.FromResult(State);
    }

    public Task<SpotlightStateResponse> DisableAsync(DisableSpotlightRequest request, CancellationToken cancellationToken)
    {
        if (Exception is not null) throw Exception;
        State = State with
        {
            Revision = State.Revision + 1,
            Enabled = false,
            ContentGroupId = null,
            Title = null,
            Artist = null,
        };
        return Task.FromResult(State);
    }

    public static SpotlightStateResponse CreateState() => new(
        OperationsProtocol.SchemaVersion,
        4,
        true,
        "purple-rain",
        "Purple Rain",
        "Prince",
        2.0,
        [new SpotlightCatalogOption("purple-rain", "Purple Rain", "Prince")]);
}
