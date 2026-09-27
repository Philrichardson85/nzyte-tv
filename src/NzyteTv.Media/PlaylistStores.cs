using System.Text;
using System.Text.Json;
using NzyteTv.Core;

namespace NzyteTv.Media;

public interface IPlaylistStore
{
    Task WriteAsync(string path, PlaylistDocument playlist, CancellationToken cancellationToken);
}

public interface IPlaylistHistoryStore
{
    PlaylistHistoryDocument Load(string path);

    Task WriteAsync(string path, PlaylistHistoryDocument history, CancellationToken cancellationToken);
}

public sealed class PlaylistStore : IPlaylistStore
{
    public Task WriteAsync(string path, PlaylistDocument playlist, CancellationToken cancellationToken) =>
        PlaylistJsonFile.WriteAtomicAsync(path, playlist, cancellationToken);
}

public sealed class PlaylistHistoryStore : IPlaylistHistoryStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public PlaylistHistoryDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return PlaylistHistoryDocument.Empty;
        }

        try
        {
            PlaylistHistoryDocument? history = JsonSerializer.Deserialize<PlaylistHistoryDocument>(
                File.ReadAllText(fullPath),
                ReadOptions);
            Validate(history, fullPath);
            return history!;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Malformed playlist history JSON in '{fullPath}': {exception.Message}",
                exception);
        }
    }

    public Task WriteAsync(
        string path,
        PlaylistHistoryDocument history,
        CancellationToken cancellationToken)
    {
        Validate(history, Path.GetFullPath(path));
        return PlaylistJsonFile.WriteAtomicAsync(path, history, cancellationToken);
    }

    private static void Validate(PlaylistHistoryDocument? history, string path)
    {
        if (history is null)
        {
            throw new InvalidDataException($"Playlist history is empty: {path}");
        }

        if (history.SchemaVersion != PlaylistHistoryDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported playlist history schemaVersion {history.SchemaVersion} in '{path}'; expected {PlaylistHistoryDocument.CurrentSchemaVersion}.");
        }

        if (history.Plays is null
            || history.Plays.Any(play => string.IsNullOrWhiteSpace(play.AssetId)
                || string.IsNullOrWhiteSpace(play.Type)))
        {
            throw new InvalidDataException($"Playlist history contains invalid play entries: {path}");
        }

        if (history.ScheduleEndUtc is DateTimeOffset end
            && history.Plays.Any(play => play.PlayedAtUtc > end))
        {
            throw new InvalidDataException($"Playlist history contains a play after scheduleEndUtc: {path}");
        }
    }
}

internal static class PlaylistJsonFile
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task WriteAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The JSON output path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.partial");
        try
        {
            string json = JsonSerializer.Serialize(value, WriteOptions) + Environment.NewLine;
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
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
