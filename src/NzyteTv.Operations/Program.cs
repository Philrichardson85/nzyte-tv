using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NzyteTv.Media;
using NzyteTv.Operations.Configuration;
using NzyteTv.Operations.Contracts;
using NzyteTv.Operations.Http;
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
});
builder.Services.AddSingleton(operationsOptions);
builder.Services.AddSingleton<IProgrammingConfigurationStore, ProgrammingConfigurationStore>();
builder.Services.AddSingleton<ISongCatalogStore, SongCatalogStore>();
builder.Services.AddSingleton<IProgrammingInventoryLoader, ProgrammingInventoryLoader>();
builder.Services.AddSingleton<IProgrammingConfigurationMutationLock, ProgrammingConfigurationMutationLock>();
builder.Services.AddSingleton<ProgrammingService>();
builder.Services.AddSingleton<ISpotlightOperationsService, SpotlightOperationsService>();
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
        _ => (500, OperationsErrorCodes.InternalError),
    };
    return Results.Json(new OperationsErrorResponse(OperationsProtocol.SchemaVersion, code), statusCode: status);
}

public partial class Program;
