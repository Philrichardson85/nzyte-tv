using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NzyteTv.Core;

namespace NzyteTv.Media;

public static partial class StationSecretRedactor
{
    [GeneratedRegex("""(?i)rtmps?://[^\s"'<>]+""", RegexOptions.CultureInvariant)]
    private static partial Regex RtmpUrlPattern();

    public static string? RedactRtmpUrls(string? value) => string.IsNullOrEmpty(value)
        ? value
        : RtmpUrlPattern().Replace(value, "[REDACTED]");
}

public interface IAtomicTextFileWriter
{
    Task WriteAsync(string path, string content, CancellationToken cancellationToken);
}

public sealed class AtomicTextFileWriter : IAtomicTextFileWriter
{
    public async Task WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The station state path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.partial");

        try
        {
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

public interface IStationStateStore
{
    Task WriteAsync(string path, StationRuntimeState state, CancellationToken cancellationToken);

    StationRuntimeState Read(string path);
}

public sealed class StationStateStore : IStationStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IAtomicTextFileWriter _writer;

    public StationStateStore(IAtomicTextFileWriter? writer = null)
    {
        _writer = writer ?? new AtomicTextFileWriter();
    }

    public Task WriteAsync(
        string path,
        StationRuntimeState state,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);
        StationRuntimeState safeState = state with
        {
            LastError = StationSecretRedactor.RedactRtmpUrls(state.LastError),
        };
        string json = JsonSerializer.Serialize(safeState, JsonOptions) + Environment.NewLine;
        return _writer.WriteAsync(path, json, cancellationToken);
    }

    public StationRuntimeState Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Station state file not found: {fullPath}", fullPath);
        }

        StationRuntimeState state;
        try
        {
            state = JsonSerializer.Deserialize<StationRuntimeState>(
                File.ReadAllText(fullPath),
                JsonOptions) ?? throw new InvalidDataException($"Station state JSON is empty: {fullPath}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Malformed station state JSON in '{fullPath}': {exception.Message}",
                exception);
        }

        Validate(state, fullPath);
        return state;
    }

    private static void Validate(StationRuntimeState state, string path)
    {
        if (state.SchemaVersion != StationRuntimeState.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported station state schemaVersion {state.SchemaVersion} in '{path}'; " +
                $"expected {StationRuntimeState.CurrentSchemaVersion}.");
        }

        if (state.StationPid <= 0
            || !Enum.IsDefined(state.StationState)
            || !Enum.IsDefined(state.BroadcastState)
            || state.StartedAtUtc == default
            || state.LastHeartbeatUtc == default
            || string.IsNullOrWhiteSpace(state.MediaRoot)
            || string.IsNullOrWhiteSpace(state.LibraryRoot)
            || state.TotalPlaylistCount < 1
            || state.QueuedPlaylistCount < 0
            || state.QueuedPlaylistCount > state.TotalPlaylistCount)
        {
            throw new InvalidDataException($"Station state contains missing or invalid required values: {path}");
        }
    }
}
