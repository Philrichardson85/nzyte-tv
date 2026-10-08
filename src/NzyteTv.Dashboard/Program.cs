using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzyteTv.Dashboard.Configuration;
using NzyteTv.Dashboard.Status;
using NzyteTv.Media;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
DashboardHostConfiguration.RejectAlternativeListeners(builder.Configuration);
DashboardOptions dashboardOptions = DashboardOptions.Load(builder.Configuration);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Configure(builder.Configuration.GetSection("Kestrel"), reloadOnChange: false);
    options.Listen(IPAddress.Loopback, dashboardOptions.Port);
});

builder.Services.AddRazorPages();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
string dashboardVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? "unknown";
builder.Services.AddSingleton(dashboardOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new DashboardRuntimeInfo(dashboardVersion, startedAtUtc));
builder.Services.AddSingleton<IProcessExistence, ProcessExistence>();
builder.Services.AddSingleton<IDashboardStateReader, DashboardStateReader>();
builder.Services.AddSingleton<IDashboardStatusSnapshotSource, DashboardStatusProvider>();
builder.Services.AddSingleton<DashboardStatusCache>();
builder.Services.AddSingleton<IDashboardStatusProvider>(services =>
    services.GetRequiredService<DashboardStatusCache>());
builder.Services.AddHostedService(services =>
    services.GetRequiredService<DashboardStatusCache>());

WebApplication app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self'; " +
            "img-src 'self' data:; connect-src 'self'; object-src 'none'; " +
            "base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        return Task.CompletedTask;
    });
    if (!HttpMethods.IsGet(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers.Allow = "GET";
        return;
    }

    await next().ConfigureAwait(false);
});

app.UseStaticFiles();
app.UseRouting();

app.MapGet("/api/v1/status", (HttpContext context, IDashboardStatusProvider provider) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(provider.GetStatus());
});

app.MapGet("/healthz", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Text("dashboard-ok", "text/plain");
});

app.MapRazorPages();

app.Run();

public partial class Program;
