using NzyteTv.Core;

namespace NzyteTv.Media;

public interface IMediaAnalyzer
{
    Task<MediaDescription> InspectAsync(string filePath, CancellationToken cancellationToken);

    Task<MediaDescription> AnalyzeForVerificationAsync(string filePath, CancellationToken cancellationToken);
}

public sealed class MediaAnalyzer : IMediaAnalyzer
{
    private readonly string _ffprobePath;
    private readonly IProcessRunner _processRunner;

    public MediaAnalyzer(string ffprobePath, IProcessRunner processRunner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffprobePath);
        _ffprobePath = ffprobePath;
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<MediaDescription> InspectAsync(string filePath, CancellationToken cancellationToken)
    {
        EnsureInputExists(filePath);
        ProcessResult result = await _processRunner.RunAsync(
            new ProcessRequest(_ffprobePath,
            [
                "-v", "error",
                "-print_format", "json",
                "-show_format",
                "-show_streams",
                Path.GetFullPath(filePath),
            ]),
            cancellationToken).ConfigureAwait(false);

        EnsureProbeSucceeded(result, filePath);
        return FfprobeJsonParser.ParseMedia(result.StandardOutput, filePath);
    }

    public async Task<MediaDescription> AnalyzeForVerificationAsync(string filePath, CancellationToken cancellationToken)
    {
        MediaDescription media = await InspectAsync(filePath, cancellationToken).ConfigureAwait(false);
        ProcessResult result = await _processRunner.RunAsync(
            new ProcessRequest(_ffprobePath,
            [
                "-v", "error",
                "-select_streams", "v:0",
                "-skip_frame", "nokey",
                "-show_frames",
                "-show_entries", "frame=best_effort_timestamp_time,pts_time",
                "-print_format", "json",
                Path.GetFullPath(filePath),
            ]),
            cancellationToken).ConfigureAwait(false);

        EnsureProbeSucceeded(result, filePath);
        return media with { KeyframeTimestamps = FfprobeJsonParser.ParseKeyframes(result.StandardOutput) };
    }

    private static void EnsureInputExists(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Input media file not found: {Path.GetFullPath(filePath)}", filePath);
        }
    }

    private static void EnsureProbeSucceeded(ProcessResult result, string filePath)
    {
        if (result.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? "No diagnostic output was provided."
                : result.StandardError.Trim();
            throw new MediaProbeException($"FFprobe could not inspect '{Path.GetFullPath(filePath)}': {detail}");
        }
    }
}

public sealed class MediaProbeException : Exception
{
    public MediaProbeException(string message)
        : base(message)
    {
    }
}
