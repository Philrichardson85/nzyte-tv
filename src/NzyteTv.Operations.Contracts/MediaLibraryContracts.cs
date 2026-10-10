using System.Text.Json.Serialization;

namespace NzyteTv.Operations.Contracts;

public enum MediaLibraryFeatureState
{
    Disabled,
    Ready,
    Busy,
    Unavailable,
}

public enum MediaLibraryOperationState
{
    Starting,
    Running,
    Succeeded,
    SucceededWithWarnings,
    NoChanges,
    Failed,
    Interrupted,
}

public sealed record MediaLibraryIssueResponse(
    string Code,
    string? PackageId = null,
    string? RelativeIdentity = null);

public sealed record MediaLibraryOperationResponse(
    int SchemaVersion,
    string OperationId,
    MediaLibraryOperationState Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long MetadataRevisionBefore,
    long MetadataRevisionAfter,
    string? GenerationIdBefore,
    string? GenerationIdAfter,
    int PackagesObserved,
    int PackagesAccepted,
    int PackagesAlreadyProcessed,
    int PackagesRejected,
    int NewSourceAssets,
    int NewLibraryAssets,
    int MetadataRecordsCreated,
    int MetadataRecordsPreserved,
    int AssetsNewlyEligible,
    int UnresolvedAssets,
    int IncompletePackages,
    int SkippedPackages,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<MediaLibraryIssueResponse> Issues);

public sealed record MediaLibrarySummaryResponse(
    int SchemaVersion,
    MediaLibraryFeatureState FeatureState,
    long? MetadataRevision,
    string? GenerationId,
    string? ActiveOperationId,
    MediaLibraryOperationResponse? LastOperation);

public sealed record MediaLibraryRefreshRequest
{
    [JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonRequired]
    public long ExpectedMetadataRevision { get; init; }
}

public sealed record MediaLibraryRefreshAcceptedResponse(
    int SchemaVersion,
    string OperationId);
