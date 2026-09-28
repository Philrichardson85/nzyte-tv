namespace NzyteTv.Core;

public sealed record PlaylistAsset(
    string AssetId,
    string? ContentGroupId,
    string Title,
    string? Artist,
    string Type,
    string? Subtype,
    string RelativePath,
    double DurationSeconds,
    DateOnly? RotationStartDate);

public sealed record PlaylistExclusion(string RelativePath, IReadOnlyList<string> Reasons);

public sealed record PlaylistItem(
    int Sequence,
    string AssetId,
    string? ContentGroupId,
    string Title,
    string Type,
    string? Subtype,
    string RelativePath,
    double DurationSeconds,
    double StartOffsetSeconds);

public sealed class PlaylistSummary
{
    public int EligibleAssets { get; init; }

    public int ExcludedAssets { get; init; }

    public int CategoryTargetRelaxations { get; init; }

    public int ExactAssetCooldownRelaxations { get; init; }

    public int NewReleasePreferenceBypasses { get; init; }

    public int ContentGroupCooldownRelaxations { get; init; }

    public int MusicFirstRescueRelaxations { get; init; }

    public int EmergencyContentGroupFloorViolations { get; init; }

    public int ShortToShortPreferredRelaxations { get; init; }

    public int FullToShortPreferredRelaxations { get; init; }

    public int ShortToFullPreferredRelaxations { get; init; }

    public int EmergencyShortToShortFloorViolations { get; init; }

    public int EmergencyFullToShortFloorViolations { get; init; }

    public int EmergencyShortToFullFloorViolations { get; init; }

    public int MusicFirstCategorySubstitutions { get; init; }

    public int FullPresentationPrioritySubstitutions { get; init; }

    public int VlogAboveTargetFallbacks { get; init; }

    public int CooldownAgePreferenceSubstitutions { get; init; }

    public int ProjectedVlogOvershootSubstitutions { get; init; }

    public int LongMusicAirtimeEfficiencySubstitutions { get; init; }

    public int ConsecutiveVlogViolations { get; init; }

    public int EmergencyVlogRunViolations { get; init; }

    public int BumperCadenceMisses { get; init; }

    public int PromoCadenceMisses { get; init; }

    public int InterstitialCadenceMisses { get; init; }

    public int BumperInsertions { get; init; }

    public int PromoInsertions { get; init; }

    public int InterstitialInsertions { get; init; }

    public IReadOnlyDictionary<string, double> ConfiguredAirtimeTargetPercentages { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, double> EffectiveAirtimeTargetPercentages { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, double> PracticalCategoryCapacitySeconds { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    public IReadOnlyList<string> CapacityLimitedCategories { get; init; } = [];

    public double RedistributedTargetAirtimeSeconds { get; init; }

    public IReadOnlyDictionary<string, double> AirtimePercentages { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);
}

public sealed class PlaylistDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTimeOffset GeneratedAtUtc { get; init; }

    public DateTimeOffset ScheduleStartUtc { get; init; }

    public int Seed { get; init; }

    public double TargetDurationSeconds { get; init; }

    public double ActualDurationSeconds { get; init; }

    public double OverrunSeconds { get; init; }

    public IReadOnlyList<PlaylistItem> Items { get; init; } = [];

    public IReadOnlyList<PlaylistExclusion> ExcludedAssets { get; init; } = [];

    public PlaylistSummary Summary { get; init; } = new();
}

public sealed record PlaylistHistoryEntry(
    string AssetId,
    string? ContentGroupId,
    string Type,
    DateTimeOffset PlayedAtUtc,
    double? DurationSeconds = null);

public sealed class PlaylistHistoryDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTimeOffset? ScheduleEndUtc { get; init; }

    public IReadOnlyList<PlaylistHistoryEntry> Plays { get; init; } = [];

    public static PlaylistHistoryDocument Empty { get; } = new();
}

public sealed record PlaylistGenerationResult(
    PlaylistDocument Playlist,
    PlaylistHistoryDocument UpdatedHistory);
