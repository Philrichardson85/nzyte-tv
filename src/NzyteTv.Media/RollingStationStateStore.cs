using System.Text.Json;
using System.Text.Json.Serialization;
using NzyteTv.Core;

namespace NzyteTv.Media;

public interface IRollingStationStateStore
{
    Task WriteAsync(
        string path,
        RollingStationRuntimeState state,
        CancellationToken cancellationToken);

    RollingStationRuntimeState Read(string path);

    RollingStationRuntimeState? ReadIfExists(string path);
}

public sealed class RollingStationStateStore : IRollingStationStateStore
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

    public RollingStationStateStore(IAtomicTextFileWriter? writer = null)
    {
        _writer = writer ?? new AtomicTextFileWriter();
    }

    public Task WriteAsync(
        string path,
        RollingStationRuntimeState state,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);
        RollingStationRuntimeState safeState = state with
        {
            LastTransitionError = StationSecretRedactor.RedactRtmpUrls(state.LastTransitionError),
        };
        Validate(safeState, Path.GetFullPath(path));
        string json = JsonSerializer.Serialize(safeState, JsonOptions) + Environment.NewLine;
        if (!string.Equals(json, StationSecretRedactor.RedactRtmpUrls(json), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Rolling station state contains an RTMP or RTMPS destination.");
        }

        return _writer.WriteAsync(path, json, cancellationToken);
    }

    public RollingStationRuntimeState Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Rolling station state file not found: {fullPath}",
                fullPath);
        }

        RollingStationRuntimeState state;
        try
        {
            state = JsonSerializer.Deserialize<RollingStationRuntimeState>(
                File.ReadAllText(fullPath),
                JsonOptions) ?? throw new InvalidDataException(
                $"Rolling station state JSON is empty: {fullPath}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Malformed rolling station state JSON in '{fullPath}': {exception.Message}",
                exception);
        }

        Validate(state, fullPath);
        return state;
    }

    public RollingStationRuntimeState? ReadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(Path.GetFullPath(path)) ? Read(path) : null;
    }

    public static void Validate(RollingStationRuntimeState state, string path)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != RollingStationRuntimeState.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported rolling station state schemaVersion {state.SchemaVersion} in '{path}'; " +
                $"expected {RollingStationRuntimeState.CurrentSchemaVersion}.");
        }

        if (!Guid.TryParseExact(state.PlannerId, "N", out _)
            || !Enum.IsDefined(state.Phase)
            || state.UpdatedAtUtc == default)
        {
            throw new InvalidDataException(
                $"Rolling station state contains missing or invalid required values: {path}");
        }

        bool hasAnyActive = state.ActiveBlockSequence is not null
            || state.ActiveBlockId is not null
            || state.ActiveQueueId is not null
            || state.ClaimedAtUtc is not null;
        bool hasAllActive = state.ActiveBlockSequence is > 0
            && IsSha256(state.ActiveBlockId)
            && IsSha256(state.ActiveQueueId)
            && state.ClaimedAtUtc is not null;
        if (hasAnyActive != hasAllActive)
        {
            throw new InvalidDataException(
                $"Rolling station active block identity must be present as one complete group: {path}");
        }

        bool phaseRequiresActive = state.Phase is
            RollingStationPhase.Claimed or
            RollingStationPhase.Executing or
            RollingStationPhase.Stopped;
        bool phaseForbidsActive = state.Phase is
            RollingStationPhase.Advancing or
            RollingStationPhase.WaitingForBlock;
        if ((phaseRequiresActive && !hasAllActive) || (phaseForbidsActive && hasAnyActive))
        {
            throw new InvalidDataException(
                $"Rolling station phase and active block identity are inconsistent: {path}");
        }

        bool hasAnyCompleted = state.LastCompletedBlockSequence is not null
            || state.LastCompletedBlockId is not null
            || state.LastCompletedAtUtc is not null;
        bool hasAllCompleted = state.LastCompletedBlockSequence is > 0
            && IsSha256(state.LastCompletedBlockId)
            && state.LastCompletedAtUtc is not null;
        if (hasAnyCompleted != hasAllCompleted)
        {
            throw new InvalidDataException(
                $"Rolling station completed block identity must be present as one complete group: {path}");
        }

        if (hasAllActive
            && state.ActiveBlockSequence != (state.LastCompletedBlockSequence ?? 0) + 1)
        {
            throw new InvalidDataException(
                $"Rolling station active sequence does not follow its last completed sequence: {path}");
        }

        if (state.Phase == RollingStationPhase.Failed)
        {
            if (state.FailureDisposition is null || string.IsNullOrWhiteSpace(state.LastTransitionError))
            {
                throw new InvalidDataException(
                    $"Failed rolling station state requires a disposition and transition error: {path}");
            }
        }
        else if (state.FailureDisposition is not null)
        {
            throw new InvalidDataException(
                $"Only failed rolling station state may contain a failure disposition: {path}");
        }

        bool hasCutoverQueue = state.InitialCutoverSourceQueueId is not null;
        bool hasCutoverTime = state.InitialCutoverAcceptedAtUtc is not null;
        if (hasCutoverQueue != hasCutoverTime
            || (hasCutoverQueue && !IsSha256(state.InitialCutoverSourceQueueId)))
        {
            throw new InvalidDataException(
                $"Rolling station initial-cutover audit information is invalid: {path}");
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public interface IRollingCoordinatorLock : IDisposable;

public interface IRollingCoordinatorLockProvider
{
    IRollingCoordinatorLock Acquire(string stationStatePath);
}

public sealed class RollingCoordinatorLockProvider : IRollingCoordinatorLockProvider
{
    public static string GetLockPath(string stationStatePath) =>
        Path.GetFullPath(stationStatePath) + ".rolling-coordinator.lock";

    public IRollingCoordinatorLock Acquire(string stationStatePath)
    {
        string lockPath = GetLockPath(stationStatePath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)
            ?? throw new InvalidOperationException("Rolling coordinator lock path has no parent directory."));
        try
        {
            return new HeldRollingCoordinatorLock(new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RollingStationSafetyException(
                "The rolling coordinator lock is unavailable; another rolling coordinator may already be running.",
                exception);
        }
    }

    private sealed class HeldRollingCoordinatorLock(FileStream stream) : IRollingCoordinatorLock
    {
        public void Dispose() => stream.Dispose();
    }
}

public sealed class RollingStationSafetyException : Exception
{
    public RollingStationSafetyException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
