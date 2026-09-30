using System.Text.Json;
using System.Text.Json.Serialization;
using NzyteTv.Core;

namespace NzyteTv.Media;

public interface IProgrammingConfigurationStore
{
    ProgrammingConfiguration Load(string path);

    ProgrammingConfiguration? LoadIfExists(string path);

    Task<ProgrammingConfigurationInitializationResult> InitializeAsync(
        string path,
        CancellationToken cancellationToken);

    Task WriteAsync(
        string path,
        ProgrammingConfiguration configuration,
        CancellationToken cancellationToken);
}

public sealed record ProgrammingConfigurationInitializationResult(
    string Path,
    ProgrammingConfiguration Configuration,
    bool Created);

public sealed class ProgrammingConfigurationStore : IProgrammingConfigurationStore
{
    public const string FileName = "programming.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly IAtomicTextFileWriter _writer;

    public ProgrammingConfigurationStore(IAtomicTextFileWriter? writer = null)
    {
        _writer = writer ?? new AtomicTextFileWriter();
    }

    public static string GetPathForCatalog(string catalogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        string fullCatalogPath = Path.GetFullPath(catalogPath);
        string directory = Path.GetDirectoryName(fullCatalogPath)
            ?? throw new InvalidOperationException("The song catalog path has no parent directory.");
        return Path.Combine(directory, FileName);
    }

    public ProgrammingConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Programming configuration not found: {fullPath}", fullPath);
        }

        try
        {
            string json = File.ReadAllText(fullPath);
            using JsonDocument document = JsonDocument.Parse(json);
            ValidateNoDuplicateProperties(document.RootElement, "$", fullPath);
            ProgrammingConfiguration configuration = JsonSerializer.Deserialize<ProgrammingConfiguration>(
                json,
                JsonOptions) ?? throw new InvalidDataException($"Programming configuration is empty: {fullPath}");
            ProgrammingConfigurationValidator.ValidateStructure(configuration);
            return Normalize(configuration);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Malformed programming configuration JSON in '{fullPath}': {exception.Message}",
                exception);
        }
    }

    public ProgrammingConfiguration? LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(Path.GetFullPath(path)) ? Load(path) : null;
    }

    public async Task<ProgrammingConfigurationInitializationResult> InitializeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        ProgrammingConfiguration? existing = LoadIfExists(fullPath);
        if (existing is not null)
        {
            return new ProgrammingConfigurationInitializationResult(fullPath, existing, Created: false);
        }

        ProgrammingConfiguration configuration = ProgrammingConfiguration.CreateDefault();
        string json = Serialize(configuration);
        await WriteNewAsync(fullPath, json, cancellationToken).ConfigureAwait(false);
        return new ProgrammingConfigurationInitializationResult(fullPath, configuration, Created: true);
    }

    public Task WriteAsync(
        string path,
        ProgrammingConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(configuration);
        ProgrammingConfigurationValidator.ValidateStructure(configuration);
        return _writer.WriteAsync(Path.GetFullPath(path), Serialize(configuration), cancellationToken);
    }

    private static string Serialize(ProgrammingConfiguration configuration) =>
        JsonSerializer.Serialize(Normalize(configuration), JsonOptions) + Environment.NewLine;

    private static ProgrammingConfiguration Normalize(ProgrammingConfiguration configuration) =>
        configuration with
        {
            AssetOverrides = (configuration.AssetOverrides
                    ?? new Dictionary<string, AssetEditorialOverride>(StringComparer.Ordinal))
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
        };

    private async Task WriteNewAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The programming configuration path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.partial");
        try
        {
            await _writer.WriteAsync(temporaryPath, content, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ValidateNoDuplicateProperties(JsonElement element, string location, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"Programming configuration contains duplicate property '{property.Name}' at {location}: {path}");
                }

                ValidateNoDuplicateProperties(
                    property.Value,
                    $"{location}.{property.Name}",
                    path);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement child in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(child, $"{location}[{index}]", path);
                index++;
            }
        }
    }
}
