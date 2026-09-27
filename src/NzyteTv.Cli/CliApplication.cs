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
            if (command.Kind is CommandKind.MetadataInitialize
                or CommandKind.MetadataReview
                or CommandKind.MetadataSync
                or CommandKind.MetadataRebind
                or CommandKind.MetadataEdit)
            {
                return await RunMetadataCommandAsync(command, cancellationToken).ConfigureAwait(false);
            }

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
            InvalidDataException or CatalogValidationException or AssetMetadataValidationException or
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

    private static async Task<int> RunMetadataCommandAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        var discovery = new MetadataAssetDiscovery();
        var metadataStore = new AssetMetadataStore();
        var synchronizer = new MetadataSynchronizer(discovery, metadataStore);

        switch (command.Kind)
        {
            case CommandKind.MetadataInitialize:
                var initializer = new MetadataInitializer(
                    new SongCatalogStore(),
                    discovery,
                    metadataStore,
                    synchronizer);
                MetadataInitializationResult initialized = await initializer.InitializeAsync(
                    command.Input!,
                    command.Destination!,
                    command.CatalogPath!,
                    command.DryRun,
                    cancellationToken).ConfigureAwait(false);
                PrintMetadataInitialization(initialized, command.Input!);
                return initialized.ExitCode;

            case CommandKind.MetadataReview:
                var reviewer = new MetadataReviewer(
                    new SongCatalogStore(),
                    discovery,
                    metadataStore,
                    synchronizer,
                    new ConsoleMetadataReviewPrompt());
                MetadataReviewResult reviewed = await reviewer.ReviewAsync(
                    command.Input!,
                    command.Destination!,
                    command.CatalogPath!,
                    cancellationToken).ConfigureAwait(false);
                PrintMetadataReviewSummary(reviewed);
                return 0;

            case CommandKind.MetadataSync:
                MetadataSyncResult synchronized = await synchronizer.SynchronizeAsync(
                    command.Input!,
                    command.Destination!,
                    dryRun: false,
                    cancellationToken).ConfigureAwait(false);
                PrintMetadataSync(synchronized, command.Input!);
                return synchronized.ExitCode;

            case CommandKind.MetadataRebind:
                new MetadataRebinder(metadataStore).Rebind(command.Input!, command.Destination!);
                AssetMetadata rebound = metadataStore.Read(command.Destination!);
                Console.WriteLine("NZYTE TV Metadata Rebind");
                Console.WriteLine();
                Console.WriteLine($"Old source: {Path.GetFullPath(command.Input!)}");
                Console.WriteLine($"New source: {Path.GetFullPath(command.Destination!)}");
                Console.WriteLine($"Asset ID:   {rebound.AssetId}");
                Console.WriteLine($"Group ID:   {rebound.ContentGroupId ?? "(not applicable or unresolved)"}");
                Console.WriteLine();
                Console.WriteLine("Programming metadata moved without encoding media.");
                return 0;

            case CommandKind.MetadataEdit:
                AssetMetadata edited = await new MetadataEditor(metadataStore).UpdateTypeAsync(
                    command.Input!,
                    command.MetadataType!,
                    command.MetadataSubtype,
                    cancellationToken).ConfigureAwait(false);
                Console.WriteLine("NZYTE TV Metadata Edit");
                Console.WriteLine();
                Console.WriteLine($"Source:    {Path.GetFullPath(command.Input!)}");
                Console.WriteLine($"Asset ID:  {edited.AssetId}");
                Console.WriteLine($"Type:      {edited.Type}");
                Console.WriteLine($"Subtype:   {edited.Subtype ?? "(none)"}");
                Console.WriteLine($"Group ID:  {edited.ContentGroupId ?? "(not applicable or unresolved)"}");
                Console.WriteLine();
                Console.WriteLine("Programming metadata updated without encoding media. Run metadata sync to copy it to an existing library asset.");
                return 0;

            default:
                return 2;
        }
    }

    private static void PrintMetadataInitialization(MetadataInitializationResult result, string sourceRoot)
    {
        Console.WriteLine("NZYTE TV Metadata Initialization");
        Console.WriteLine();
        if (result.DryRun)
        {
            Console.WriteLine("DRY RUN: no files were written.");
            Console.WriteLine();
        }

        foreach (IGrouping<string, MetadataAssetResult> group in result.Assets
            .Where(asset => !string.IsNullOrWhiteSpace(asset.ContentGroupId)
                && asset.Status is MetadataInitializationStatus.Resolved or MetadataInitializationStatus.Preserved)
            .GroupBy(asset => asset.ContentGroupId!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"GROUP: {group.Key}");
            Console.WriteLine($"Status: {(group.Any(asset => asset.Status == MetadataInitializationStatus.Resolved) ? "AUTO-GROUPED" : "METADATA PRESERVED")}");
            Console.WriteLine();
            foreach (MetadataAssetResult asset in group)
            {
                PrintMetadataAsset(asset, sourceRoot);
            }
        }

        PrintMetadataSection(
            "NON-SONG ASSETS",
            result.Assets.Where(asset => asset.Status is MetadataInitializationStatus.NonSong
                || (asset.Status == MetadataInitializationStatus.Preserved
                    && string.IsNullOrWhiteSpace(asset.ContentGroupId))),
            sourceRoot);
        PrintMetadataSection(
            "REVIEW REQUIRED",
            result.Assets.Where(asset => asset.Status == MetadataInitializationStatus.ReviewRequired),
            sourceRoot);
        PrintMetadataSection(
            "UNRESOLVED",
            result.Assets.Where(asset => asset.Status == MetadataInitializationStatus.Unresolved),
            sourceRoot);
        PrintMetadataSection(
            "ORPHANED / MISSING SOURCE",
            result.Assets.Where(asset => asset.Status == MetadataInitializationStatus.Orphaned),
            sourceRoot);
        PrintMetadataSection(
            "ERRORS",
            result.Assets.Where(asset => asset.Status == MetadataInitializationStatus.Error),
            sourceRoot);

        Console.WriteLine("SUMMARY");
        Console.WriteLine();
        Console.WriteLine($"    Assets scanned:               {result.AssetsScanned}");
        Console.WriteLine($"    Existing metadata preserved:  {result.ExistingMetadataPreserved}");
        Console.WriteLine($"    Metadata created:             {result.MetadataCreated}");
        Console.WriteLine($"    Automatically resolved:       {result.AutomaticallyResolved}");
        Console.WriteLine($"    Review required:              {result.ReviewRequired}");
        Console.WriteLine($"    Unresolved:                   {result.Unresolved}");
        Console.WriteLine($"    Orphaned metadata:            {result.Orphaned}");
        Console.WriteLine($"    Errors:                       {result.Errors}");
    }

    private static void PrintMetadataSection(
        string heading,
        IEnumerable<MetadataAssetResult> assets,
        string sourceRoot)
    {
        MetadataAssetResult[] materialized = assets.ToArray();
        if (materialized.Length == 0)
        {
            return;
        }

        Console.WriteLine(heading);
        Console.WriteLine();
        foreach (MetadataAssetResult asset in materialized)
        {
            PrintMetadataAsset(asset, sourceRoot);
        }
    }

    private static void PrintMetadataAsset(MetadataAssetResult asset, string sourceRoot)
    {
        string displayType = string.IsNullOrWhiteSpace(asset.Subtype)
            ? asset.Type ?? "unknown"
            : $"{asset.Type} / {asset.Subtype}";
        Console.WriteLine($"    [{displayType}]");
        Console.WriteLine($"    {GetDisplayPath(sourceRoot, asset.SourcePath)}");
        if (!string.IsNullOrWhiteSpace(asset.AssetId))
        {
            Console.WriteLine($"    Asset ID: {asset.AssetId}");
        }

        if (!string.IsNullOrWhiteSpace(asset.ContentGroupId))
        {
            Console.WriteLine($"    Content group: {asset.ContentGroupId}");
        }

        Console.WriteLine($"    Reason: {asset.Reason}");
        if (asset.Candidates?.Count > 0)
        {
            Console.WriteLine("    Possible matches:");
            foreach (SongCatalogEntry candidate in asset.Candidates)
            {
                Console.WriteLine($"      - {candidate.Title} — {candidate.Project ?? "project unspecified"} ({candidate.ContentGroupId})");
            }
        }

        if (asset.Eligibility is not null)
        {
            Console.WriteLine($"    Encoding status: {asset.Eligibility.EncodingStatus.ToString().ToUpperInvariant()}");
            Console.WriteLine($"    Metadata status: {asset.Eligibility.MetadataStatus.ToString().ToUpperInvariant()}");
            Console.WriteLine($"    Playlist eligibility: {(asset.Eligibility.IsPlaylistEligible ? "YES" : "NO")}");
        }

        Console.WriteLine();
    }

    private static void PrintMetadataSync(MetadataSyncResult result, string sourceRoot)
    {
        Console.WriteLine("NZYTE TV Metadata Synchronization");
        Console.WriteLine();
        foreach (MetadataSyncFileResult file in result.Files)
        {
            Console.WriteLine($"{GetDisplayPath(sourceRoot, file.SourcePath)}");
            Console.WriteLine($"    Status: {file.Status.ToString().ToUpperInvariant()}");
            Console.WriteLine($"    {file.Detail}");
            Console.WriteLine();
        }

        Console.WriteLine($"Assets scanned: {result.Files.Count}");
        Console.WriteLine($"Errors: {result.Files.Count(file => file.Status == MetadataSyncStatus.Error)}");
    }

    private static void PrintMetadataReviewSummary(MetadataReviewResult result)
    {
        Console.WriteLine();
        Console.WriteLine("NZYTE TV Metadata Review Summary");
        Console.WriteLine();
        Console.WriteLine($"Assets reviewed: {result.Files.Count}");
        Console.WriteLine($"Resolved:        {result.Files.Count(file => file.Resolved)}");
        Console.WriteLine($"Left unresolved: {result.Files.Count(file => !file.Resolved)}");
    }

    private static string GetDisplayPath(string root, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal)
            ? path
            : relative;
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
            Console.WriteLine("NZYTE TV media normalization, verification, and programming metadata");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  nzytetv inspect <input>");
            Console.WriteLine("  nzytetv normalize <input> [--overwrite]");
            Console.WriteLine("  nzytetv normalize-library <source-root> <destination-root> [--overwrite]");
            Console.WriteLine("  nzytetv verify <input>");
            Console.WriteLine("  nzytetv metadata <initialize|review|sync|rebind|edit> ...");
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
            case CommandKind.MetadataHelp:
                Console.WriteLine("NZYTE TV programming metadata and content catalog");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  nzytetv metadata initialize <source-root> <library-root> --catalog <catalog-path> [--dry-run]");
                Console.WriteLine("  nzytetv metadata review <source-root> <library-root> --catalog <catalog-path>");
                Console.WriteLine("  nzytetv metadata sync <source-root> <library-root>");
                Console.WriteLine("  nzytetv metadata rebind <old-source-path> <new-source-path>");
                Console.WriteLine("  nzytetv metadata edit <source-media-path> --type <type> [--subtype <subtype>]");
                Console.WriteLine();
                Console.WriteLine("Metadata commands never encode media or invoke FFmpeg.");
                break;
            case CommandKind.MetadataInitialize:
                Console.WriteLine("Usage: nzytetv metadata initialize <source-root> <library-root> --catalog <catalog-path> [--dry-run]");
                Console.WriteLine("Create or preserve programming sidecars and synchronize existing library assets.");
                Console.WriteLine("--dry-run  Report planned changes without writing any file.");
                break;
            case CommandKind.MetadataReview:
                Console.WriteLine("Usage: nzytetv metadata review <source-root> <library-root> --catalog <catalog-path>");
                Console.WriteLine("Interactively resolve only song assets that need human review.");
                break;
            case CommandKind.MetadataSync:
                Console.WriteLine("Usage: nzytetv metadata sync <source-root> <library-root>");
                Console.WriteLine("Copy source programming sidecars to existing normalized library assets without encoding.");
                break;
            case CommandKind.MetadataRebind:
                Console.WriteLine("Usage: nzytetv metadata rebind <old-source-path> <new-source-path>");
                Console.WriteLine("Move a programming sidecar to an intentionally renamed source while preserving identity.");
                break;
            case CommandKind.MetadataEdit:
                Console.WriteLine("Usage: nzytetv metadata edit <source-media-path> --type <type> [--subtype <subtype>]");
                Console.WriteLine("Override folder-derived programming type without moving or encoding media.");
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

    private sealed class ConsoleMetadataReviewPrompt : IMetadataReviewPrompt
    {
        public Task<string?> SelectContentGroupAsync(
            MetadataReviewRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine();
            Console.WriteLine("Asset:");
            Console.WriteLine($"    {request.SourcePath}");
            Console.WriteLine();
            Console.WriteLine($"Detected title: {request.DetectedTitle}");
            Console.WriteLine($"Reason: {request.Reason}");
            Console.WriteLine();
            Console.WriteLine("Possible matches:");
            Console.WriteLine();
            for (int index = 0; index < request.Candidates.Count; index++)
            {
                SongCatalogEntry candidate = request.Candidates[index];
                Console.WriteLine($"    {index + 1}. {candidate.Title} — {candidate.Project ?? "project unspecified"}");
                Console.WriteLine($"       ID: {candidate.ContentGroupId}");
            }

            int unresolvedChoice = request.Candidates.Count + 1;
            Console.WriteLine($"    {unresolvedChoice}. Leave unresolved");
            Console.WriteLine();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Console.Write("Selection: ");
                string? input = Console.ReadLine();
                if (input is null)
                {
                    return Task.FromResult<string?>(null);
                }

                if (int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out int selection)
                    && selection >= 1
                    && selection <= unresolvedChoice)
                {
                    return Task.FromResult(selection == unresolvedChoice
                        ? null
                        : request.Candidates[selection - 1].ContentGroupId);
                }

                Console.WriteLine($"Enter a number from 1 through {unresolvedChoice}.");
            }
        }
    }
}
