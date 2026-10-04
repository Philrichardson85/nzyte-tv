using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NzyteTv.Core;

namespace NzyteTv.Media;

public enum FfmpegProgressState
{
    Continue,
    End,
}

public sealed record FfmpegProgressBatch(
    FfmpegProgressState State,
    TimeSpan? OutputTime,
    long? Frame,
    double? FramesPerSecond,
    double? BitrateKbitsPerSecond,
    long? TotalSizeBytes,
    long? DuplicateFrames,
    long? DroppedFrames,
    double? Speed);

/// <summary>Collects FFmpeg -progress output until its progress marker completes a batch.</summary>
public sealed class FfmpegProgressBatchParser
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public bool TryAddLine(string? line, out FfmpegProgressBatch? batch)
    {
        batch = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        int separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        string key = line[..separator];
        string value = line[(separator + 1)..];
        lock (_gate)
        {
            if (!string.Equals(key, "progress", StringComparison.Ordinal))
            {
                if (IsSupportedProgressKey(key))
                {
                    _values[key] = value;
                }

                return false;
            }

            FfmpegProgressState? state = value switch
            {
                "continue" => FfmpegProgressState.Continue,
                "end" => FfmpegProgressState.End,
                _ => null,
            };
            if (state is null)
            {
                _values.Clear();
                return false;
            }

            batch = new FfmpegProgressBatch(
                state.Value,
                ParseOutputTime(_values),
                ParseInt64(_values, "frame"),
                ParseDouble(_values, "fps"),
                ParseUnitDouble(_values, "bitrate", "kbits/s"),
                ParseInt64(_values, "total_size"),
                ParseInt64(_values, "dup_frames"),
                ParseInt64(_values, "drop_frames"),
                ParseUnitDouble(_values, "speed", "x"));
            _values.Clear();
            return true;
        }
    }

    private static bool IsSupportedProgressKey(string key) => key is
        "frame"
        or "fps"
        or "bitrate"
        or "total_size"
        or "out_time_us"
        or "out_time_ms"
        or "out_time"
        or "dup_frames"
        or "drop_frames"
        or "speed";

    private static TimeSpan? ParseOutputTime(IReadOnlyDictionary<string, string> values)
    {
        if (TryParseNonnegativeInt64(values, "out_time_us", out long microseconds)
            || TryParseNonnegativeInt64(values, "out_time_ms", out microseconds))
        {
            try
            {
                return TimeSpan.FromMicroseconds(microseconds);
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        return values.TryGetValue("out_time", out string? value)
            && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan parsed)
            && parsed >= TimeSpan.Zero
                ? parsed
                : null;
    }

    private static long? ParseInt64(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value)
        && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : null;

    private static double? ParseDouble(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value)
        && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
        && double.IsFinite(parsed)
            ? parsed
            : null;

    private static double? ParseUnitDouble(
        IReadOnlyDictionary<string, string> values,
        string key,
        string suffix)
    {
        if (!values.TryGetValue(key, out string? value))
        {
            return null;
        }

        string numeric = value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? value[..^suffix.Length]
            : value;
        return double.TryParse(
            numeric.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsed)
            && double.IsFinite(parsed)
                ? parsed
                : null;
    }

    private static bool TryParseNonnegativeInt64(
        IReadOnlyDictionary<string, string> values,
        string key,
        out long parsed)
    {
        parsed = 0;
        return values.TryGetValue(key, out string? value)
            && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
            && parsed >= 0;
    }
}

public sealed class BoundedDiagnosticBuffer
{
    public const int DefaultCapacity = 50;
    public const int DefaultMaximumLineLength = 2048;

    private readonly object _gate = new();
    private readonly List<string> _firstLines = [];
    private readonly Queue<string> _latestLines = new();
    private readonly int _firstLineCapacity;
    private readonly int _latestLineCapacity;
    private readonly int _maximumLineLength;
    private readonly Func<string, string> _sanitize;

    public BoundedDiagnosticBuffer(
        int capacity = DefaultCapacity,
        int maximumLineLength = DefaultMaximumLineLength,
        Func<string, string>? sanitize = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maximumLineLength <= 0) throw new ArgumentOutOfRangeException(nameof(maximumLineLength));
        _firstLineCapacity = Math.Max(1, capacity / 2);
        _latestLineCapacity = capacity - _firstLineCapacity;
        _maximumLineLength = maximumLineLength;
        _sanitize = sanitize ?? (value => value);
    }

    public void Add(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        string safe = _sanitize(line);
        if (safe.Length > _maximumLineLength)
        {
            safe = safe[.._maximumLineLength];
        }

        lock (_gate)
        {
            if (_firstLines.Count < _firstLineCapacity)
            {
                _firstLines.Add(safe);
                return;
            }

            if (_latestLineCapacity > 0)
            {
                while (_latestLines.Count >= _latestLineCapacity)
                {
                    _latestLines.Dequeue();
                }

                _latestLines.Enqueue(safe);
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _firstLines.Concat(_latestLines).ToArray();
        }
    }
}

public static partial class BroadcastCredentialRedactor
{
    [GeneratedRegex("(?i)rtmps?://[^\\s\"'<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex RtmpUrlPattern();

    [GeneratedRegex("(?im)(\\bauthorization\\s*[:=]\\s*)[^\\r\\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex("(?i)(\\bbearer\\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex("(?i)([\"']?(?:access_token|refresh_token|client_secret|stream_key)[\"']?\\s*[:=]\\s*[\"']?)[^&\\s,;\"'}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecretPattern();

    public static string Redact(string? value, string? configuredDestination = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        string safe = value;
        if (!string.IsNullOrEmpty(configuredDestination))
        {
            safe = safe.Replace(configuredDestination, "[REDACTED]", StringComparison.Ordinal);
            foreach (string secretComponent in GetSecretComponents(configuredDestination))
            {
                safe = safe.Replace(secretComponent, "[REDACTED]", StringComparison.Ordinal);
            }
        }

        safe = RtmpUrlPattern().Replace(safe, "[REDACTED]");
        safe = AuthorizationPattern().Replace(safe, "$1[REDACTED]");
        safe = BearerTokenPattern().Replace(safe, "$1[REDACTED]");
        return NamedSecretPattern().Replace(safe, "$1[REDACTED]");
    }

    public static string RedactDiagnosticLine(string? value, string? configuredDestination = null)
    {
        string safe = Redact(value, configuredDestination);
        return safe.Length > BoundedDiagnosticBuffer.DefaultMaximumLineLength
            ? safe[..BoundedDiagnosticBuffer.DefaultMaximumLineLength]
            : safe;
    }

    private static IReadOnlyList<string> GetSecretComponents(string destination)
    {
        if (!Uri.TryCreate(destination, UriKind.Absolute, out Uri? uri))
        {
            return [];
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var components = new HashSet<string>(StringComparer.Ordinal);
        if (segments.Length >= 2)
        {
            AddEscapedAndUnescaped(components, segments[^1]);
        }

        foreach (string queryPart in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = queryPart.IndexOf('=');
            if (separator >= 0 && separator + 1 < queryPart.Length)
            {
                AddEscapedAndUnescaped(components, queryPart[(separator + 1)..]);
            }
        }

        return components.ToArray();
    }

    private static void AddEscapedAndUnescaped(ISet<string> components, string value)
    {
        if (value.Length >= 4)
        {
            components.Add(value);
            try
            {
                components.Add(Uri.UnescapeDataString(value));
            }
            catch (UriFormatException)
            {
                // The exact escaped value is still protected.
            }
        }
    }
}

public enum BroadcastLaunchReason
{
    RecoveryRetry,
}

public enum BroadcastRetryDecision
{
    Completed,
    CompletedAfterQueueEnd,
    Cancelled,
    LaunchFailed,
    NoRetryPermanentFailure,
    RetryScheduled,
    RetryLimitReached,
}

public sealed record BroadcastAttemptDiagnostics
{
    public required string AttemptId { get; init; }

    public required DateTimeOffset LaunchTimeUtc { get; init; }

    public BroadcastLaunchReason? LaunchReason { get; init; }

    public required int StartingGlobalItemIndex { get; init; }

    public required string StartingPlaylistFileName { get; init; }

    public required int StartingSequence { get; init; }

    public required string StartingAssetId { get; init; }

    public int? FfmpegPid { get; init; }

    public DateTimeOffset? ProcessStartTimeUtc { get; init; }

    public double? ProcessStartElapsedMilliseconds { get; init; }

    public DateTimeOffset? FirstProgressTimeUtc { get; init; }

    public double? FirstProgressElapsedMilliseconds { get; init; }

    public DateTimeOffset? LastProgressTimeUtc { get; init; }

    public double? LastProgressElapsedMilliseconds { get; init; }

    public DateTimeOffset? LastAdvancingProgressTimeUtc { get; init; }

    public double? LastAdvancingProgressElapsedMilliseconds { get; init; }

    public TimeSpan? LatestOutputTime { get; init; }

    public long ProgressBatchCount { get; init; }

    public long AdvancingProgressBatchCount { get; init; }

    public long StagnantProgressBatchCount { get; init; }

    public FfmpegProgressState? LastProgressState { get; init; }

    public long? Frame { get; init; }

    public double? FramesPerSecond { get; init; }

    public double? BitrateKbitsPerSecond { get; init; }

    public long? TotalSizeBytes { get; init; }

    public long? DuplicateFrames { get; init; }

    public long? DroppedFrames { get; init; }

    public double? Speed { get; init; }

    public DateTimeOffset? ProcessExitTimeUtc { get; init; }

    public double? ProcessExitElapsedMilliseconds { get; init; }

    public int? ProcessExitCode { get; init; }

    public bool Cancelled { get; init; }

    public BroadcastFailureKind? FailureClassification { get; init; }

    public BroadcastRetryDecision? RetryDecision { get; init; }

    public int? RetryAttempt { get; init; }

    public TimeSpan? RetryDelay { get; init; }

    public IReadOnlyList<string> RecentStandardError { get; init; } = [];
}

public sealed record BroadcastDiagnosticsDocument
{
    public const int CurrentSchemaVersion = 1;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<BroadcastAttemptDiagnostics> Attempts { get; init; } = [];
}

public interface IBroadcastDiagnosticsStore
{
    BroadcastDiagnosticsDocument? ReadIfExists();

    Task WriteAsync(BroadcastDiagnosticsDocument document, CancellationToken cancellationToken);
}

public sealed class BroadcastDiagnosticsFileStore : IBroadcastDiagnosticsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly IAtomicTextFileWriter _writer;

    public BroadcastDiagnosticsFileStore(string path, IAtomicTextFileWriter? writer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _writer = writer ?? new AtomicTextFileWriter();
    }

    public BroadcastDiagnosticsDocument? ReadIfExists()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        BroadcastDiagnosticsDocument document = JsonSerializer.Deserialize<BroadcastDiagnosticsDocument>(
            File.ReadAllText(_path),
            JsonOptions) ?? throw new InvalidDataException("Broadcast diagnostics JSON is empty.");
        if (document.SchemaVersion != BroadcastDiagnosticsDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported broadcast diagnostics schemaVersion {document.SchemaVersion}.");
        }

        return document;
    }

    public Task WriteAsync(BroadcastDiagnosticsDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        BroadcastDiagnosticsDocument safeDocument = document with
        {
            Attempts = document.Attempts.Select(attempt => attempt with
            {
                StartingPlaylistFileName = BroadcastCredentialRedactor.Redact(attempt.StartingPlaylistFileName),
                StartingAssetId = BroadcastCredentialRedactor.Redact(attempt.StartingAssetId),
                RecentStandardError = attempt.RecentStandardError
                    .Select(line => BroadcastCredentialRedactor.RedactDiagnosticLine(line))
                    .TakeLast(BoundedDiagnosticBuffer.DefaultCapacity)
                    .ToArray(),
            }).ToArray(),
        };
        string json = JsonSerializer.Serialize(safeDocument, JsonOptions) + Environment.NewLine;
        return _writer.WriteAsync(_path, json, cancellationToken);
    }
}

public interface IBroadcastDiagnostics
{
    bool IsEnabled { get; }

    void BeginAttempt(
        string attemptId,
        BroadcastPlanItem startingItem,
        int startingGlobalItemIndex,
        BroadcastLaunchReason? launchReason);

    void RecordProcessStarted(string attemptId, int processId);

    void RecordProgress(string attemptId, FfmpegProgressBatch progress);

    void RecordStandardError(string attemptId, string line);

    void RecordProcessStopped(string attemptId);

    void RecordProcessExit(
        string attemptId,
        int? exitCode,
        bool cancelled,
        IReadOnlyList<string> recentStandardError);

    void RecordDecision(
        string attemptId,
        BroadcastFailureKind? failureClassification,
        BroadcastRetryDecision retryDecision,
        int? retryAttempt = null,
        TimeSpan? retryDelay = null);

    Task FlushAsync();
}

public sealed class BroadcastDiagnosticsRecorder : IBroadcastDiagnostics
{
    public const int DefaultMaximumAttempts = 20;
    public static readonly TimeSpan DefaultProgressPersistenceInterval = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly object _persistenceWorkerGate = new();
    private readonly List<BroadcastAttemptDiagnostics> _attempts;
    private readonly Dictionary<string, AttemptTiming> _timings = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly IBroadcastDiagnosticsStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _progressPersistenceInterval;
    private readonly int _maximumAttempts;
    private int _persistenceFailureCount;
    private bool _persistenceRequested;
    private Task? _persistenceWorker;

    public BroadcastDiagnosticsRecorder(
        IBroadcastDiagnosticsStore store,
        TimeProvider? timeProvider = null,
        TimeSpan? progressPersistenceInterval = null,
        int maximumAttempts = DefaultMaximumAttempts)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (maximumAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _progressPersistenceInterval = progressPersistenceInterval ?? DefaultProgressPersistenceInterval;
        if (_progressPersistenceInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(progressPersistenceInterval));
        }

        _maximumAttempts = maximumAttempts;
        try
        {
            _attempts = store.ReadIfExists()?.Attempts.TakeLast(maximumAttempts).ToList() ?? [];
        }
        catch
        {
            // Advisory diagnostics must never prevent or interrupt broadcasting.
            _attempts = [];
            _persistenceFailureCount = 1;
        }
    }

    public bool IsEnabled => true;

    public int PersistenceFailureCount => Volatile.Read(ref _persistenceFailureCount);

    public void BeginAttempt(
        string attemptId,
        BroadcastPlanItem startingItem,
        int startingGlobalItemIndex,
        BroadcastLaunchReason? launchReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        ArgumentNullException.ThrowIfNull(startingItem);
        long timestamp = _timeProvider.GetTimestamp();
        var attempt = new BroadcastAttemptDiagnostics
        {
            AttemptId = attemptId,
            LaunchTimeUtc = GetUtcNow(),
            LaunchReason = launchReason,
            StartingGlobalItemIndex = startingGlobalItemIndex,
            StartingPlaylistFileName = Path.GetFileName(startingItem.PlaylistPath),
            StartingSequence = startingItem.Sequence,
            StartingAssetId = startingItem.AssetId,
        };

        lock (_gate)
        {
            while (_attempts.Count >= _maximumAttempts)
            {
                string removedId = _attempts[0].AttemptId;
                _attempts.RemoveAt(0);
                _timings.Remove(removedId);
            }

            _attempts.Add(attempt);
            _timings[attemptId] = new AttemptTiming(timestamp);
        }

        QueuePersistence();
    }

    public void RecordProcessStarted(string attemptId, int processId)
    {
        Update(attemptId, (attempt, timing, now, elapsed) => attempt with
        {
            FfmpegPid = processId,
            ProcessStartTimeUtc = now,
            ProcessStartElapsedMilliseconds = elapsed.TotalMilliseconds,
        });
        QueuePersistence();
    }

    public void RecordProgress(string attemptId, FfmpegProgressBatch progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        bool shouldPersist = false;
        lock (_gate)
        {
            int index = FindAttemptIndex(attemptId);
            if (index < 0 || !_timings.TryGetValue(attemptId, out AttemptTiming? timing))
            {
                return;
            }

            BroadcastAttemptDiagnostics attempt = _attempts[index];
            long timestamp = _timeProvider.GetTimestamp();
            DateTimeOffset now = GetUtcNow();
            TimeSpan elapsed = _timeProvider.GetElapsedTime(timing.StartTimestamp, timestamp);
            bool firstProgress = attempt.FirstProgressTimeUtc is null;
            bool advancing = progress.OutputTime is not null
                && (timing.HighestOutputTime is null
                    ? progress.OutputTime > TimeSpan.Zero
                    : progress.OutputTime > timing.HighestOutputTime);
            bool stagnant = progress.OutputTime is not null && !advancing;
            if (advancing)
            {
                timing.HighestOutputTime = progress.OutputTime;
            }

            _attempts[index] = attempt with
            {
                FirstProgressTimeUtc = attempt.FirstProgressTimeUtc ?? now,
                FirstProgressElapsedMilliseconds = attempt.FirstProgressElapsedMilliseconds
                    ?? elapsed.TotalMilliseconds,
                LastProgressTimeUtc = now,
                LastProgressElapsedMilliseconds = elapsed.TotalMilliseconds,
                LastAdvancingProgressTimeUtc = advancing ? now : attempt.LastAdvancingProgressTimeUtc,
                LastAdvancingProgressElapsedMilliseconds = advancing
                    ? elapsed.TotalMilliseconds
                    : attempt.LastAdvancingProgressElapsedMilliseconds,
                LatestOutputTime = progress.OutputTime ?? attempt.LatestOutputTime,
                ProgressBatchCount = attempt.ProgressBatchCount + 1,
                AdvancingProgressBatchCount = attempt.AdvancingProgressBatchCount + (advancing ? 1 : 0),
                StagnantProgressBatchCount = attempt.StagnantProgressBatchCount + (stagnant ? 1 : 0),
                LastProgressState = progress.State,
                Frame = progress.Frame ?? attempt.Frame,
                FramesPerSecond = progress.FramesPerSecond ?? attempt.FramesPerSecond,
                BitrateKbitsPerSecond = progress.BitrateKbitsPerSecond ?? attempt.BitrateKbitsPerSecond,
                TotalSizeBytes = progress.TotalSizeBytes ?? attempt.TotalSizeBytes,
                DuplicateFrames = progress.DuplicateFrames ?? attempt.DuplicateFrames,
                DroppedFrames = progress.DroppedFrames ?? attempt.DroppedFrames,
                Speed = progress.Speed ?? attempt.Speed,
            };

            if (firstProgress
                || timing.LastProgressPersistenceTimestamp is null
                || _timeProvider.GetElapsedTime(timing.LastProgressPersistenceTimestamp.Value, timestamp)
                    >= _progressPersistenceInterval)
            {
                timing.LastProgressPersistenceTimestamp = timestamp;
                shouldPersist = true;
            }
        }

        if (shouldPersist)
        {
            QueuePersistence();
        }
    }

    public void RecordStandardError(string attemptId, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Update(attemptId, (attempt, _, _, _) =>
        {
            var lines = new Queue<string>(attempt.RecentStandardError);
            while (lines.Count >= BoundedDiagnosticBuffer.DefaultCapacity)
            {
                lines.Dequeue();
            }

            lines.Enqueue(BroadcastCredentialRedactor.RedactDiagnosticLine(line));
            return attempt with { RecentStandardError = lines.ToArray() };
        });
    }

    public void RecordProcessStopped(string attemptId)
    {
        Update(attemptId, (attempt, _, now, elapsed) => attempt with
        {
            ProcessExitTimeUtc = attempt.ProcessExitTimeUtc ?? now,
            ProcessExitElapsedMilliseconds = attempt.ProcessExitElapsedMilliseconds
                ?? elapsed.TotalMilliseconds,
        });
        QueuePersistence();
    }

    public void RecordProcessExit(
        string attemptId,
        int? exitCode,
        bool cancelled,
        IReadOnlyList<string> recentStandardError)
    {
        ArgumentNullException.ThrowIfNull(recentStandardError);
        Update(attemptId, (attempt, _, now, elapsed) => attempt with
        {
            ProcessExitTimeUtc = attempt.FfmpegPid is not null || exitCode is not null
                ? attempt.ProcessExitTimeUtc ?? now
                : attempt.ProcessExitTimeUtc,
            ProcessExitElapsedMilliseconds = attempt.FfmpegPid is not null || exitCode is not null
                ? attempt.ProcessExitElapsedMilliseconds ?? elapsed.TotalMilliseconds
                : attempt.ProcessExitElapsedMilliseconds,
            ProcessExitCode = exitCode,
            Cancelled = cancelled,
            RecentStandardError = recentStandardError
                .Select(line => BroadcastCredentialRedactor.RedactDiagnosticLine(line))
                .TakeLast(BoundedDiagnosticBuffer.DefaultCapacity)
                .ToArray(),
        });
        QueuePersistence();
    }

    public void RecordDecision(
        string attemptId,
        BroadcastFailureKind? failureClassification,
        BroadcastRetryDecision retryDecision,
        int? retryAttempt = null,
        TimeSpan? retryDelay = null)
    {
        Update(attemptId, (attempt, _, _, _) => attempt with
        {
            FailureClassification = failureClassification,
            RetryDecision = retryDecision,
            RetryAttempt = retryAttempt,
            RetryDelay = retryDelay,
        });
        QueuePersistence();
    }

    public BroadcastDiagnosticsDocument Snapshot()
    {
        lock (_gate)
        {
            return new BroadcastDiagnosticsDocument { Attempts = _attempts.ToArray() };
        }
    }

    public async Task FlushAsync()
    {
        QueuePersistence();
        while (true)
        {
            Task? worker;
            lock (_persistenceWorkerGate)
            {
                worker = _persistenceWorker;
            }

            if (worker is null)
            {
                return;
            }

            await worker.ConfigureAwait(false);
        }
    }

    private void Update(
        string attemptId,
        Func<BroadcastAttemptDiagnostics, AttemptTiming, DateTimeOffset, TimeSpan, BroadcastAttemptDiagnostics> update)
    {
        lock (_gate)
        {
            int index = FindAttemptIndex(attemptId);
            if (index < 0 || !_timings.TryGetValue(attemptId, out AttemptTiming? timing))
            {
                return;
            }

            long timestamp = _timeProvider.GetTimestamp();
            _attempts[index] = update(
                _attempts[index],
                timing,
                GetUtcNow(),
                _timeProvider.GetElapsedTime(timing.StartTimestamp, timestamp));
        }
    }

    private DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private int FindAttemptIndex(string attemptId) =>
        _attempts.FindIndex(attempt => string.Equals(
            attempt.AttemptId,
            attemptId,
            StringComparison.Ordinal));

    private void QueuePersistence()
    {
        lock (_persistenceWorkerGate)
        {
            _persistenceRequested = true;
            if (_persistenceWorker is null)
            {
                _persistenceWorker = Task.Run(PersistenceLoopAsync);
            }
        }
    }

    private async Task PersistenceLoopAsync()
    {
        while (true)
        {
            lock (_persistenceWorkerGate)
            {
                if (!_persistenceRequested)
                {
                    _persistenceWorker = null;
                    return;
                }

                _persistenceRequested = false;
            }

            await PersistLatestAsync().ConfigureAwait(false);
        }
    }

    private async Task PersistLatestAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            BroadcastDiagnosticsDocument snapshot = Snapshot();
            await _store.WriteAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Increment(ref _persistenceFailureCount);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private sealed class AttemptTiming(long startTimestamp)
    {
        public long StartTimestamp { get; } = startTimestamp;

        public long? LastProgressPersistenceTimestamp { get; set; }

        public TimeSpan? HighestOutputTime { get; set; }
    }
}

public sealed class DisabledBroadcastDiagnostics : IBroadcastDiagnostics
{
    public static DisabledBroadcastDiagnostics Instance { get; } = new();

    private DisabledBroadcastDiagnostics()
    {
    }

    public bool IsEnabled => false;

    public void BeginAttempt(
        string attemptId,
        BroadcastPlanItem startingItem,
        int startingGlobalItemIndex,
        BroadcastLaunchReason? launchReason)
    {
    }

    public void RecordProcessStarted(string attemptId, int processId)
    {
    }

    public void RecordProgress(string attemptId, FfmpegProgressBatch progress)
    {
    }

    public void RecordStandardError(string attemptId, string line)
    {
    }

    public void RecordProcessStopped(string attemptId)
    {
    }

    public void RecordProcessExit(
        string attemptId,
        int? exitCode,
        bool cancelled,
        IReadOnlyList<string> recentStandardError)
    {
    }

    public void RecordDecision(
        string attemptId,
        BroadcastFailureKind? failureClassification,
        BroadcastRetryDecision retryDecision,
        int? retryAttempt = null,
        TimeSpan? retryDelay = null)
    {
    }

    public Task FlushAsync() => Task.CompletedTask;
}

public static class BroadcastDiagnosticsConfiguration
{
    public const string DefaultEnvironmentVariable = "NZYTE_TV_BROADCAST_DIAGNOSTICS_PATH";

    public static IBroadcastDiagnostics CreateFromEnvironment() => Create(
        Environment.GetEnvironmentVariable(DefaultEnvironmentVariable));

    public static IBroadcastDiagnostics Create(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return DisabledBroadcastDiagnostics.Instance;
        }

        try
        {
            return new BroadcastDiagnosticsRecorder(
                new BroadcastDiagnosticsFileStore(configuredPath));
        }
        catch
        {
            // Invalid or unavailable advisory configuration cannot interrupt broadcasting.
            return DisabledBroadcastDiagnostics.Instance;
        }
    }
}
