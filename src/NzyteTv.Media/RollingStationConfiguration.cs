using System.Text.Json;
using System.Text.Json.Serialization;
using NzyteTv.Core;

namespace NzyteTv.Media;

public interface IRollingStationConfigurationLoader
{
    RollingStationConfiguration Load(string path);
}

public sealed class RollingStationConfigurationLoader : IRollingStationConfigurationLoader
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter<RollingAssetMetadataStorageMode>(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false),
        },
    };

    public RollingStationConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string configPath = Path.GetFullPath(path);
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                $"Rolling station configuration file not found: {configPath}",
                configPath);
        }

        RollingStationConfiguration configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<RollingStationConfiguration>(
                File.ReadAllText(configPath),
                ReadOptions) ?? throw new InvalidDataException(
                $"Rolling station configuration JSON is empty: {configPath}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Malformed rolling station configuration JSON in '{configPath}': {exception.Message}",
                exception);
        }

        if (configuration.SchemaVersion != RollingStationConfiguration.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported rolling station schemaVersion {configuration.SchemaVersion} in '{configPath}'; " +
                $"expected {RollingStationConfiguration.CurrentSchemaVersion}.");
        }

        if (!Guid.TryParseExact(configuration.PlannerId, "N", out _))
        {
            throw new InvalidDataException(
                "Rolling station configuration property 'plannerId' must be a 32-character planner lineage ID.");
        }

        string stationConfigPath = RequireAbsolutePath(
            configuration.StationConfigPath,
            "stationConfigPath");
        string rollingStatePath = RequireAbsolutePath(
            configuration.RollingStatePath,
            "rollingStatePath");
        RollingAssetMetadataStorageConfiguration? assetMetadataStorage =
            ValidateAssetMetadataStorage(configuration.AssetMetadataStorage);
        if (!File.Exists(stationConfigPath))
        {
            throw new FileNotFoundException(
                $"Referenced static station configuration file not found: {stationConfigPath}",
                stationConfigPath);
        }

        if (Directory.Exists(rollingStatePath))
        {
            throw new InvalidDataException(
                $"Rolling station rollingStatePath names a directory, not a file: {rollingStatePath}");
        }

        string stateDirectory = Path.GetDirectoryName(rollingStatePath)
            ?? throw new InvalidDataException("Rolling station rollingStatePath must have a parent directory.");
        if (File.Exists(stateDirectory))
        {
            throw new InvalidDataException(
                $"Rolling station rollingStatePath parent is a file, not a directory: {stateDirectory}");
        }

        return configuration with
        {
            StationConfigPath = stationConfigPath,
            PlannerId = configuration.PlannerId.ToLowerInvariant(),
            RollingStatePath = rollingStatePath,
            AssetMetadataStorage = assetMetadataStorage,
        };
    }

    private static RollingAssetMetadataStorageConfiguration? ValidateAssetMetadataStorage(
        RollingAssetMetadataStorageConfiguration? configuration)
    {
        if (configuration is null)
        {
            return null;
        }

        if (!Enum.IsDefined(configuration.Mode))
        {
            throw new InvalidDataException(
                "Rolling station assetMetadataStorage mode is invalid.");
        }

        if (configuration.Mode == RollingAssetMetadataStorageMode.Adjacent)
        {
            if (configuration.ExternalRoot is not null)
            {
                throw new InvalidDataException(
                    "Rolling station assetMetadataStorage externalRoot is not valid in adjacent mode.");
            }

            return configuration;
        }

        string externalRoot = RequireAbsolutePath(
            configuration.ExternalRoot,
            "assetMetadataStorage.externalRoot");
        return configuration with { ExternalRoot = externalRoot };
    }

    private static string RequireAbsolutePath(string? path, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException(
                $"Rolling station configuration property '{propertyName}' is required.");
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new InvalidDataException(
                    $"Rolling station configuration property '{propertyName}' must be an absolute path: {path}");
            }

            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(
                $"Rolling station configuration property '{propertyName}' is not a valid path.",
                exception);
        }
    }
}

public static class RollingStationMetadataRepositoryFactory
{
    public static IAssetMetadataRepository Create(RollingStationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        RollingAssetMetadataStorageConfiguration? metadata = configuration.AssetMetadataStorage;
        AssetMetadataStorageOptions options = metadata?.Mode switch
        {
            null or RollingAssetMetadataStorageMode.Adjacent =>
                AssetMetadataStorageOptions.Adjacent,
            RollingAssetMetadataStorageMode.ExternalGeneration =>
                AssetMetadataStorageOptions.External(metadata.ExternalRoot!),
            _ => throw new InvalidDataException(
                "Rolling station assetMetadataStorage mode is invalid."),
        };
        return AssetMetadataRepository.Create(options);
    }
}
