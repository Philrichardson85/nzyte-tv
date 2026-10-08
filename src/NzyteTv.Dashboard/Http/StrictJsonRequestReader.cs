using System.Text.Json;
using System.Text.Json.Serialization;
using NzyteTv.Operations.Contracts;

namespace NzyteTv.Dashboard.Http;

internal static class StrictJsonRequestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<T> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken) where T : class
    {
        if (!request.HasJsonContentType()) throw new DashboardUnsupportedContentTypeException();
        if (request.ContentLength > OperationsProtocol.MaximumRequestBytes) throw new DashboardRequestTooLargeException();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[1024];
        while (true)
        {
            int count = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > OperationsProtocol.MaximumRequestBytes) throw new DashboardRequestTooLargeException();
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(buffer, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? throw new DashboardMalformedRequestException();
        }
        catch (JsonException exception) { throw new DashboardMalformedRequestException(exception); }
    }
}

internal sealed class DashboardUnsupportedContentTypeException : InvalidOperationException;
internal sealed class DashboardRequestTooLargeException : InvalidOperationException;
internal sealed class DashboardMalformedRequestException(Exception? innerException = null)
    : InvalidOperationException("Malformed request.", innerException);
