using System.Text.Json;

namespace NzyteTv.Media;

internal static class StrictJson
{
    public static T Deserialize<T>(ReadOnlySpan<byte> json, JsonSerializerOptions options, string description)
        where T : class
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = 32,
            });
            ValidateNoDuplicateProperties(document.RootElement, "$", description);
            return JsonSerializer.Deserialize<T>(json, options)
                ?? throw new InvalidDataException($"The {description} is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Malformed {description}.", exception);
        }
    }

    private static void ValidateNoDuplicateProperties(
        JsonElement element,
        string location,
        string description)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"The {description} contains a duplicate property at {location}.");
                }

                ValidateNoDuplicateProperties(property.Value, $"{location}.{property.Name}", description);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement child in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(child, $"{location}[{index++}]", description);
            }
        }
    }
}
