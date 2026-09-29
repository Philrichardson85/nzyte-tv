using System.Text.Json;
using System.Text.Json.Serialization;
using NzyteTv.Core;

namespace NzyteTv.Media;

public interface IStationConfigurationLoader
{
    StationConfiguration Load(string path);
}

public sealed class StationConfigurationLoader : IStationConfigurationLoader
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public StationConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string configPath = Path.GetFullPath(path);
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Station configuration file not found: {configPath}", configPath);
        }

        StationConfiguration configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<StationConfiguration>(
                File.ReadAllText(configPath),
                ReadOptions) ?? throw new InvalidDataException(
                    $"Station configuration JSON is empty: {configPath}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Malformed station configuration JSON in '{configPath}': {exception.Message}",
                exception);
        }

        return ValidateAndNormalize(configuration, configPath);
    }

    private static StationConfiguration ValidateAndNormalize(
        StationConfiguration configuration,
        string configPath)
    {
        if (configuration.SchemaVersion != StationConfiguration.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported station schemaVersion {configuration.SchemaVersion} in '{configPath}'; " +
                $"expected {StationConfiguration.CurrentSchemaVersion}.");
        }

        string mediaRoot = RequireAbsolutePath(configuration.MediaRoot, "mediaRoot");
        string libraryRoot = RequireAbsolutePath(configuration.LibraryRoot, "libraryRoot");
        string statePath = RequireAbsolutePath(configuration.StatePath, "statePath");

        if (!Directory.Exists(mediaRoot))
        {
            throw new DirectoryNotFoundException($"Station media root not found: {mediaRoot}");
        }

        if (!Directory.Exists(libraryRoot))
        {
            throw new DirectoryNotFoundException($"Station library root not found: {libraryRoot}");
        }

        if (Directory.Exists(statePath))
        {
            throw new InvalidDataException($"Station statePath names a directory, not a file: {statePath}");
        }

        string stateDirectory = Path.GetDirectoryName(statePath)
            ?? throw new InvalidDataException("Station statePath must have a parent directory.");
        if (File.Exists(stateDirectory))
        {
            throw new InvalidDataException(
                $"Station statePath parent is a file, not a directory: {stateDirectory}");
        }

        if (configuration.Playlists is null || configuration.Playlists.Count == 0)
        {
            throw new InvalidDataException("Station configuration requires at least one playlist path.");
        }

        var normalizedPlaylists = new List<string>(configuration.Playlists.Count);
        var uniquePlaylists = new HashSet<string>(GetPathComparer());
        foreach (string playlistPath in configuration.Playlists)
        {
            string normalized = RequireAbsolutePath(playlistPath, "playlists entry");
            if (!uniquePlaylists.Add(normalized))
            {
                throw new InvalidDataException($"Station configuration contains a duplicate playlist path: {normalized}");
            }

            if (!File.Exists(normalized))
            {
                throw new FileNotFoundException($"Station playlist file not found: {normalized}", normalized);
            }

            normalizedPlaylists.Add(normalized);
        }

        return configuration with
        {
            MediaRoot = mediaRoot,
            LibraryRoot = libraryRoot,
            StatePath = statePath,
            Playlists = normalizedPlaylists,
        };
    }

    private static string RequireAbsolutePath(string? path, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException($"Station configuration property '{propertyName}' is required.");
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new InvalidDataException(
                    $"Station configuration property '{propertyName}' must be an absolute path: {path}");
            }

            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(
                $"Station configuration property '{propertyName}' is not a valid path.",
                exception);
        }
    }

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

public sealed record StationValidationResult(
    StationConfiguration Configuration,
    BroadcastPlan BroadcastPlan,
    bool FfmpegAvailable,
    bool DestinationConfigured)
{
    public bool IsReady => BroadcastPlan.IsReady && FfmpegAvailable;
}

public sealed class StationValidationService
{
    private readonly IStationConfigurationLoader _configurationLoader;
    private readonly IBroadcastPlanner _broadcastPlanner;
    private readonly Func<string> _locateFfmpeg;

    public StationValidationService(
        IStationConfigurationLoader configurationLoader,
        IBroadcastPlanner broadcastPlanner,
        Func<string>? locateFfmpeg = null)
    {
        ArgumentNullException.ThrowIfNull(configurationLoader);
        ArgumentNullException.ThrowIfNull(broadcastPlanner);
        _configurationLoader = configurationLoader;
        _broadcastPlanner = broadcastPlanner;
        _locateFfmpeg = locateFfmpeg ?? MediaToolLocator.LocateFfmpegOnPath;
    }

    public StationValidationResult Validate(string configPath, bool destinationConfigured)
    {
        StationConfiguration configuration = _configurationLoader.Load(configPath);
        BroadcastPlan plan = _broadcastPlanner.CreatePlan(
            configuration.Playlists,
            configuration.LibraryRoot);
        bool ffmpegAvailable;
        try
        {
            ffmpegAvailable = !string.IsNullOrWhiteSpace(_locateFfmpeg());
        }
        catch (MediaToolNotFoundException)
        {
            ffmpegAvailable = false;
        }

        return new StationValidationResult(
            configuration,
            plan,
            ffmpegAvailable,
            destinationConfigured);
    }
}
