using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NzyteTv.Media;

public static class RollingProgrammingJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options) + Environment.NewLine;

    public static string SerializeCanonical<T>(T value) =>
        JsonSerializer.Serialize(value, CanonicalOptions);

    public static T Deserialize<T>(string json, string description)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            ValidateNoDuplicateProperties(document.RootElement, "$", description);
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new InvalidDataException($"{description} is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Malformed {description}: {exception.Message}", exception);
        }
    }

    public static string Sha256(string content) => Sha256(Encoding.UTF8.GetBytes(content));

    public static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    public static string Sha256File(string path) => Sha256(File.ReadAllBytes(path));

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
                        $"{description} contains duplicate property '{property.Name}' at {location}.");
                }

                ValidateNoDuplicateProperties(property.Value, $"{location}.{property.Name}", description);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement child in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(child, $"{location}[{index}]", description);
                index++;
            }
        }
    }
}
