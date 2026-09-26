using System.Diagnostics;
using System.Globalization;
using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record NormalizationProgress(string Stage, TimeSpan Elapsed, double? Percent);

public sealed record NormalizationResult(string OutputPath, TimeSpan Elapsed, MediaVerification Verification);

public interface IMediaNormalizer
{
    Task<NormalizationResult> NormalizeAsync(
        string inputPath,
        string outputPath,
        bool overwrite,
        IProgress<NormalizationProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class MediaNormalizer : IMediaNormalizer
{
    private readonly string _ffmpegPath;
    private readonly IProcessRunner _processRunner;
    private readonly IMediaAnalyzer _analyzer;
    private readonly IMediaVerifier _verifier;

    public MediaNormalizer(
        string ffmpegPath,
        IProcessRunner processRunner,
        IMediaAnalyzer analyzer,
        IMediaVerifier verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        _ffmpegPath = ffmpegPath;
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    public async Task<NormalizationResult> NormalizeAsync(
        string inputPath,
        string outputPath,
        bool overwrite,
        IProgress<NormalizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Input media file not found: {Path.GetFullPath(inputPath)}", inputPath);
        }

        string source = Path.GetFullPath(inputPath);
        string destination = Path.GetFullPath(outputPath);
        MediaPathPolicy.EnsureOutputIsAllowed(source, destination, overwrite);
        string destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The output path has no parent directory.");
        Directory.CreateDirectory(destinationDirectory);

        string temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.partial.mp4");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            MediaDescription sourceMedia = await _analyzer.InspectAsync(source, cancellationToken).ConfigureAwait(false);
            if (sourceMedia.Video is null)
            {
                throw new MediaNormalizationException("The source contains no video stream.");
            }

            progress?.Report(new NormalizationProgress("Encoding", stopwatch.Elapsed, 0));
            IReadOnlyList<string> arguments = FfmpegArgumentBuilder.BuildNormalizeArguments(
                source,
                temporaryPath,
                sourceMedia.Audio is not null);

            ProcessResult encodeResult = await _processRunner.RunAsync(
                new ProcessRequest(
                    _ffmpegPath,
                    arguments,
                    line => ReportProgress(line, sourceMedia.Duration, stopwatch, progress)),
                cancellationToken).ConfigureAwait(false);

            if (encodeResult.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(encodeResult.StandardError)
                    ? "No diagnostic output was provided."
                    : encodeResult.StandardError.Trim();
                throw new MediaNormalizationException($"FFmpeg failed with exit code {encodeResult.ExitCode}: {detail}");
            }

            progress?.Report(new NormalizationProgress("Verifying", stopwatch.Elapsed, 100));
            MediaVerification verification = await _verifier.VerifyAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!verification.Result.IsBroadcastReady)
            {
                throw new NormalizationVerificationException(verification);
            }

            File.Move(temporaryPath, destination, overwrite);
            stopwatch.Stop();
            progress?.Report(new NormalizationProgress("Complete", stopwatch.Elapsed, 100));
            return new NormalizationResult(destination, stopwatch.Elapsed, verification);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ReportProgress(
        string line,
        TimeSpan? duration,
        Stopwatch stopwatch,
        IProgress<NormalizationProgress>? progress)
    {
        if (progress is null || duration is null || duration.Value.TotalSeconds <= 0
            || !line.StartsWith("out_time_us=", StringComparison.Ordinal)
            || !long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds))
        {
            return;
        }

        double percent = Math.Clamp(
            microseconds / 1_000_000d / duration.Value.TotalSeconds * 100d,
            0d,
            100d);
        progress.Report(new NormalizationProgress("Encoding", stopwatch.Elapsed, percent));
    }
}

public class MediaNormalizationException : Exception
{
    public MediaNormalizationException(string message)
        : base(message)
    {
    }
}

public sealed class NormalizationVerificationException : MediaNormalizationException
{
    public NormalizationVerificationException(MediaVerification verification)
        : base("FFmpeg completed, but the normalized file failed broadcast verification.")
    {
        Verification = verification;
    }

    public MediaVerification Verification { get; }
}
