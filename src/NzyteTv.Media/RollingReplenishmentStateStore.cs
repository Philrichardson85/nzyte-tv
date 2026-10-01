using System.Text.Json;
using System.Text.Json.Serialization;
using NzyteTv.Core;

namespace NzyteTv.Media;

public interface IRollingReplenishmentStateStore
{
    Task WriteAsync(
        string path,
        RollingReplenishmentState state,
        CancellationToken cancellationToken);

    RollingReplenishmentState Read(string path);

    RollingReplenishmentState? ReadIfExists(string path);
}

public sealed class RollingReplenishmentStateStore : IRollingReplenishmentStateStore
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

    public RollingReplenishmentStateStore(IAtomicTextFileWriter? writer = null)
    {
        _writer = writer ?? new AtomicTextFileWriter();
    }

    public static string GetPath(string rollingStatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollingStatePath);
        return Path.GetFullPath(rollingStatePath) + ".replenishment.json";
    }

    public Task WriteAsync(
        string path,
        RollingReplenishmentState state,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);
        RollingReplenishmentState safe = state with
        {
            LastError = StationSecretRedactor.RedactRtmpUrls(state.LastError),
        };
        Validate(safe, Path.GetFullPath(path));
        string json = JsonSerializer.Serialize(safe, JsonOptions) + Environment.NewLine;
        if (!string.Equals(json, StationSecretRedactor.RedactRtmpUrls(json), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Rolling replenishment diagnostics contain an RTMP or RTMPS destination.");
        }

        return _writer.WriteAsync(path, json, cancellationToken);
    }

    public RollingReplenishmentState Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Rolling replenishment state file not found: {fullPath}",
                fullPath);
        }

        RollingReplenishmentState state;
        try
        {
            state = JsonSerializer.Deserialize<RollingReplenishmentState>(
                File.ReadAllText(fullPath),
                JsonOptions) ?? throw new InvalidDataException(
                    $"Rolling replenishment state JSON is empty: {fullPath}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Malformed rolling replenishment state JSON in '{fullPath}': {exception.Message}",
                exception);
        }

        Validate(state, fullPath);
        return state;
    }

    public RollingReplenishmentState? ReadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(Path.GetFullPath(path)) ? Read(path) : null;
    }

    public static void Validate(RollingReplenishmentState state, string path)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != RollingReplenishmentState.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported rolling replenishment schemaVersion {state.SchemaVersion} in '{path}'; " +
                $"expected {RollingReplenishmentState.CurrentSchemaVersion}.");
        }

        if (!Guid.TryParseExact(state.PlannerId, "N", out _)
            || !Enum.IsDefined(state.Health)
            || !Enum.IsDefined(state.ErrorClassification)
            || state.AnchorSequence < 1
            || state.HighestCommittedSequence < 0
            || state.RequiredHighestSequence < state.AnchorSequence
            || state.FutureBlockTarget < 0
            || state.RequiredHighestSequence != state.AnchorSequence + state.FutureBlockTarget
            || state.BufferDeficit != Math.Max(
                0,
                state.RequiredHighestSequence - state.HighestCommittedSequence)
            || state.UpdatedAtUtc == default)
        {
            throw new InvalidDataException(
                $"Rolling replenishment state contains missing or invalid required values: {path}");
        }

        bool hasError = state.LastErrorAtUtc is not null
            || !string.IsNullOrWhiteSpace(state.LastError)
            || state.ErrorClassification != RollingReplenishmentErrorClassification.None;
        bool hasCompleteError = state.LastErrorAtUtc is not null
            && !string.IsNullOrWhiteSpace(state.LastError)
            && state.ErrorClassification != RollingReplenishmentErrorClassification.None;
        if (hasError != hasCompleteError)
        {
            throw new InvalidDataException(
                $"Rolling replenishment error diagnostics are inconsistent: {path}");
        }

        string json = JsonSerializer.Serialize(state, JsonOptions);
        if (!string.Equals(json, StationSecretRedactor.RedactRtmpUrls(json), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Rolling replenishment state contains an RTMP or RTMPS destination: {path}");
        }
    }
}
