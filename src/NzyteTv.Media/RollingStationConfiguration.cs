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
        };
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
