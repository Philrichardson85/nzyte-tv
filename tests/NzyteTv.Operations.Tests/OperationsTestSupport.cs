using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NzyteTv.Operations.Contracts;
using NzyteTv.Operations.Spotlight;

namespace NzyteTv.Operations.Tests;

internal sealed class OperationsWebApplicationFactory(ISpotlightOperationsService service)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISpotlightOperationsService>();
            services.AddSingleton(service);
        });
    }
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
