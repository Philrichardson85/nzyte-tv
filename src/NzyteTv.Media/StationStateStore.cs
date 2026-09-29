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

    StationRuntimeState? ReadIfExists(string path);
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

    public StationRuntimeState? ReadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(Path.GetFullPath(path)) ? Read(path) : null;
    }

    private static void Validate(StationRuntimeState state, string path)
    {
        if (state.SchemaVersion is not (
            StationRuntimeState.LegacySchemaVersion or
            StationRuntimeState.CurrentSchemaVersion))
        {
            throw new InvalidDataException(
                $"Unsupported station state schemaVersion {state.SchemaVersion} in '{path}'; " +
                $"supported versions are {StationRuntimeState.LegacySchemaVersion} and " +
                $"{StationRuntimeState.CurrentSchemaVersion}.");
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

        if (state.SchemaVersion == StationRuntimeState.CurrentSchemaVersion)
        {
            ValidateVersionTwo(state, path);
        }
    }

    private static void ValidateVersionTwo(StationRuntimeState state, string path)
    {
        if (!IsSha256(state.QueueId)
            || state.QueueItemCount is not int queueItemCount
            || queueItemCount < 1
            || state.LastStartMode is not StationStartMode startMode
            || !Enum.IsDefined(startMode)
            || state.ResumeCount < 0
            || (startMode == StationStartMode.Resume
                && (state.ResumeCount < 1 || state.LastResumeAtUtc is null)))
        {
            throw new InvalidDataException(
                $"Station state schemaVersion 2 contains invalid resume metadata: {path}");
        }

        ValidateIndex(state.CurrentGlobalIndex, queueItemCount, nameof(state.CurrentGlobalIndex), path);
        ValidateIndex(state.LastCompletedGlobalIndex, queueItemCount, nameof(state.LastCompletedGlobalIndex), path);
        ValidateIndex(state.ResumeGlobalIndex, queueItemCount, nameof(state.ResumeGlobalIndex), path);

        if (state.ResumeGlobalIndex is int resumeIndex)
        {
            int expectedResume = state.LastCompletedGlobalIndex is int lastCompleted
                ? lastCompleted + 1
                : 0;
            if (resumeIndex != expectedResume)
            {
                throw new InvalidDataException(
                    $"Station state contains inconsistent completion and resume positions: {path}");
            }
        }
        else if (state.LastCompletedGlobalIndex != queueItemCount - 1)
        {
            throw new InvalidDataException(
                $"Station state without a resume position must have completed its final queue item: {path}");
        }

        if (state.StationState == StationState.Completed
            && (state.ResumeGlobalIndex is not null
                || state.LastCompletedGlobalIndex != queueItemCount - 1))
        {
            throw new InvalidDataException(
                $"Completed station state contains an incomplete resume position: {path}");
        }
    }

    private static void ValidateIndex(int? index, int itemCount, string propertyName, string path)
    {
        if (index is < 0 || index >= itemCount)
        {
            throw new InvalidDataException(
                $"Station state property '{propertyName}' is outside the queue bounds: {path}");
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
