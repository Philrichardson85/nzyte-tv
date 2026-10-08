using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NzyteTv.Dashboard.Status;

namespace NzyteTv.Dashboard.Tests;

internal sealed class DashboardWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IDashboardStatusProvider _statusProvider;

    public DashboardWebApplicationFactory(DashboardStatusSnapshot snapshot)
        : this(new FixedDashboardStatusProvider(snapshot))
    {
    }

    public DashboardWebApplicationFactory(IDashboardStatusProvider statusProvider)
    {
        _statusProvider = statusProvider;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDashboardStatusProvider>();
            services.AddSingleton(_statusProvider);
        });
    }
}
