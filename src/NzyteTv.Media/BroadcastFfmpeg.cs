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

public sealed record BroadcastExecutionResult(int FfmpegExitCode);

public sealed record BroadcastAttemptResult(
    int FfmpegExitCode,
    TimeSpan? OutputTime,
    string Diagnostic,
    int StartItemIndex);

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
        if (key == "out_time_us" && long.TryParse(value, out long microseconds) && microseconds >= 0)
        {
            outputTime = TimeSpan.FromMicroseconds(microseconds);
            return true;
        }

        if (key == "out_time" && TimeSpan.TryParse(value, out TimeSpan parsed) && parsed >= TimeSpan.Zero)
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

    public FfmpegBroadcaster(string ffmpegPath, IProcessRunner processRunner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentNullException.ThrowIfNull(processRunner);
        _ffmpegPath = ffmpegPath;
        _processRunner = processRunner;
    }

    public async Task<BroadcastExecutionResult> BroadcastAsync(
        BroadcastPlan plan,
        string destination,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (!plan.IsReady)
        {
            throw new InvalidOperationException("Broadcast plan is not ready.");
        }

        BroadcastAttemptResult result = await BroadcastAttemptAsync(
            plan, destination, 0, onOutput, onProgress: null, cancellationToken).ConfigureAwait(false);
        if (result.FfmpegExitCode != 0) throw new BroadcastProcessException(result.FfmpegExitCode);
        return new BroadcastExecutionResult(result.FfmpegExitCode);
    }

    public async Task<BroadcastAttemptResult> BroadcastAttemptAsync(
        BroadcastPlan plan,
        string destination,
        int startItemIndex,
        Action<string>? onOutput,
        Action<TimeSpan>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (!plan.IsReady) throw new InvalidOperationException("Broadcast plan is not ready.");
        if (startItemIndex < 0 || startItemIndex >= plan.Items.Count) throw new ArgumentOutOfRangeException(nameof(startItemIndex));

        string? concatPath = null;
        TimeSpan? latestOutputTime = null;
        var diagnostics = new List<string>();
        try
        {
            concatPath = await FfmpegConcatFile.CreateTemporaryAsync(
                plan.Items.Skip(startItemIndex).Select(item => item.MediaPath), cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> arguments = BroadcastFfmpegArgumentBuilder.Build(concatPath, destination);
            void HandleOutput(string line)
            {
                string safe = Redact(line, destination);
                if (FfmpegProgressParser.TryParseOutputTime(safe, out TimeSpan outputTime))
                {
                    latestOutputTime = outputTime;
                    onProgress?.Invoke(outputTime);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(safe))
                {
                    if (diagnostics.Count < 12) diagnostics.Add(safe);
                    onOutput?.Invoke(safe);
                }
            }
            ProcessResult result = await _processRunner.RunAsync(new ProcessRequest(
                _ffmpegPath, arguments, HandleOutput, HandleOutput), cancellationToken).ConfigureAwait(false);
            string diagnostic = string.Join(Environment.NewLine, diagnostics);
            if (string.IsNullOrWhiteSpace(diagnostic)) diagnostic = Redact(result.StandardError, destination);
            return new BroadcastAttemptResult(result.ExitCode, latestOutputTime, diagnostic, startItemIndex);
        }
        finally
        {
            if (concatPath is not null && File.Exists(concatPath)) File.Delete(concatPath);
        }
    }

    private static string Redact(string value, string secret) =>
        value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
}

public sealed class BroadcastProcessException : InvalidOperationException
{
    public BroadcastProcessException(int exitCode)
        : base($"FFmpeg broadcast exited with status {exitCode}.")
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
