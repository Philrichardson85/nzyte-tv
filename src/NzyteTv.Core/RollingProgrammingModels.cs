namespace NzyteTv.Core;

public static class RollingProgrammingPolicy
{
    public const int ManifestSchemaVersion = 1;

    public const int ArtifactSchemaVersion = 1;

    public const int IdentityFormatVersion = 1;

    public const string PlannerAlgorithmVersion = "nzyte-tv-rolling-3b1-v1";

    public const int DefaultTargetPreparedBlockCount = 3;

    public const double DefaultTargetBlockDurationSeconds = 6 * 60 * 60;

    public const string LegacyProgrammingSnapshotMarker = "legacy-no-programming-configuration";
}

public sealed record RollingArtifactReference(
    string RelativePath,
    string Sha256);

public sealed class RollingProgrammingManifest
{
    public const int CurrentSchemaVersion = RollingProgrammingPolicy.ManifestSchemaVersion;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? PlannerId { get; init; }

    public int BaseSeed { get; init; }

    public double TargetBlockDurationSeconds { get; init; } =
        RollingProgrammingPolicy.DefaultTargetBlockDurationSeconds;

    public int TargetPreparedBlockCount { get; init; } =
        RollingProgrammingPolicy.DefaultTargetPreparedBlockCount;

    public long NextSequence { get; init; } = 1;

    public RollingArtifactReference? GenesisHistory { get; init; }

    public RollingArtifactReference? HistoryHead { get; init; }

    public IReadOnlyList<RollingCommittedBlock>? Blocks { get; init; } = [];

    public DateTimeOffset InitializedAtUtc { get; init; }
}

public sealed record RollingCommittedBlock
{
    public long Sequence { get; init; }

    public string? BlockId { get; init; }

    public string? ParentBlockId { get; init; }

    public int Seed { get; init; }

    public string? PlaylistPath { get; init; }

    public string? PlaylistSha256 { get; init; }

    public string? DescriptorPath { get; init; }

    public string? DescriptorSha256 { get; init; }

    public string? InputSnapshotPath { get; init; }

    public string? InputSnapshotSha256 { get; init; }

    public double TargetDurationSeconds { get; init; }

    public double ActualDurationSeconds { get; init; }

    public DateTimeOffset ScheduleStartUtc { get; init; }

    public DateTimeOffset ScheduleEndUtc { get; init; }

    public int ItemCount { get; init; }

    public RollingArtifactReference? HistoryBefore { get; init; }

    public RollingArtifactReference? HistoryAfter { get; init; }

    public string? CatalogSnapshotHash { get; init; }

    public string? ProgrammingSnapshotHash { get; init; }

    public string? InventorySnapshotHash { get; init; }

    public int? ProgrammingSchemaVersion { get; init; }

    public int? ProgrammingRevision { get; init; }

    public string? PlannerAlgorithmVersion { get; init; }

    public DateTimeOffset GeneratedAtUtc { get; init; }
}

public sealed class RollingBlockDescriptor
{
    public int SchemaVersion { get; init; } = RollingProgrammingPolicy.ArtifactSchemaVersion;

    public RollingCommittedBlock? Block { get; init; }
}

public sealed record RollingAssetReadinessSnapshot(
    string AssetId,
    string RelativePath,
    long MediaLength,
    DateTimeOffset MediaLastWriteUtc,
    string TechnicalManifestSha256,
    string ProgrammingMetadataSha256);

public sealed class RollingPlanningInputSnapshot
{
    public int SchemaVersion { get; init; } = RollingProgrammingPolicy.ArtifactSchemaVersion;

    public long Sequence { get; init; }

    public int Seed { get; init; }

    public double TargetDurationSeconds { get; init; }

    public DateTimeOffset GeneratedAtUtc { get; init; }

    public DateTimeOffset PlannedScheduleStartUtc { get; init; }

    public string? PlannerAlgorithmVersion { get; init; }

    public string? CatalogJson { get; init; }

    public string? CatalogSnapshotHash { get; init; }

    public string? ProgrammingJson { get; init; }

    public string? ProgrammingSnapshotHash { get; init; }

    public ProgrammingConfiguration? ProgrammingConfiguration { get; init; }

    public string? InventorySnapshotHash { get; init; }

    public PlaylistHistoryDocument? HistoryBefore { get; init; }

    public string? HistoryBeforeHash { get; init; }

    public IReadOnlyList<PlaylistAsset>? EligibleAssets { get; init; } = [];

    public IReadOnlyList<PlaylistExclusion>? ExcludedAssets { get; init; } = [];

    public IReadOnlyList<RollingAssetReadinessSnapshot>? AssetReadiness { get; init; } = [];
}

public sealed class RollingGenerationIntent
{
    public int SchemaVersion { get; init; } = RollingProgrammingPolicy.ArtifactSchemaVersion;

    public string? IntentId { get; init; }

    public long Sequence { get; init; }

    public int Seed { get; init; }

    public string? ParentBlockId { get; init; }

    public RollingArtifactReference? HistoryBefore { get; init; }

    public string? InputSnapshotPath { get; init; }

    public string? InputSnapshotSha256 { get; init; }

    public string? PlannerAlgorithmVersion { get; init; }

    public DateTimeOffset GeneratedAtUtc { get; init; }

    public DateTimeOffset PlannedScheduleStartUtc { get; init; }

    public double TargetDurationSeconds { get; init; }
}

public sealed record RollingBlockIdentityInput(
    string PlannerId,
    long Sequence,
    string? ParentBlockId,
    int Seed,
    double TargetDurationSeconds,
    PlaylistDocument Playlist,
    string HistoryBeforeHash,
    string HistoryAfterHash,
    string CatalogSnapshotHash,
    string ProgrammingSnapshotHash,
    string InventorySnapshotHash,
    string PlannerAlgorithmVersion);
