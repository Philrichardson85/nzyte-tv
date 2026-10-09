using System.Text.Json;
using System.Text.Json.Serialization;

namespace NzyteTv.Media;

[JsonConverter(typeof(JsonStringEnumConverter<MediaLibraryRefreshStatus>))]
public enum MediaLibraryRefreshStatus
{
    Running,
    Succeeded,
    SucceededWithWarnings,
    NoChanges,
    Failed,
    Interrupted,
}

[JsonConverter(typeof(JsonStringEnumConverter<MediaLibraryRefreshIssueCode>))]
public enum MediaLibraryRefreshIssueCode
{
    InvalidReadyFileName,
    InvalidReadyManifest,
    DuplicatePackageId,
    PackageIdConflict,
    ExistingMediaConflict,
    MissingPackageMember,
    PackageMemberMismatch,
    PackageMemberChanged,
    InvalidTechnicalManifest,
    InvalidProgrammingMetadata,
    ConflictingProgrammingMetadata,
    CatalogMatchRequired,
    AmbiguousCatalogMatch,
    DuplicateAssetId,
    CatalogChanged,
    RefreshFailed,
    OperationFinalizationInterrupted,
    BootstrapInconsistency,
}

public sealed record MediaLibraryRefreshIssue(
    MediaLibraryRefreshIssueCode Code,
    string? PackageId = null,
    string? RelativeIdentity = null);

public sealed record MediaLibraryRefreshResult
{
    public const int CurrentSchemaVersion = 1;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? OperationId { get; init; }

    [JsonRequired]
    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    [JsonRequired]
    public MediaLibraryRefreshStatus Status { get; init; }

    [JsonRequired]
    public long MetadataRevisionBefore { get; init; }

    [JsonRequired]
    public long MetadataRevisionAfter { get; init; }

    public string? GenerationIdBefore { get; init; }

    public string? GenerationIdAfter { get; init; }

    public int PackagesObserved { get; init; }

    public int PackagesAccepted { get; init; }

    public int PackagesAlreadyProcessed { get; init; }

    public int PackagesRejected { get; init; }

    public int NewSourceAssets { get; init; }

    public int NewLibraryAssets { get; init; }

    public int MetadataRecordsCreated { get; init; }

    public int MetadataRecordsPreserved { get; init; }

    public int AssetsNewlyEligible { get; init; }

    public int UnresolvedAssets { get; init; }

    public int IncompletePackages { get; init; }

    public int SkippedPackages { get; init; }

    public int WarningCount { get; init; }

    public int ErrorCount { get; init; }

    public IReadOnlyList<MediaLibraryRefreshIssue>? Issues { get; init; } = [];
}

public static class MediaLibraryRefreshResultSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static string Serialize(MediaLibraryRefreshResult result)
    {
        Validate(result);
        return JsonSerializer.Serialize(result, JsonOptions) + Environment.NewLine;
    }

    public static MediaLibraryRefreshResult Deserialize(ReadOnlySpan<byte> content)
    {
        MediaLibraryRefreshResult result = StrictJson.Deserialize<MediaLibraryRefreshResult>(
            content,
            JsonOptions,
            "media refresh operation result");
        Validate(result);
        return result;
    }

    private static void Validate(MediaLibraryRefreshResult? result)
    {
        if (result is null
            || result.SchemaVersion != MediaLibraryRefreshResult.CurrentSchemaVersion
            || !ReadyMediaPackageValidator.IsPackageId(result.OperationId)
            || result.StartedAt == default
            || !Enum.IsDefined(result.Status)
            || result.MetadataRevisionBefore < 0
            || result.MetadataRevisionAfter < 0
            || result.Issues is null
            || result.Issues.Any(issue => !Enum.IsDefined(issue.Code)
                || (issue.PackageId is not null && !ReadyMediaPackageValidator.IsPackageId(issue.PackageId))
                || !IsSafeRelative(issue.RelativeIdentity)))
        {
            throw new InvalidDataException("The media refresh operation result is invalid.");
        }

        if (result.Status == MediaLibraryRefreshStatus.Running && result.CompletedAt is not null
            || result.Status != MediaLibraryRefreshStatus.Running && result.CompletedAt is null)
        {
            throw new InvalidDataException("The media refresh operation completion state is inconsistent.");
        }
    }

    private static bool IsSafeRelative(string? value)
    {
        if (value is null) return true;
        try
        {
            return string.Equals(
                MetadataPathSafety.NormalizeRelativeMediaPath(value),
                value,
                StringComparison.Ordinal);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}

public interface IMediaRefreshOperationStore
{
    Task WriteAsync(MediaLibraryRefreshResult result, CancellationToken cancellationToken);

    IReadOnlyList<MediaLibraryRefreshResult> LoadAll();

    Task<IReadOnlyList<MediaLibraryRefreshResult>> ReconcileRunningAsync(
        AssetMetadataGenerationPointer? current,
        CancellationToken cancellationToken);
}

public sealed class MediaRefreshOperationStore : IMediaRefreshOperationStore
{
    public const string OperationsDirectoryName = "operations";
    private const int MaximumOperationBytes = 256 * 1024;
    private readonly string _root;
    private readonly IAtomicTextFileWriter _writer;
    private readonly TimeProvider _timeProvider;

    public MediaRefreshOperationStore(
        string externalMetadataRoot,
        IAtomicTextFileWriter? writer = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalMetadataRoot);
        if (!Path.IsPathFullyQualified(externalMetadataRoot))
        {
            throw new ArgumentException("The external metadata root must be absolute.", nameof(externalMetadataRoot));
        }

        _root = Path.GetFullPath(externalMetadataRoot);
        _writer = writer ?? new AtomicTextFileWriter();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task WriteAsync(MediaLibraryRefreshResult result, CancellationToken cancellationToken)
    {
        string directory = GetOperationsDirectory(create: true);
        string path = Path.Combine(directory, result.OperationId + ".json");
        return _writer.WriteAsync(path, MediaLibraryRefreshResultSerializer.Serialize(result), cancellationToken);
    }

    public IReadOnlyList<MediaLibraryRefreshResult> LoadAll()
    {
        string directory = GetOperationsDirectory(create: false);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(Read)
            .ToArray();
    }

    public async Task<IReadOnlyList<MediaLibraryRefreshResult>> ReconcileRunningAsync(
        AssetMetadataGenerationPointer? current,
        CancellationToken cancellationToken)
    {
        var reconciled = new List<MediaLibraryRefreshResult>();
        foreach (MediaLibraryRefreshResult operation in LoadAll()
            .Where(value => value.Status == MediaLibraryRefreshStatus.Running))
        {
            bool published = operation.GenerationIdAfter is not null
                && current is not null
                && operation.MetadataRevisionAfter == current.Revision
                && string.Equals(operation.GenerationIdAfter, current.GenerationId, StringComparison.Ordinal);
            MediaLibraryRefreshIssue[] issues = published
                ? [.. operation.Issues!, new MediaLibraryRefreshIssue(
                    MediaLibraryRefreshIssueCode.OperationFinalizationInterrupted)]
                : [.. operation.Issues!];
            MediaLibraryRefreshResult updated = operation with
            {
                CompletedAt = _timeProvider.GetUtcNow(),
                Status = published
                    ? MediaLibraryRefreshStatus.SucceededWithWarnings
                    : MediaLibraryRefreshStatus.Interrupted,
                WarningCount = published ? operation.WarningCount + 1 : operation.WarningCount,
                ErrorCount = published ? operation.ErrorCount : Math.Max(1, operation.ErrorCount),
                Issues = issues,
            };
            await WriteAsync(updated, cancellationToken).ConfigureAwait(false);
            reconciled.Add(updated);
        }

        return reconciled;
    }

    private MediaLibraryRefreshResult Read(string path)
    {
        MetadataPathSafety.EnsureNoReparsePoint(GetOperationsDirectory(create: false), path);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaximumOperationBytes)
        {
            throw new InvalidDataException("A media refresh operation record has an invalid size.");
        }

        return MediaLibraryRefreshResultSerializer.Deserialize(File.ReadAllBytes(path));
    }

    private string GetOperationsDirectory(bool create)
    {
        if (create)
        {
            Directory.CreateDirectory(_root);
            MetadataPathSafety.EnsureNoReparsePoint(_root, _root);
        }

        string directory = Path.Combine(_root, OperationsDirectoryName);
        if (create) Directory.CreateDirectory(directory);
        if (Directory.Exists(directory)) MetadataPathSafety.EnsureNoReparsePoint(_root, directory);
        return directory;
    }
}
