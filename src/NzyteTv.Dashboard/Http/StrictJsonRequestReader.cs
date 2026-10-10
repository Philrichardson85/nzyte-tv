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

    public static async Task<T> ReadAsync<T>(
        HttpRequest request,
        CancellationToken cancellationToken,
        int maximumBytes = OperationsProtocol.MaximumRequestBytes) where T : class
    {
        if (!request.HasJsonContentType()) throw new DashboardUnsupportedContentTypeException();
        if (request.ContentLength > maximumBytes) throw new DashboardRequestTooLargeException();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[1024];
        while (true)
        {
            int count = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > maximumBytes) throw new DashboardRequestTooLargeException();
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        try
        {
            using JsonDocument document = JsonDocument.Parse(buffer);
            EnsureNoDuplicateProperties(document.RootElement);
            buffer.Position = 0;
            return await JsonSerializer.DeserializeAsync<T>(buffer, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? throw new DashboardMalformedRequestException();
        }
        catch (JsonException exception) { throw new DashboardMalformedRequestException(exception); }
    }

    private static void EnsureNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property.");
                EnsureNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) EnsureNoDuplicateProperties(item);
        }
    }
}

internal sealed class DashboardUnsupportedContentTypeException : InvalidOperationException;
internal sealed class DashboardRequestTooLargeException : InvalidOperationException;
internal sealed class DashboardMalformedRequestException(Exception? innerException = null)
    : InvalidOperationException("Malformed request.", innerException);
