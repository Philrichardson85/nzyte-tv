using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NzyteTv.Media;
using NzyteTv.Operations.Configuration;
using NzyteTv.Operations.Contracts;
using NzyteTv.Operations.Http;
using NzyteTv.Operations.MediaLibrary;
using NzyteTv.Operations.Spotlight;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
OperationsHostConfiguration.RejectTcpListeners(builder.Configuration);
OperationsOptions operationsOptions = OperationsOptions.Load(builder.Configuration);
builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(
    operationsOptions.SocketPath, listen => listen.Protocols = HttpProtocols.Http1));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton(operationsOptions);
builder.Services.AddSingleton<IProgrammingConfigurationStore, ProgrammingConfigurationStore>();
builder.Services.AddSingleton<ISongCatalogStore, SongCatalogStore>();
builder.Services.AddSingleton<IProgrammingInventoryLoader, ProgrammingInventoryLoader>();
builder.Services.AddSingleton<IProgrammingConfigurationMutationLock, ProgrammingConfigurationMutationLock>();
builder.Services.AddSingleton<ProgrammingService>();
builder.Services.AddSingleton<ISpotlightOperationsService, SpotlightOperationsService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IMediaMetadataRefreshLock, MediaMetadataRefreshLock>();
builder.Services.AddSingleton<IMediaLibraryRefreshRunner, MediaLibraryRefreshRunner>();
builder.Services.AddSingleton<MediaLibraryOperationCoordinator>();
builder.Services.AddSingleton<IMediaLibraryOperationCoordinator>(services =>
    services.GetRequiredService<MediaLibraryOperationCoordinator>());
builder.Services.AddHostedService(services =>
    services.GetRequiredService<MediaLibraryOperationCoordinator>());
WebApplication app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsPost(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers.Allow = "GET, POST";
        return;
    }
    await next().ConfigureAwait(false);
});
app.MapGet("/api/v1/spotlight", (ISpotlightOperationsService service) => Execute(service.Get));
app.MapPost("/api/v1/spotlight", async (HttpRequest request, ISpotlightOperationsService service,
    CancellationToken cancellationToken) => await ExecuteAsync(async () =>
    {
        SetSpotlightRequest body = await StrictJsonRequestReader.ReadAsync<SetSpotlightRequest>(request, cancellationToken);
        return await service.SetAsync(body, cancellationToken);
    }));
app.MapPost("/api/v1/spotlight/disable", async (HttpRequest request, ISpotlightOperationsService service,
    CancellationToken cancellationToken) => await ExecuteAsync(async () =>
    {
        DisableSpotlightRequest body = await StrictJsonRequestReader.ReadAsync<DisableSpotlightRequest>(request, cancellationToken);
        return await service.DisableAsync(body, cancellationToken);
    }));
app.MapGet("/api/v1/media-library", async (
    IMediaLibraryOperationCoordinator coordinator,
    CancellationToken cancellationToken) => await ExecuteMediaAsync(
        () => coordinator.GetSummaryAsync(cancellationToken)).ConfigureAwait(false));
app.MapPost("/api/v1/media-library/refresh", async (
    HttpRequest request,
    IMediaLibraryOperationCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    try
    {
        MediaLibraryRefreshRequest body = await StrictJsonRequestReader
            .ReadAsync<MediaLibraryRefreshRequest>(
                request,
                cancellationToken,
                OperationsProtocol.MaximumMediaRefreshRequestBytes)
            .ConfigureAwait(false);
        MediaLibraryRefreshAcceptedResponse accepted = await coordinator
            .StartRefreshAsync(body, cancellationToken).ConfigureAwait(false);
        return Results.Accepted(
            $"/api/v1/media-library/operations/{accepted.OperationId}", accepted);
    }
    catch (Exception exception) { return MapError(exception); }
});
app.MapGet("/api/v1/media-library/operations/{operationId}", async (
    string operationId,
    IMediaLibraryOperationCoordinator coordinator,
    CancellationToken cancellationToken) => await ExecuteMediaAsync(
        () => coordinator.GetOperationAsync(operationId, cancellationToken)).ConfigureAwait(false));
app.Run();

static IResult Execute(Func<SpotlightStateResponse> action)
{
    try { return Results.Ok(action()); }
    catch (Exception exception) { return MapError(exception); }
}
static async Task<IResult> ExecuteAsync(Func<Task<SpotlightStateResponse>> action)
{
    try { return Results.Ok(await action().ConfigureAwait(false)); }
    catch (Exception exception) { return MapError(exception); }
}
static async Task<IResult> ExecuteMediaAsync<T>(Func<Task<T>> action)
{
    try { return Results.Ok(await action().ConfigureAwait(false)); }
    catch (Exception exception) { return MapError(exception); }
}
static IResult MapError(Exception exception)
{
    (int status, string code) = exception switch
    {
        ProgrammingConfigurationConflictException => (409, OperationsErrorCodes.StaleRevision),
        UnsupportedContentTypeException => (415, OperationsErrorCodes.UnsupportedContentType),
        RequestTooLargeException => (413, OperationsErrorCodes.RequestTooLarge),
        MalformedRequestException => (400, OperationsErrorCodes.MalformedRequest),
        OperationsValidationException => (400, OperationsErrorCodes.ValidationFailed),
        ProgrammingConfigurationLockException => (503, OperationsErrorCodes.HelperUnavailable),
        MediaLibraryOperationException media => media.Code switch
        {
            OperationsErrorCodes.RefreshBusy or OperationsErrorCodes.StaleRevision => (409, media.Code),
            OperationsErrorCodes.OperationNotFound => (404, media.Code),
            OperationsErrorCodes.InvalidOperationId or OperationsErrorCodes.ValidationFailed => (400, media.Code),
            OperationsErrorCodes.FeatureDisabled or OperationsErrorCodes.FeatureUnavailable => (503, media.Code),
            _ => (500, OperationsErrorCodes.InternalError),
        },
        _ => (500, OperationsErrorCodes.InternalError),
    };
    return Results.Json(new OperationsErrorResponse(OperationsProtocol.SchemaVersion, code), statusCode: status);
}

public partial class Program;
