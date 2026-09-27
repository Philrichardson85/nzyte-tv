namespace NzyteTv.Media;

public enum LibraryFileStatus
{
    Normalized,
    SkippedExisting,
    Failed,
}

public sealed record LibraryFileResult(
    string SourcePath,
    string DestinationPath,
    LibraryFileStatus Status,
    TimeSpan Elapsed,
    string? FailureReason = null);

public sealed class LibraryNormalizationResult
{
    public LibraryNormalizationResult(IReadOnlyList<LibraryFileResult> files, TimeSpan elapsed)
    {
        Files = files;
        Elapsed = elapsed;
    }

    public IReadOnlyList<LibraryFileResult> Files { get; }

    public TimeSpan Elapsed { get; }

    public int DiscoveredVideoFiles => Files.Count;

    public int Normalized => Files.Count(file => file.Status == LibraryFileStatus.Normalized);

    public int SkippedExisting => Files.Count(file => file.Status == LibraryFileStatus.SkippedExisting);

    public int Failed => Files.Count(file => file.Status == LibraryFileStatus.Failed);

    public int VerifiedReady => Normalized + SkippedExisting;

    public int ExitCode => Failed == 0 ? 0 : 1;
}

public enum LibraryProgressStage
{
    Starting,
    Normalizing,
    VerifyingExisting,
    RefreshingExisting,
    Complete,
    Skipped,
    Failed,
}

public sealed record LibraryNormalizationProgress(
    int Index,
    int Total,
    string SourcePath,
    string DestinationPath,
    LibraryProgressStage Stage,
    TimeSpan FileElapsed,
    double? Percent = null,
    string? Detail = null);
