using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NzyteTv.Dashboard.Operations;
using NzyteTv.Dashboard.Status;
using NzyteTv.Operations.Contracts;

namespace NzyteTv.Dashboard.Tests;

internal sealed class DashboardWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IDashboardStatusProvider _statusProvider;
    private readonly IOperationsHelperClient _operationsClient;

    public DashboardWebApplicationFactory(DashboardStatusSnapshot snapshot)
        : this(new FixedDashboardStatusProvider(snapshot), new FakeOperationsHelperClient())
    {
    }

    public DashboardWebApplicationFactory(
        IDashboardStatusProvider statusProvider,
        IOperationsHelperClient? operationsClient = null)
    {
        _statusProvider = statusProvider;
        _operationsClient = operationsClient ?? new FakeOperationsHelperClient();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDashboardStatusProvider>();
            services.AddSingleton(_statusProvider);
            services.RemoveAll<IOperationsHelperClient>();
            services.AddSingleton(_operationsClient);
        });
    }
}

internal sealed class FakeOperationsHelperClient : IOperationsHelperClient
{
    public Exception? Exception { get; set; }
    public SpotlightStateResponse State { get; set; } = new(
        OperationsProtocol.SchemaVersion, 5, true, "purple-rain", "Purple Rain", "Prince", 2.0,
        [new SpotlightCatalogOption("purple-rain", "Purple Rain", "Prince"),
         new SpotlightCatalogOption("free-fallin", "Free Fallin'", "Tom Petty")]);
    public MediaLibrarySummaryResponse MediaSummary { get; set; } = new(
        OperationsProtocol.SchemaVersion, MediaLibraryFeatureState.Ready, 7, "000000000007", null, null);
    public MediaLibraryOperationResponse MediaOperation { get; set; } = new(
        OperationsProtocol.SchemaVersion, Guid.Empty.ToString("N"), MediaLibraryOperationState.NoChanges,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 7, 7, "000000000007", "000000000007",
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);

    public Task<SpotlightStateResponse> GetSpotlightAsync(CancellationToken cancellationToken) => Result();

    public Task<SpotlightStateResponse> SetSpotlightAsync(SetSpotlightRequest request, CancellationToken cancellationToken)
    {
        if (Exception is not null) return Task.FromException<SpotlightStateResponse>(Exception);
        State = State with
        {
            Revision = State.Revision + 1,
            Enabled = true,
            ContentGroupId = request.ContentGroupId,
            WeightMultiplier = request.WeightMultiplier
        };
        return Task.FromResult(State);
    }

    public Task<SpotlightStateResponse> DisableSpotlightAsync(DisableSpotlightRequest request, CancellationToken cancellationToken)
    {
        if (Exception is not null) return Task.FromException<SpotlightStateResponse>(Exception);
        State = State with
        {
            Revision = State.Revision + 1,
            Enabled = false,
            ContentGroupId = null,
            Title = null,
            Artist = null
        };
        return Task.FromResult(State);
    }

    public Task<MediaLibrarySummaryResponse> GetMediaLibraryAsync(CancellationToken cancellationToken) =>
        Exception is null
            ? Task.FromResult(MediaSummary)
            : Task.FromException<MediaLibrarySummaryResponse>(Exception);

    public Task<MediaLibraryRefreshAcceptedResponse> StartMediaLibraryRefreshAsync(
        MediaLibraryRefreshRequest request,
        CancellationToken cancellationToken)
    {
        if (Exception is not null) return Task.FromException<MediaLibraryRefreshAcceptedResponse>(Exception);
        if (request.SchemaVersion != OperationsProtocol.SchemaVersion
            || request.ExpectedMetadataRevision <= 0)
        {
            return Task.FromException<MediaLibraryRefreshAcceptedResponse>(
                new OperationsHelperException(OperationsErrorCodes.ValidationFailed, 400));
        }
        return Task.FromResult(new MediaLibraryRefreshAcceptedResponse(
            OperationsProtocol.SchemaVersion, MediaOperation.OperationId));
    }

    public Task<MediaLibraryOperationResponse> GetMediaLibraryOperationAsync(
        string operationId,
        CancellationToken cancellationToken) => Exception is null
            ? Task.FromResult(MediaOperation with { OperationId = operationId })
            : Task.FromException<MediaLibraryOperationResponse>(Exception);

    private Task<SpotlightStateResponse> Result() => Exception is null
        ? Task.FromResult(State)
        : Task.FromException<SpotlightStateResponse>(Exception);
}
