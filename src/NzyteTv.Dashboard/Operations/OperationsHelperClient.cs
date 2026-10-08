using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzyteTv.Dashboard.Configuration;
using NzyteTv.Operations.Contracts;

namespace NzyteTv.Dashboard.Operations;

public sealed class OperationsHelperClient : IOperationsHelperClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly HttpClient _httpClient;

    public OperationsHelperClient(DashboardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(
                        new UnixDomainSocketEndPoint(options.OperationsSocketPath),
                        cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://localhost"),
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    public Task<SpotlightStateResponse> GetSpotlightAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/api/v1/spotlight", content: null, cancellationToken);

    public Task<SpotlightStateResponse> SetSpotlightAsync(
        SetSpotlightRequest request,
        CancellationToken cancellationToken) => SendAsync(
            HttpMethod.Post, "/api/v1/spotlight", JsonContent.Create(request, options: JsonOptions), cancellationToken);

    public Task<SpotlightStateResponse> DisableSpotlightAsync(
        DisableSpotlightRequest request,
        CancellationToken cancellationToken) => SendAsync(
            HttpMethod.Post, "/api/v1/spotlight/disable", JsonContent.Create(request, options: JsonOptions), cancellationToken);

    public void Dispose() => _httpClient.Dispose();

    private async Task<SpotlightStateResponse> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<SpotlightStateResponse>(JsonOptions, cancellationToken)
                    .ConfigureAwait(false) ?? throw new OperationsHelperException(OperationsErrorCodes.HelperUnavailable);
            }

            OperationsErrorResponse? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<OperationsErrorResponse>(JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                // An invalid helper response is reduced to the fixed unavailable code below.
            }
            throw new OperationsHelperException(
                error?.Code ?? OperationsErrorCodes.HelperUnavailable,
                (int)response.StatusCode);
        }
        catch (OperationsHelperException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or SocketException
            or TaskCanceledException or JsonException or NotSupportedException)
        {
            throw new OperationsHelperException(OperationsErrorCodes.HelperUnavailable, innerException: exception);
        }
    }
}
