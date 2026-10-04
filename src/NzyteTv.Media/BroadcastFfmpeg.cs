using System.Globalization;
using System.Text;
using NzyteTv.Core;

namespace NzyteTv.Media;

public static class BroadcastFfmpegArgumentBuilder
{
    public static IReadOnlyList<string> Build(string concatPath, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(concatPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        return
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-progress", "pipe:1",
            "-nostats",
            "-re",
            "-f", "concat",
            "-safe", "0",
            "-i", Path.GetFullPath(concatPath),
            "-c", "copy",
            "-flvflags", "no_duration_filesize",
            "-f", "flv",
            destination,
        ];
    }
}

public static class FfmpegConcatFile
{
    public static string BuildContent(IEnumerable<string> mediaPaths)
    {
        ArgumentNullException.ThrowIfNull(mediaPaths);
        var content = new StringBuilder("ffconcat version 1.0\n");
        foreach (string mediaPath in mediaPaths)
        {
            string normalized = Path.GetFullPath(mediaPath).Replace('\\', '/');
            string escaped = normalized.Replace("'", "'\\''", StringComparison.Ordinal);
            content.Append("file '").Append(escaped).Append("'\n");
        }

        return content.ToString();
    }

    public static async Task<string> CreateTemporaryAsync(
        IEnumerable<string> mediaPaths,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(Path.GetTempPath(), $"nzytetv-broadcast-{Guid.NewGuid():N}.ffconcat");
        try
        {
            await File.WriteAllTextAsync(
                path,
                BuildContent(mediaPaths),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            throw;
        }
    }
}

public static class BroadcastDestination
{
    public const string DefaultEnvironmentVariable = "NZYTE_TV_RTMP_URL";

    public static BroadcastDestinationStatus GetStatus(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return BroadcastDestinationStatus.NotConfigured;
        }

        try
        {
            _ = Resolve(configuredValue, dryRun: false);
            return BroadcastDestinationStatus.Valid;
        }
        catch (InvalidOperationException)
        {
            return BroadcastDestinationStatus.Invalid;
        }
    }

    public static string? Resolve(string? configuredValue, bool dryRun)
    {
        if (dryRun)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            throw new InvalidOperationException(
                $"Broadcast destination environment variable {DefaultEnvironmentVariable} is not configured.");
        }

        if (!Uri.TryCreate(configuredValue, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("rtmp" or "rtmps"))
        {
            throw new InvalidOperationException(
                $"Broadcast destination environment variable {DefaultEnvironmentVariable} must contain a valid RTMP or RTMPS URL.");
        }

        return configuredValue;
    }
}

public enum BroadcastDestinationStatus
{
    NotConfigured,
    Valid,
    Invalid,
}

public sealed record BroadcastAttemptResult(
    int FfmpegExitCode,
    TimeSpan? OutputTime,
    string Diagnostic,
    int StartItemIndex)
{
    public string AttemptId { get; init; } = string.Empty;
}

public static class FfmpegProgressParser
{
    public static bool TryParseOutputTime(string line, out TimeSpan outputTime)
    {
        outputTime = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        int separator = line.IndexOf('=');
        if (separator <= 0) return false;
        string key = line[..separator];
        string value = line[(separator + 1)..];
        if (key == "out_time_us"
            && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds)
            && microseconds >= 0)
        {
            outputTime = TimeSpan.FromMicroseconds(microseconds);
            return true;
        }

        if (key == "out_time"
            && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan parsed)
            && parsed >= TimeSpan.Zero)
        {
            outputTime = parsed;
            return true;
        }

        return false;
    }
}

public enum BroadcastFailureKind { None, Cancelled, Transient, Permanent, Ambiguous }

public static class BroadcastFailureClassifier
{
    private static readonly string[] TransientMarkers =
    [
        "connection reset", "broken pipe", "connection timed out", "connection refused",
        "network is unreachable", "temporary failure", "network error", "i/o error",
        "error muxing a packet", "error submitting a packet", "error writing trailer",
    ];
    private static readonly string[] PermanentMarkers =
    ["no such file", "invalid data found", "invalid concat", "not found", "unknown encoder"];

    public static BroadcastFailureKind Classify(int exitCode, string diagnostic, bool cancelled)
    {
        if (cancelled) return BroadcastFailureKind.Cancelled;
        if (exitCode == 0) return BroadcastFailureKind.None;
        if (TransientMarkers.Any(marker => diagnostic.Contains(marker, StringComparison.OrdinalIgnoreCase))) return BroadcastFailureKind.Transient;
        if (PermanentMarkers.Any(marker => diagnostic.Contains(marker, StringComparison.OrdinalIgnoreCase))) return BroadcastFailureKind.Permanent;
        return BroadcastFailureKind.Ambiguous;
    }
}

public sealed class FfmpegBroadcaster
{
    private readonly string _ffmpegPath;
    private readonly IProcessRunner _processRunner;
    private readonly IBroadcastDiagnostics _diagnostics;

    public FfmpegBroadcaster(
        string ffmpegPath,
        IProcessRunner processRunner,
        IBroadcastDiagnostics? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentNullException.ThrowIfNull(processRunner);
        _ffmpegPath = ffmpegPath;
        _processRunner = processRunner;
        _diagnostics = diagnostics ?? DisabledBroadcastDiagnostics.Instance;
    }

    public IBroadcastDiagnostics Diagnostics => _diagnostics;

    internal async Task FlushDiagnosticsAsync()
    {
        try
        {
            await _diagnostics.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics are advisory and must not alter broadcast outcomes.
        }
    }

    public async Task<BroadcastAttemptResult> BroadcastAttemptAsync(
        BroadcastPlan plan,
        string destination,
        int startItemIndex,
        Action<string>? onOutput,
        Action<TimeSpan>? onProgress,
        CancellationToken cancellationToken)
    {
        return await BroadcastAttemptAsync(
            plan,
            destination,
            startItemIndex,
            onOutput,
            onProgress,
            observer: null,
            launchReason: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BroadcastAttemptResult> BroadcastAttemptAsync(
        BroadcastPlan plan,
        string destination,
        int startItemIndex,
        Action<string>? onOutput,
        Action<TimeSpan>? onProgress,
        IBroadcastRuntimeObserver? observer,
        CancellationToken cancellationToken)
    {
        return await BroadcastAttemptAsync(
            plan,
            destination,
            startItemIndex,
            onOutput,
            onProgress,
            observer,
            launchReason: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BroadcastAttemptResult> BroadcastAttemptAsync(
        BroadcastPlan plan,
        string destination,
        int startItemIndex,
        Action<string>? onOutput,
        Action<TimeSpan>? onProgress,
        IBroadcastRuntimeObserver? observer,
        BroadcastLaunchReason? launchReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (!plan.IsReady) throw new InvalidOperationException("Broadcast plan is not ready.");
        if (startItemIndex < 0 || startItemIndex >= plan.Items.Count) throw new ArgumentOutOfRangeException(nameof(startItemIndex));

        string attemptId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string? concatPath = null;
        TimeSpan? latestOutputTime = null;
        var progressParser = new FfmpegProgressBatchParser();
        var progressGate = new object();
        var diagnosticBuffer = new BoundedDiagnosticBuffer(
            sanitize: line => BroadcastCredentialRedactor.Redact(line, destination));
        _diagnostics.BeginAttempt(
            attemptId,
            plan.Items[startItemIndex],
            startItemIndex,
            launchReason);
        try
        {
            concatPath = await FfmpegConcatFile.CreateTemporaryAsync(
                plan.Items.Skip(startItemIndex).Select(item => item.MediaPath), cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> arguments = BroadcastFfmpegArgumentBuilder.Build(concatPath, destination);
            void HandleProgress(string line)
            {
                string safe = BroadcastCredentialRedactor.RedactDiagnosticLine(line, destination);
                if (!FfmpegProgressParser.TryParseOutputTime(safe, out _)
                    && !string.IsNullOrWhiteSpace(safe))
                {
                    // Preserve the existing live console stream without treating stdout as
                    // failure diagnostics or allowing it to consume the stderr buffer.
                    onOutput?.Invoke(safe);
                }

                if (!progressParser.TryAddLine(line, out FfmpegProgressBatch? batch) || batch is null)
                {
                    return;
                }

                _diagnostics.RecordProgress(attemptId, batch);
                if (batch.OutputTime is TimeSpan outputTime)
                {
                    lock (progressGate)
                    {
                        latestOutputTime = outputTime;
                    }

                    onProgress?.Invoke(outputTime);
                }
            }

            void HandleDiagnostic(string line)
            {
                string safe = BroadcastCredentialRedactor.RedactDiagnosticLine(line, destination);
                diagnosticBuffer.Add(safe);
                _diagnostics.RecordStandardError(attemptId, safe);
                if (!string.IsNullOrWhiteSpace(safe))
                {
                    onOutput?.Invoke(safe);
                }
            }

            ProcessResult result = await _processRunner.RunAsync(new ProcessRequest(
                _ffmpegPath,
                arguments,
                HandleProgress,
                HandleDiagnostic,
                processId =>
                {
                    _diagnostics.RecordProcessStarted(attemptId, processId);
                    observer?.OnEvent(new BroadcastRuntimeEvent(
                        BroadcastRuntimeEventKind.FfmpegProcessStarted,
                        FfmpegPid: processId));
                },
                processId =>
                {
                    _diagnostics.RecordProcessStopped(attemptId);
                    observer?.OnEvent(new BroadcastRuntimeEvent(
                        BroadcastRuntimeEventKind.FfmpegProcessStopped,
                        FfmpegPid: processId));
                },
                CaptureStandardOutput: false,
                CaptureStandardError: false), cancellationToken).ConfigureAwait(false);
            // Only the parent cancellation state makes this a cancellation. In particular,
            // FFmpeg exit 255 without a requested token is an unexpected child failure.
            cancellationToken.ThrowIfCancellationRequested();
            if (diagnosticBuffer.Snapshot().Count == 0 && !string.IsNullOrWhiteSpace(result.StandardError))
            {
                foreach (string line in result.StandardError.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    diagnosticBuffer.Add(line);
                }
            }

            IReadOnlyList<string> diagnosticLines = diagnosticBuffer.Snapshot();
            string diagnostic = string.Join(Environment.NewLine, diagnosticLines);
            _diagnostics.RecordProcessExit(
                attemptId,
                result.ExitCode,
                cancelled: false,
                diagnosticLines);
            if (result.ExitCode == 0)
            {
                await FlushDiagnosticsAsync().ConfigureAwait(false);
            }

            lock (progressGate)
            {
                return new BroadcastAttemptResult(
                    result.ExitCode,
                    latestOutputTime,
                    diagnostic,
                    startItemIndex)
                {
                    AttemptId = attemptId,
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _diagnostics.RecordProcessExit(
                attemptId,
                exitCode: null,
                cancelled: true,
                diagnosticBuffer.Snapshot());
            _diagnostics.RecordDecision(
                attemptId,
                BroadcastFailureKind.Cancelled,
                BroadcastRetryDecision.Cancelled);
            await FlushDiagnosticsAsync().ConfigureAwait(false);
            throw;
        }
        catch
        {
            _diagnostics.RecordProcessExit(
                attemptId,
                exitCode: null,
                cancelled: false,
                diagnosticBuffer.Snapshot());
            _diagnostics.RecordDecision(
                attemptId,
                failureClassification: null,
                BroadcastRetryDecision.LaunchFailed);
            await FlushDiagnosticsAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (concatPath is not null && File.Exists(concatPath)) File.Delete(concatPath);
        }
    }
}
