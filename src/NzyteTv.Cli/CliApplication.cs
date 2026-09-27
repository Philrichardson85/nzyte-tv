using System.Globalization;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Cli;

public static class CliApplication
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        CommandParseResult parseResult = CommandLineParser.Parse(args);
        if (!parseResult.IsSuccess)
        {
            Console.Error.WriteLine($"Error: {parseResult.Error}");
            Console.Error.WriteLine("Run 'nzytetv --help' for usage.");
            return 2;
        }

        ParsedCommand command = parseResult.Command!;
        if (command.ShowHelp)
        {
            PrintHelp(command.Kind);
            return 0;
        }

        try
        {
            var locator = new MediaToolLocator();
            MediaToolPaths tools = await locator.LocateAsync(cancellationToken).ConfigureAwait(false);
            var runner = new ProcessRunner();
            var analyzer = new MediaAnalyzer(tools.Ffprobe, runner);
            var validator = new BroadcastStandardValidator();
            var verifier = new MediaVerifier(analyzer, validator);

            return command.Kind switch
            {
                CommandKind.Inspect => await InspectAsync(command.Input!, analyzer, cancellationToken).ConfigureAwait(false),
                CommandKind.Verify => await VerifyAsync(command.Input!, verifier, cancellationToken).ConfigureAwait(false),
                CommandKind.Normalize => await NormalizeAsync(
                    command.Input!, command.Overwrite, tools.Ffmpeg, runner, analyzer, verifier, cancellationToken).ConfigureAwait(false),
                CommandKind.NormalizeLibrary => await NormalizeLibraryAsync(
                    command.Input!,
                    command.Destination!,
                    command.Overwrite,
                    tools.Ffmpeg,
                    runner,
                    analyzer,
                    verifier,
                    cancellationToken).ConfigureAwait(false),
                _ => 2,
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (NormalizationVerificationException exception)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(exception.Message);
            PrintVerification(exception.Verification.Result);
            return 1;
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException or
            MediaToolNotFoundException or ProcessExecutionException or MediaProbeException or
            MediaNormalizationException or FfprobeDataException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"FAILED: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> InspectAsync(string input, IMediaAnalyzer analyzer, CancellationToken cancellationToken)
    {
        MediaDescription media = await analyzer.InspectAsync(input, cancellationToken).ConfigureAwait(false);
        Console.WriteLine("NZYTE TV Media Inspection");
        Console.WriteLine();
        Console.WriteLine($"File:      {media.FilePath}");
        Console.WriteLine($"Duration:  {FormatDuration(media.Duration)}");
        Console.WriteLine($"Container: {media.Container}");
        Console.WriteLine($"File size: {FormatFileSize(media.FileSize)} ({media.FileSize:N0} bytes)");

        Console.WriteLine();
        Console.WriteLine("Video");
        if (media.Video is null)
        {
            Console.WriteLine("  No video stream");
        }
        else
        {
            Console.WriteLine($"  Codec:        {media.Video.Codec}");
            Console.WriteLine($"  Profile:      {media.Video.Profile ?? "unknown"}");
            Console.WriteLine($"  Resolution:   {media.Video.Width}x{media.Video.Height}");
            Console.WriteLine($"  Pixel format: {media.Video.PixelFormat}");
            Console.WriteLine($"  Frame rate:   {FormatFrameRate(media.Video.FrameRate)}");
            Console.WriteLine($"  Bitrate:      {FormatBitrate(media.Video.BitRate)}");
        }

        Console.WriteLine();
        Console.WriteLine("Audio");
        if (media.Audio is null)
        {
            Console.WriteLine("  No audio stream");
        }
        else
        {
            Console.WriteLine($"  Codec:       {media.Audio.Codec}");
            Console.WriteLine($"  Sample rate: {(media.Audio.SampleRate.HasValue ? $"{media.Audio.SampleRate:N0} Hz" : "unknown")}");
            Console.WriteLine($"  Channels:    {media.Audio.Channels?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
            Console.WriteLine($"  Bitrate:     {FormatBitrate(media.Audio.BitRate)}");
        }

        return 0;
    }

    private static async Task<int> VerifyAsync(string input, IMediaVerifier verifier, CancellationToken cancellationToken)
    {
        MediaVerification verification = await verifier.VerifyAsync(input, cancellationToken).ConfigureAwait(false);
        PrintVerification(verification.Result);
        return verification.Result.IsBroadcastReady ? 0 : 1;
    }

    private static async Task<int> NormalizeAsync(
        string input,
        bool overwrite,
        string ffmpegPath,
        IProcessRunner runner,
        IMediaAnalyzer analyzer,
        IMediaVerifier verifier,
        CancellationToken cancellationToken)
    {
        string output = MediaPathPolicy.GetDefaultOutputPath(input, Environment.CurrentDirectory);
        Console.WriteLine($"Source:      {Path.GetFullPath(input)}");
        Console.WriteLine($"Destination: {output}");
        Console.WriteLine();

        var normalizer = new MediaNormalizer(ffmpegPath, runner, analyzer, verifier);
        var progress = new InlineProgress<NormalizationProgress>(value =>
        {
            string percentage = value.Percent.HasValue ? $" {value.Percent.Value,6:0.0}%" : string.Empty;
            Console.Write($"\r{value.Stage,-12} elapsed {value.Elapsed:hh\\:mm\\:ss}{percentage}   ");
            if (value.Stage is "Complete" or "Verifying")
            {
                Console.WriteLine();
            }
        });

        NormalizationResult result = await normalizer.NormalizeAsync(
            input, output, overwrite, progress, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        PrintVerification(result.Verification.Result);
        Console.WriteLine($"Output: {result.OutputPath}");
        Console.WriteLine($"Elapsed: {result.Elapsed:hh\\:mm\\:ss}");
        return 0;
    }

    private static async Task<int> NormalizeLibraryAsync(
        string sourceRoot,
        string destinationRoot,
        bool overwrite,
        string ffmpegPath,
        IProcessRunner runner,
        IMediaAnalyzer analyzer,
        IMediaVerifier verifier,
        CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(sourceRoot);
        string destination = Path.GetFullPath(destinationRoot);
        Console.WriteLine("NZYTE TV Library Normalization");
        Console.WriteLine();
        Console.WriteLine($"Source root:      {source}");
        Console.WriteLine($"Destination root: {destination}");
        Console.WriteLine($"Overwrite:         {(overwrite ? "yes" : "no")}");
        Console.WriteLine();

        var fileNormalizer = new MediaNormalizer(ffmpegPath, runner, analyzer, verifier);
        var libraryNormalizer = new MediaLibraryNormalizer(
            new MediaLibraryDiscovery(),
            fileNormalizer,
            verifier,
            new SourceManifestStore());
        var progress = new InlineProgress<LibraryNormalizationProgress>(PrintLibraryProgress);

        LibraryNormalizationResult result = await libraryNormalizer.NormalizeAsync(
            source,
            destination,
            overwrite,
            progress,
            cancellationToken).ConfigureAwait(false);

        PrintLibrarySummary(result);
        return result.ExitCode;
    }

    public static void PrintVerification(VerificationResult result)
    {
        Console.WriteLine("NZYTE TV Broadcast Verification");
        foreach (IGrouping<string, VerificationCheck> section in result.Checks.GroupBy(check => check.Section))
        {
            Console.WriteLine();
            Console.WriteLine(section.Key);
            foreach (VerificationCheck check in section)
            {
                string detail = !check.Passed && !string.IsNullOrWhiteSpace(check.Detail)
                    ? $" ({check.Detail})"
                    : string.Empty;
                Console.WriteLine($"{check.Label,-24} {(check.Passed ? "PASS" : "FAIL")}{detail}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(result.IsBroadcastReady ? "RESULT: BROADCAST READY" : "RESULT: NOT BROADCAST READY");
    }

    private static void PrintHelp(CommandKind command)
    {
        if (command == CommandKind.RootHelp)
        {
            Console.WriteLine("NZYTE TV media normalization and verification");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  nzytetv inspect <input>");
            Console.WriteLine("  nzytetv normalize <input> [--overwrite]");
            Console.WriteLine("  nzytetv normalize-library <source-root> <destination-root> [--overwrite]");
            Console.WriteLine("  nzytetv verify <input>");
            Console.WriteLine();
            Console.WriteLine("Run 'nzytetv <command> --help' for command-specific help.");
            return;
        }

        switch (command)
        {
            case CommandKind.Inspect:
                Console.WriteLine("Usage: nzytetv inspect <input>");
                Console.WriteLine("Inspect media metadata using FFprobe JSON output.");
                break;
            case CommandKind.Normalize:
                Console.WriteLine("Usage: nzytetv normalize <input> [--overwrite]");
                Console.WriteLine("Create ./BroadcastReady/<original-name>.mp4 and verify it.");
                Console.WriteLine("--overwrite  Replace an existing destination; the source is never replaced.");
                break;
            case CommandKind.NormalizeLibrary:
                Console.WriteLine("Usage: nzytetv normalize-library <source-root> <destination-root> [--overwrite]");
                Console.WriteLine("Recursively normalize supported videos while preserving relative folders.");
                Console.WriteLine("--overwrite  Replace existing destinations; source files are never replaced.");
                break;
            case CommandKind.Verify:
                Console.WriteLine("Usage: nzytetv verify <input>");
                Console.WriteLine("Independently verify media and actual keyframe timestamps with FFprobe.");
                break;
            default:
                break;
        }
    }

    private static string FormatDuration(TimeSpan? duration) =>
        duration?.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture) ?? "unknown";

    private static string FormatFrameRate(string? rational) =>
        RationalNumber.TryParse(rational, out double fps) ? $"{fps:0.###} fps ({rational})" : rational ?? "unknown";

    private static string FormatBitrate(long? bitsPerSecond) =>
        bitsPerSecond.HasValue ? $"{bitsPerSecond.Value / 1000d:0.#} kbps" : "unknown";

    private static string FormatFileSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:0.##} GiB",
        >= 1_048_576 => $"{bytes / 1_048_576d:0.##} MiB",
        >= 1024 => $"{bytes / 1024d:0.##} KiB",
        _ => $"{bytes} B",
    };

    private static void PrintLibraryProgress(LibraryNormalizationProgress value)
    {
        switch (value.Stage)
        {
            case LibraryProgressStage.Starting:
                Console.WriteLine($"[{value.Index}/{value.Total}] Normalizing");
                Console.WriteLine($"Source:      {value.SourcePath}");
                Console.WriteLine($"Destination: {value.DestinationPath}");
                break;
            case LibraryProgressStage.Normalizing:
                string percentage = value.Percent.HasValue ? $" {value.Percent.Value,6:0.0}%" : string.Empty;
                Console.Write($"\r{value.Detail ?? "Working",-12} elapsed {FormatElapsed(value.FileElapsed)}{percentage}   ");
                break;
            case LibraryProgressStage.VerifyingExisting:
                Console.WriteLine("Checking existing destination verification...");
                break;
            case LibraryProgressStage.RefreshingExisting:
                Console.WriteLine($"Refresh:      {value.Detail}");
                break;
            case LibraryProgressStage.Complete:
                Console.WriteLine();
                Console.WriteLine($"Complete:     {FormatElapsed(value.FileElapsed)}");
                Console.WriteLine("Verification: PASS");
                Console.WriteLine();
                break;
            case LibraryProgressStage.Skipped:
                Console.WriteLine($"Skipped:      {value.Detail}");
                Console.WriteLine("Verification: PASS");
                Console.WriteLine();
                break;
            case LibraryProgressStage.Failed:
                Console.WriteLine();
                Console.WriteLine($"Failed:       {value.Detail}");
                Console.WriteLine();
                break;
            default:
                break;
        }
    }

    private static void PrintLibrarySummary(LibraryNormalizationResult result)
    {
        Console.WriteLine("NZYTE TV Library Normalization Summary");
        Console.WriteLine();
        Console.WriteLine($"Discovered video files: {result.DiscoveredVideoFiles,6}");
        Console.WriteLine($"Normalized:             {result.Normalized,6}");
        Console.WriteLine($"Skipped existing:       {result.SkippedExisting,6}");
        Console.WriteLine($"Failed:                 {result.Failed,6}");
        Console.WriteLine($"Verified ready:         {result.VerifiedReady,6}");
        Console.WriteLine($"Elapsed:                {FormatElapsed(result.Elapsed),6}");

        LibraryFileResult[] failures = result.Files
            .Where(file => file.Status == LibraryFileStatus.Failed)
            .ToArray();
        if (failures.Length == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Failures:");
        foreach (LibraryFileResult failure in failures)
        {
            Console.WriteLine($"- {failure.SourcePath}");
            Console.WriteLine($"  Destination: {failure.DestinationPath}");
            Console.WriteLine($"  Reason: {failure.FailureReason}");
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
