using NzyteTv.Core;

namespace NzyteTv.Media;

public enum MetadataInitializationStatus
{
    Resolved,
    NonSong,
    Preserved,
    ReviewRequired,
    Unresolved,
    Error,
    Orphaned,
}

public sealed record MetadataAssetResult(
    string SourcePath,
    string? LibraryPath,
    string? Type,
    string? AssetId,
    string? ContentGroupId,
    MetadataInitializationStatus Status,
    string Reason,
    bool MetadataCreated = false,
    bool ExistingMetadataPreserved = false,
    bool LibraryMetadataSynchronized = false,
    AssetEligibilityResult? Eligibility = null,
    IReadOnlyList<SongCatalogEntry>? Candidates = null,
    string? Subtype = null);

public sealed class MetadataInitializationResult
{
    public MetadataInitializationResult(IReadOnlyList<MetadataAssetResult> assets, bool dryRun)
    {
        Assets = assets;
        DryRun = dryRun;
    }

    public IReadOnlyList<MetadataAssetResult> Assets { get; }

    public bool DryRun { get; }

    public int AssetsScanned => Assets.Count(asset => asset.Status != MetadataInitializationStatus.Orphaned);

    public int ExistingMetadataPreserved => Assets.Count(asset => asset.ExistingMetadataPreserved);

    public int MetadataCreated => Assets.Count(asset => asset.MetadataCreated);

    public int AutomaticallyResolved => Assets.Count(asset => asset.Status == MetadataInitializationStatus.Resolved);

    public int ReviewRequired => Assets.Count(asset => asset.Status == MetadataInitializationStatus.ReviewRequired);

    public int Unresolved => Assets.Count(asset => asset.Status == MetadataInitializationStatus.Unresolved);

    public int Errors => Assets.Count(asset => asset.Status == MetadataInitializationStatus.Error);

    public int Orphaned => Assets.Count(asset => asset.Status == MetadataInitializationStatus.Orphaned);

    public int ExitCode => Errors == 0 ? 0 : 1;
}

public enum MetadataSyncStatus
{
    Synchronized,
    Unchanged,
    MissingSourceMetadata,
    MissingLibraryAsset,
    Error,
}

public sealed record MetadataSyncFileResult(
    string SourcePath,
    string LibraryPath,
    MetadataSyncStatus Status,
    string Detail);

public sealed class MetadataSyncResult(IReadOnlyList<MetadataSyncFileResult> files)
{
    public IReadOnlyList<MetadataSyncFileResult> Files { get; } = files;

    public int ExitCode => Files.Any(file => file.Status == MetadataSyncStatus.Error) ? 1 : 0;
}

public sealed record MetadataReviewRequest(
    string SourcePath,
    AssetMetadata Metadata,
    string DetectedTitle,
    IReadOnlyList<SongCatalogEntry> Candidates,
    string Reason);

public interface IMetadataReviewPrompt
{
    Task<string?> SelectContentGroupAsync(MetadataReviewRequest request, CancellationToken cancellationToken);
}

public sealed record MetadataReviewFileResult(
    string SourcePath,
    string AssetId,
    string? ContentGroupId,
    bool Resolved,
    bool LibraryMetadataSynchronized,
    string Detail);

public sealed class MetadataReviewResult(IReadOnlyList<MetadataReviewFileResult> files)
{
    public IReadOnlyList<MetadataReviewFileResult> Files { get; } = files;
}
