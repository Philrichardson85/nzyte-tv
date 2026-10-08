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

    private Task<SpotlightStateResponse> Result() => Exception is null
        ? Task.FromResult(State)
        : Task.FromException<SpotlightStateResponse>(Exception);
}
