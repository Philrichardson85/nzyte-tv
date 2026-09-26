using System.Diagnostics;
using NzyteTv.Core;

namespace NzyteTv.Media;

public interface IMediaLibraryNormalizer
{
    Task<LibraryNormalizationResult> NormalizeAsync(
        string sourceRoot,
        string destinationRoot,
        bool overwrite,
        IProgress<LibraryNormalizationProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class MediaLibraryNormalizer : IMediaLibraryNormalizer
{
    private readonly IMediaLibraryDiscovery _discovery;
    private readonly IMediaNormalizer _normalizer;
    private readonly IMediaVerifier _verifier;

    public MediaLibraryNormalizer(
        IMediaLibraryDiscovery discovery,
        IMediaNormalizer normalizer,
        IMediaVerifier verifier)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    public async Task<LibraryNormalizationResult> NormalizeAsync(
        string sourceRoot,
        string destinationRoot,
        bool overwrite,
        IProgress<LibraryNormalizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LibraryPathPolicy.EnsureRootsDoNotOverlap(sourceRoot, destinationRoot);
        IReadOnlyList<LibraryMediaFile> files = _discovery.Discover(sourceRoot, destinationRoot);
        Directory.CreateDirectory(Path.GetFullPath(destinationRoot));
        var results = new List<LibraryFileResult>(files.Count);
        var totalStopwatch = Stopwatch.StartNew();
        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        IReadOnlyDictionary<string, int> destinationCounts = files
            .GroupBy(file => file.DestinationPath, pathComparer)
            .ToDictionary(group => group.Key, group => group.Count(), pathComparer);

        for (int index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LibraryMediaFile file = files[index];
            var fileStopwatch = Stopwatch.StartNew();
            Report(progress, index, files.Count, file, LibraryProgressStage.Starting, fileStopwatch.Elapsed);

            if (destinationCounts[file.DestinationPath] > 1)
            {
                string reason = "Multiple source files map to the same MP4 destination path.";
                results.Add(Failed(file, fileStopwatch.Elapsed, reason));
                Report(progress, index, files.Count, file, LibraryProgressStage.Failed, fileStopwatch.Elapsed, detail: reason);
                continue;
            }

            try
            {
                if (File.Exists(file.DestinationPath) && !overwrite)
                {
                    Report(progress, index, files.Count, file, LibraryProgressStage.VerifyingExisting, fileStopwatch.Elapsed);
                    MediaVerification existing = await _verifier.VerifyAsync(
                        file.DestinationPath,
                        cancellationToken).ConfigureAwait(false);

                    if (!existing.Result.IsBroadcastReady)
                    {
                        string reason = "Existing destination failed verification: " + FormatFailedChecks(existing.Result);
                        results.Add(Failed(file, fileStopwatch.Elapsed, reason));
                        Report(progress, index, files.Count, file, LibraryProgressStage.Failed, fileStopwatch.Elapsed, detail: reason);
                        continue;
                    }

                    results.Add(new LibraryFileResult(
                        file.SourcePath,
                        file.DestinationPath,
                        LibraryFileStatus.SkippedExisting,
                        fileStopwatch.Elapsed));
                    Report(progress, index, files.Count, file, LibraryProgressStage.Skipped, fileStopwatch.Elapsed,
                        detail: "Existing destination verification passed.");
                    continue;
                }

                MediaPathPolicy.EnsureOutputIsAllowed(file.SourcePath, file.DestinationPath, overwrite);

                var fileProgress = new InlineProgress<NormalizationProgress>(value =>
                {
                    if (value.Stage is "Encoding" or "Verifying")
                    {
                        Report(
                            progress,
                            index,
                            files.Count,
                            file,
                            LibraryProgressStage.Normalizing,
                            fileStopwatch.Elapsed,
                            value.Percent,
                            value.Stage);
                    }
                });

                await _normalizer.NormalizeAsync(
                    file.SourcePath,
                    file.DestinationPath,
                    overwrite,
                    fileProgress,
                    cancellationToken).ConfigureAwait(false);

                results.Add(new LibraryFileResult(
                    file.SourcePath,
                    file.DestinationPath,
                    LibraryFileStatus.Normalized,
                    fileStopwatch.Elapsed));
                Report(progress, index, files.Count, file, LibraryProgressStage.Complete, fileStopwatch.Elapsed,
                    detail: "Verification passed.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverableFileFailure(exception))
            {
                string reason = FormatException(exception);
                results.Add(Failed(file, fileStopwatch.Elapsed, reason));
                Report(progress, index, files.Count, file, LibraryProgressStage.Failed, fileStopwatch.Elapsed, detail: reason);
            }
        }

        totalStopwatch.Stop();
        return new LibraryNormalizationResult(results, totalStopwatch.Elapsed);
    }

    private static LibraryFileResult Failed(LibraryMediaFile file, TimeSpan elapsed, string reason) => new(
        file.SourcePath,
        file.DestinationPath,
        LibraryFileStatus.Failed,
        elapsed,
        reason);

    private static bool IsRecoverableFileFailure(Exception exception) => exception is
        FileNotFoundException or
        IOException or
        UnauthorizedAccessException or
        InvalidOperationException or
        MediaNormalizationException or
        MediaProbeException or
        FfprobeDataException or
        ProcessExecutionException;

    private static string FormatException(Exception exception)
    {
        if (exception is NormalizationVerificationException verificationException)
        {
            return $"{exception.Message} {FormatFailedChecks(verificationException.Verification.Result)}";
        }

        return exception.Message;
    }

    private static string FormatFailedChecks(VerificationResult result) => string.Join(
        "; ",
        result.Checks
            .Where(check => !check.Passed)
            .Select(check => string.IsNullOrWhiteSpace(check.Detail)
                ? check.Label
                : $"{check.Label} ({check.Detail})"));

    private static void Report(
        IProgress<LibraryNormalizationProgress>? progress,
        int zeroBasedIndex,
        int total,
        LibraryMediaFile file,
        LibraryProgressStage stage,
        TimeSpan elapsed,
        double? percent = null,
        string? detail = null) => progress?.Report(new LibraryNormalizationProgress(
            zeroBasedIndex + 1,
            total,
            file.SourcePath,
            file.DestinationPath,
            stage,
            elapsed,
            percent,
            detail));

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
