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
            "-stats",
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

        string? concatPath = null;
        try
        {
            concatPath = await FfmpegConcatFile.CreateTemporaryAsync(
                plan.Items.Select(item => item.MediaPath),
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> arguments = BroadcastFfmpegArgumentBuilder.Build(concatPath, destination);
            Action<string>? safeOutput = onOutput is null
                ? null
                : line => onOutput(Redact(line, destination));
            ProcessResult result = await _processRunner.RunAsync(
                new ProcessRequest(
                    _ffmpegPath,
                    arguments,
                    OnStandardOutput: safeOutput,
                    OnStandardError: safeOutput),
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new BroadcastProcessException(result.ExitCode);
            }

            return new BroadcastExecutionResult(result.ExitCode);
        }
        finally
        {
            if (concatPath is not null && File.Exists(concatPath))
            {
                File.Delete(concatPath);
            }
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
