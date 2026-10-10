using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using NzyteTv.Dashboard.Configuration;
using NzyteTv.Dashboard.Http;
using NzyteTv.Dashboard.Operations;
using NzyteTv.Dashboard.Status;
using NzyteTv.Media;
using NzyteTv.Operations.Contracts;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
DashboardHostConfiguration.RejectAlternativeListeners(builder.Configuration);
DashboardOptions dashboardOptions = DashboardOptions.Load(builder.Configuration);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Configure(builder.Configuration.GetSection("Kestrel"), reloadOnChange: false);
    options.Listen(IPAddress.Loopback, dashboardOptions.Port);
});

builder.Services.AddRazorPages();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "NzyteTvDashboardAntiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.None;
    options.HeaderName = "X-NZYTE-TV-CSRF";
});
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
builder.Services.AddSingleton<IOperationsHelperClient, OperationsHelperClient>();

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
    bool spotlightMutation = HttpMethods.IsPost(context.Request.Method)
        && (context.Request.Path.Equals("/api/v1/programming/spotlight")
            || context.Request.Path.Equals("/api/v1/programming/spotlight/disable"));
    bool mediaMutation = HttpMethods.IsPost(context.Request.Method)
        && context.Request.Path.Equals("/api/v1/media-library/refresh");
    if (!HttpMethods.IsGet(context.Request.Method) && !spotlightMutation && !mediaMutation)
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

app.MapGet("/api/v1/programming/spotlight", async (
    HttpContext context,
    IOperationsHelperClient client,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return await ExecuteHelperAsync(
        () => client.GetSpotlightAsync(cancellationToken)).ConfigureAwait(false);
});

app.MapPost("/api/v1/programming/spotlight", async (
    HttpContext context,
    IAntiforgery antiforgery,
    IOperationsHelperClient client,
    CancellationToken cancellationToken) => await ExecuteMutationAsync(
    context,
    antiforgery,
    async () =>
    {
        SetSpotlightRequest request = await StrictJsonRequestReader.ReadAsync<SetSpotlightRequest>(
            context.Request,
            cancellationToken).ConfigureAwait(false);
        return await client.SetSpotlightAsync(request, cancellationToken).ConfigureAwait(false);
    }).ConfigureAwait(false));

app.MapPost("/api/v1/programming/spotlight/disable", async (
    HttpContext context,
    IAntiforgery antiforgery,
    IOperationsHelperClient client,
    CancellationToken cancellationToken) => await ExecuteMutationAsync(
    context,
    antiforgery,
    async () =>
    {
        DisableSpotlightRequest request = await StrictJsonRequestReader.ReadAsync<DisableSpotlightRequest>(
            context.Request,
            cancellationToken).ConfigureAwait(false);
        return await client.DisableSpotlightAsync(request, cancellationToken).ConfigureAwait(false);
    }).ConfigureAwait(false));

app.MapGet("/api/v1/media-library", async (
    HttpContext context,
    IOperationsHelperClient client,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return await ExecuteHelperAsync(
        () => client.GetMediaLibraryAsync(cancellationToken)).ConfigureAwait(false);
});

app.MapPost("/api/v1/media-library/refresh", async (
    HttpContext context,
    IAntiforgery antiforgery,
    IOperationsHelperClient client,
    CancellationToken cancellationToken) => await ExecuteMutationAsync(
    context,
    antiforgery,
    async () =>
    {
        MediaLibraryRefreshRequest request = await StrictJsonRequestReader
            .ReadAsync<MediaLibraryRefreshRequest>(
                context.Request,
                cancellationToken,
                OperationsProtocol.MaximumMediaRefreshRequestBytes)
            .ConfigureAwait(false);
        return await client.StartMediaLibraryRefreshAsync(request, cancellationToken).ConfigureAwait(false);
    },
    accepted => Results.Accepted(
        $"/api/v1/media-library/operations/{accepted.OperationId}", accepted)).ConfigureAwait(false));

app.MapGet("/api/v1/media-library/operations/{operationId}", async (
    HttpContext context,
    string operationId,
    IOperationsHelperClient client,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return await ExecuteHelperAsync(
        () => client.GetMediaLibraryOperationAsync(operationId, cancellationToken)).ConfigureAwait(false);
});

app.MapRazorPages();

app.Run();

static async Task<IResult> ExecuteMutationAsync<T>(
    HttpContext context,
    IAntiforgery antiforgery,
    Func<Task<T>> action,
    Func<T, IResult>? success = null)
{
    context.Response.Headers.CacheControl = "no-store";
    try
    {
        await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        T response = await action().ConfigureAwait(false);
        return success?.Invoke(response) ?? Results.Ok(response);
    }
    catch (AntiforgeryValidationException)
    {
        return Error(StatusCodes.Status400BadRequest, OperationsErrorCodes.ValidationFailed);
    }
    catch (DashboardUnsupportedContentTypeException)
    {
        return Error(StatusCodes.Status415UnsupportedMediaType, OperationsErrorCodes.UnsupportedContentType);
    }
    catch (DashboardRequestTooLargeException)
    {
        return Error(StatusCodes.Status413PayloadTooLarge, OperationsErrorCodes.RequestTooLarge);
    }
    catch (DashboardMalformedRequestException)
    {
        return Error(StatusCodes.Status400BadRequest, OperationsErrorCodes.MalformedRequest);
    }
    catch (OperationsHelperException exception)
    {
        return MapHelperError(exception);
    }
}

static async Task<IResult> ExecuteHelperAsync<T>(Func<Task<T>> action)
{
    try
    {
        return Results.Ok(await action().ConfigureAwait(false));
    }
    catch (OperationsHelperException exception)
    {
        return MapHelperError(exception);
    }
}

static IResult MapHelperError(OperationsHelperException exception) => exception.Code switch
{
    OperationsErrorCodes.StaleRevision =>
        Error(StatusCodes.Status409Conflict, OperationsErrorCodes.StaleRevision),
    OperationsErrorCodes.ValidationFailed =>
        Error(StatusCodes.Status400BadRequest, OperationsErrorCodes.ValidationFailed),
    OperationsErrorCodes.MalformedRequest =>
        Error(StatusCodes.Status400BadRequest, OperationsErrorCodes.MalformedRequest),
    OperationsErrorCodes.InvalidOperationId =>
        Error(StatusCodes.Status400BadRequest, OperationsErrorCodes.InvalidOperationId),
    OperationsErrorCodes.OperationNotFound =>
        Error(StatusCodes.Status404NotFound, OperationsErrorCodes.OperationNotFound),
    OperationsErrorCodes.RefreshBusy =>
        Error(StatusCodes.Status409Conflict, OperationsErrorCodes.RefreshBusy),
    OperationsErrorCodes.FeatureDisabled =>
        Error(StatusCodes.Status503ServiceUnavailable, OperationsErrorCodes.FeatureDisabled),
    OperationsErrorCodes.FeatureUnavailable =>
        Error(StatusCodes.Status503ServiceUnavailable, OperationsErrorCodes.FeatureUnavailable),
    _ => Error(StatusCodes.Status503ServiceUnavailable, OperationsErrorCodes.HelperUnavailable),
};

static IResult Error(int statusCode, string code) => Results.Json(
    new OperationsErrorResponse(OperationsProtocol.SchemaVersion, code),
    statusCode: statusCode);

public partial class Program;
