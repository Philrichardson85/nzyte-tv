using System.Text.Json.Serialization;

namespace NzyteTv.Core;

public static class ProgrammingPersonalityNames
{
    public const string MusicHeavy = "music-heavy";
    public const string Mixed = "mixed";
    public const string FastPaced = "fast-paced";

    public static IReadOnlySet<string> Supported { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        MusicHeavy,
        Mixed,
        FastPaced,
    };
}

public static class ProgrammingLaneNames
{
    public const string FullMusic = "full-music";
    public const string ShortPerformance = "short-performance";
    public const string Personality = "personality";
    public const string PersonalityOrFullMusic = "personality-or-full-music";

    public static IReadOnlySet<string> Supported { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        FullMusic,
        ShortPerformance,
        Personality,
        PersonalityOrFullMusic,
    };
}

public sealed record ProgrammingRepetitionPolicy
{
    [JsonRequired]
    public int ExactAssetCooldownMinutes { get; init; } = 120;

    [JsonRequired]
    public int SameContentGroupLookback { get; init; } = 2;

    [JsonRequired]
    public int MaximumConsecutiveShortPieces { get; init; } = 3;
}

public sealed record StationImagingPolicy
{
    [JsonRequired]
    public int MinimumSubstantialPieces { get; init; } = 3;

    [JsonRequired]
    public int MaximumSubstantialPieces { get; init; } = 5;
}

public sealed record ProgrammingPromoCadence
{
    [JsonRequired]
    public int MinimumIntervalMinutes { get; init; } = 30;

    [JsonRequired]
    public int MaximumIntervalMinutes { get; init; } = 45;
}

public sealed record ProgrammingPersonality
{
    public string? Name { get; init; }

    public IReadOnlyList<string>? Lanes { get; init; } = [];
}

public sealed record ActiveCampaign
{
    public bool Enabled { get; init; }

    public string? ContentGroupId { get; init; }

    public double WeightMultiplier { get; init; } = ProgrammingConfiguration.DefaultCampaignMultiplier;
}

public sealed record AssetEditorialOverride
{
    public bool DoNotAir { get; init; }

    public double WeightMultiplier { get; init; } = 1.0;
}

public sealed record ProgrammingConfiguration
{
    public const int CurrentSchemaVersion = 1;
    public const double DefaultCampaignMultiplier = 2.0;
    public const double MinimumWeightMultiplier = 0.1;
    public const double MaximumWeightMultiplier = 10.0;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonRequired]
    public int Revision { get; init; } = 1;

    [JsonRequired]
    public ProgrammingRepetitionPolicy? Repetition { get; init; } = new();

    [JsonRequired]
    public StationImagingPolicy? StationImaging { get; init; } = new();

    [JsonRequired]
    public ProgrammingPromoCadence? PromoCadence { get; init; } = new();

    [JsonRequired]
    public bool ReleaseAgeHotRotationEnabled { get; init; }

    [JsonRequired]
    public IReadOnlyList<ProgrammingPersonality>? Personalities { get; init; } = CreateDefaultPersonalities();

    [JsonRequired]
    public ActiveCampaign? ActiveCampaign { get; init; } = new();

    [JsonRequired]
    public IReadOnlyDictionary<string, AssetEditorialOverride>? AssetOverrides { get; init; } =
        new Dictionary<string, AssetEditorialOverride>(StringComparer.Ordinal);

    public static ProgrammingConfiguration CreateDefault() => new();

    private static IReadOnlyList<ProgrammingPersonality> CreateDefaultPersonalities() =>
    [
        new ProgrammingPersonality
        {
            Name = ProgrammingPersonalityNames.MusicHeavy,
            Lanes =
            [
                ProgrammingLaneNames.FullMusic,
                ProgrammingLaneNames.FullMusic,
                ProgrammingLaneNames.ShortPerformance,
                ProgrammingLaneNames.FullMusic,
                ProgrammingLaneNames.Personality,
                ProgrammingLaneNames.FullMusic,
            ],
        },
        new ProgrammingPersonality
        {
            Name = ProgrammingPersonalityNames.Mixed,
            Lanes =
            [
                ProgrammingLaneNames.FullMusic,
                ProgrammingLaneNames.ShortPerformance,
                ProgrammingLaneNames.FullMusic,
                ProgrammingLaneNames.Personality,
                ProgrammingLaneNames.ShortPerformance,
                ProgrammingLaneNames.FullMusic,
            ],
        },
        new ProgrammingPersonality
        {
            Name = ProgrammingPersonalityNames.FastPaced,
            Lanes =
            [
                ProgrammingLaneNames.ShortPerformance,
                ProgrammingLaneNames.FullMusic,
                ProgrammingLaneNames.ShortPerformance,
                ProgrammingLaneNames.FullMusic,
                ProgrammingLaneNames.ShortPerformance,
                ProgrammingLaneNames.PersonalityOrFullMusic,
            ],
        },
    ];
}

public sealed record ProgrammingAssetInventoryEntry(
    string AssetId,
    string? ContentGroupId,
    string Type,
    bool IsTechnicallyPlaylistEligible);

public sealed class ProgrammingConfigurationValidationException : InvalidOperationException
{
    public ProgrammingConfigurationValidationException(IEnumerable<string> errors)
        : base($"Programming configuration validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}")
    {
    }
}

public static class ProgrammingConfigurationValidator
{
    public static IReadOnlyList<string> GetStructuralErrors(ProgrammingConfiguration? configuration)
    {
        if (configuration is null)
        {
            return ["The programming configuration is empty."];
        }

        var errors = new List<string>();
        if (configuration.SchemaVersion != ProgrammingConfiguration.CurrentSchemaVersion)
        {
            errors.Add(
                $"Unsupported schemaVersion {configuration.SchemaVersion}; expected {ProgrammingConfiguration.CurrentSchemaVersion}.");
        }

        if (configuration.Revision < 1)
        {
            errors.Add("revision must be at least 1.");
        }

        ValidateRepetition(configuration.Repetition, errors);
        ValidateStationImaging(configuration.StationImaging, errors);
        ValidatePromoCadence(configuration.PromoCadence, errors);
        ValidatePersonalities(configuration.Personalities, errors);
        ValidateCampaign(configuration.ActiveCampaign, errors);
        ValidateAssetOverrides(configuration.AssetOverrides, errors);
        return errors;
    }

    public static IReadOnlyList<string> GetReferenceErrors(
        ProgrammingConfiguration configuration,
        SongCatalog catalog,
        IReadOnlyCollection<ProgrammingAssetInventoryEntry> inventory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(inventory);

        var errors = new List<string>();
        ActiveCampaign? campaign = configuration.ActiveCampaign;
        if (campaign?.Enabled == true
            && catalog.FindByContentGroupId(campaign.ContentGroupId) is null)
        {
            errors.Add($"Active campaign contentGroupId '{campaign.ContentGroupId}' does not exist in the song catalog.");
        }

        var knownAssetIds = inventory
            .Select(asset => asset.AssetId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string assetId in configuration.AssetOverrides?.Keys ?? [])
        {
            if (!knownAssetIds.Contains(assetId))
            {
                errors.Add($"Asset override references unknown assetId '{assetId}'.");
            }
        }

        ProgrammingAssetInventoryEntry[] eligibleSubstantial = inventory
            .Where(asset => asset.IsTechnicallyPlaylistEligible
                && ProgrammingContentClassifier.IsSubstantial(asset.Type))
            .ToArray();
        if (eligibleSubstantial.Length > 0
            && eligibleSubstantial.All(asset => configuration.GetAssetOverride(asset.AssetId).DoNotAir))
        {
            errors.Add("Editorial overrides exclude every technically eligible substantial programming asset.");
        }

        return errors;
    }

    public static void ValidateStructure(ProgrammingConfiguration? configuration)
    {
        IReadOnlyList<string> errors = GetStructuralErrors(configuration);
        if (errors.Count > 0)
        {
            throw new ProgrammingConfigurationValidationException(errors);
        }
    }

    public static void Validate(
        ProgrammingConfiguration? configuration,
        SongCatalog catalog,
        IReadOnlyCollection<ProgrammingAssetInventoryEntry> inventory)
    {
        IReadOnlyList<string> errors = GetStructuralErrors(configuration);
        if (configuration is not null && errors.Count == 0)
        {
            errors = [.. errors, .. GetReferenceErrors(configuration, catalog, inventory)];
        }

        if (errors.Count > 0)
        {
            throw new ProgrammingConfigurationValidationException(errors);
        }
    }

    private static void ValidateRepetition(
        ProgrammingRepetitionPolicy? repetition,
        ICollection<string> errors)
    {
        if (repetition is null)
        {
            errors.Add("repetition is required.");
            return;
        }

        if (repetition.ExactAssetCooldownMinutes is < 1 or > 1440)
        {
            errors.Add("repetition.exactAssetCooldownMinutes must be between 1 and 1440.");
        }

        if (repetition.SameContentGroupLookback is < 1 or > 20)
        {
            errors.Add("repetition.sameContentGroupLookback must be between 1 and 20 substantial pieces.");
        }

        if (repetition.MaximumConsecutiveShortPieces is < 1 or > 10)
        {
            errors.Add("repetition.maximumConsecutiveShortPieces must be between 1 and 10 substantial pieces.");
        }
    }

    private static void ValidateStationImaging(
        StationImagingPolicy? stationImaging,
        ICollection<string> errors)
    {
        if (stationImaging is null)
        {
            errors.Add("stationImaging is required.");
            return;
        }

        if (stationImaging.MinimumSubstantialPieces < 1
            || stationImaging.MaximumSubstantialPieces < stationImaging.MinimumSubstantialPieces
            || stationImaging.MaximumSubstantialPieces > 100)
        {
            errors.Add("stationImaging substantial-piece spacing must be between 1 and 100, with minimum not exceeding maximum.");
        }
    }

    private static void ValidatePromoCadence(
        ProgrammingPromoCadence? promoCadence,
        ICollection<string> errors)
    {
        if (promoCadence is null)
        {
            errors.Add("promoCadence is required.");
            return;
        }

        if (promoCadence.MinimumIntervalMinutes < 1
            || promoCadence.MaximumIntervalMinutes < promoCadence.MinimumIntervalMinutes
            || promoCadence.MaximumIntervalMinutes > 1440)
        {
            errors.Add("promoCadence intervals must be between 1 and 1440 minutes, with minimum not exceeding maximum.");
        }
    }

    private static void ValidatePersonalities(
        IReadOnlyList<ProgrammingPersonality>? personalities,
        ICollection<string> errors)
    {
        if (personalities is null)
        {
            errors.Add("personalities must be an array.");
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < personalities.Count; index++)
        {
            ProgrammingPersonality? personality = personalities[index];
            string location = $"personalities[{index}]";
            if (personality is null)
            {
                errors.Add($"{location} must not be null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(personality.Name)
                || !ProgrammingPersonalityNames.Supported.Contains(personality.Name))
            {
                errors.Add($"{location}.name must be one of: {string.Join(", ", ProgrammingPersonalityNames.Supported.Order())}.");
            }
            else if (!names.Add(personality.Name))
            {
                errors.Add($"Duplicate programming personality '{personality.Name}'.");
            }

            if (personality.Lanes is null || personality.Lanes.Count == 0)
            {
                errors.Add($"{location}.lanes must contain at least one lane.");
            }
            else
            {
                for (int laneIndex = 0; laneIndex < personality.Lanes.Count; laneIndex++)
                {
                    string? lane = personality.Lanes[laneIndex];
                    if (string.IsNullOrWhiteSpace(lane)
                        || !ProgrammingLaneNames.Supported.Contains(lane))
                    {
                        errors.Add(
                            $"{location}.lanes[{laneIndex}] must be one of: {string.Join(", ", ProgrammingLaneNames.Supported.Order())}.");
                    }
                }
            }
        }

        foreach (string required in ProgrammingPersonalityNames.Supported.Where(name => !names.Contains(name)))
        {
            errors.Add($"Required programming personality '{required}' is missing.");
        }
    }

    private static void ValidateCampaign(ActiveCampaign? campaign, ICollection<string> errors)
    {
        if (campaign is null)
        {
            errors.Add("activeCampaign is required.");
            return;
        }

        if (campaign.Enabled && string.IsNullOrWhiteSpace(campaign.ContentGroupId))
        {
            errors.Add("activeCampaign.contentGroupId is required when the campaign is enabled.");
        }

        if (!campaign.Enabled && !string.IsNullOrWhiteSpace(campaign.ContentGroupId))
        {
            errors.Add("activeCampaign.contentGroupId must be null when the campaign is disabled.");
        }

        ValidateWeight(campaign.WeightMultiplier, "activeCampaign.weightMultiplier", errors);
    }

    private static void ValidateAssetOverrides(
        IReadOnlyDictionary<string, AssetEditorialOverride>? overrides,
        ICollection<string> errors)
    {
        if (overrides is null)
        {
            errors.Add("assetOverrides must be an object.");
            return;
        }

        foreach ((string assetId, AssetEditorialOverride? value) in overrides)
        {
            if (string.IsNullOrWhiteSpace(assetId))
            {
                errors.Add("assetOverrides must not contain an empty assetId key.");
            }

            if (value is null)
            {
                errors.Add($"assetOverrides['{assetId}'] must be an object.");
                continue;
            }

            ValidateWeight(value.WeightMultiplier, $"assetOverrides['{assetId}'].weightMultiplier", errors);
        }
    }

    private static void ValidateWeight(double value, string location, ICollection<string> errors)
    {
        if (!double.IsFinite(value)
            || value < ProgrammingConfiguration.MinimumWeightMultiplier
            || value > ProgrammingConfiguration.MaximumWeightMultiplier)
        {
            errors.Add(
                $"{location} must be finite and between {ProgrammingConfiguration.MinimumWeightMultiplier:0.0} and {ProgrammingConfiguration.MaximumWeightMultiplier:0.0}.");
        }
    }
}

public static class ProgrammingConfigurationExtensions
{
    private static readonly AssetEditorialOverride DefaultOverride = new();

    public static AssetEditorialOverride GetAssetOverride(
        this ProgrammingConfiguration configuration,
        string assetId) =>
        configuration.AssetOverrides is not null
        && configuration.AssetOverrides.TryGetValue(assetId, out AssetEditorialOverride? value)
            ? value
            : DefaultOverride;
}

public static class ProgrammingContentClassifier
{
    public static IReadOnlySet<string> SubstantialTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AssetTypes.MusicVideo,
        AssetTypes.LyricVideo,
        AssetTypes.Visualizer,
        AssetTypes.AnimatedVisual,
        AssetTypes.Performance,
        AssetTypes.ShortForm,
        AssetTypes.Vlog,
        AssetTypes.Special,
    };

    public static bool IsSubstantial(string? type) =>
        type is not null && SubstantialTypes.Contains(type);

    public static bool IsCommercialInsertion(string? type) =>
        type is AssetTypes.Promo or AssetTypes.Advertisement;

    public static bool IsShortProgrammingPiece(PlaylistAsset asset, PlaylistPolicy policy) =>
        IsShortProgrammingPiece(asset.Type, asset.DurationSeconds, policy);

    public static bool IsShortProgrammingPiece(
        string? type,
        double? durationSeconds,
        PlaylistPolicy policy) =>
        type is not null
        && AssetTypes.IsSongBased(type)
        && (type == AssetTypes.ShortForm
            || durationSeconds is double duration
                && duration <= policy.ShortSongPresentationMaximumDuration.TotalSeconds);

    public static bool IsShortSongPresentation(PlaylistAsset asset, PlaylistPolicy policy) =>
        IsShortProgrammingPiece(asset, policy);

    public static bool MatchesLane(PlaylistAsset asset, string lane, PlaylistPolicy policy)
    {
        bool shortPresentation = IsShortSongPresentation(asset, policy);
        bool fullMusic = AssetTypes.IsSongBased(asset.Type) && !shortPresentation;
        bool shortOrPerformance = shortPresentation || asset.Type == AssetTypes.Performance;
        bool personality = asset.Type is AssetTypes.Vlog or AssetTypes.Special;
        return lane switch
        {
            ProgrammingLaneNames.FullMusic => fullMusic,
            ProgrammingLaneNames.ShortPerformance => shortOrPerformance,
            ProgrammingLaneNames.Personality => personality,
            ProgrammingLaneNames.PersonalityOrFullMusic => personality || fullMusic,
            _ => false,
        };
    }
}

public sealed record ProgrammingPatternPosition(string PersonalityName, string Lane, int LaneIndex);

public sealed class ProgrammingPatternSequencer
{
    private readonly ProgrammingPersonality[] _personalities;
    private readonly ProgrammingSequenceRandom _random;
    private int _currentPersonalityIndex = -1;
    private int _laneIndex;

    public ProgrammingPatternSequencer(
        IReadOnlyList<ProgrammingPersonality> personalities,
        int seed)
    {
        ArgumentNullException.ThrowIfNull(personalities);
        _personalities = personalities.ToArray();
        if (_personalities.Length == 0)
        {
            throw new ArgumentException("At least one programming personality is required.", nameof(personalities));
        }

        _random = new ProgrammingSequenceRandom(seed);
    }

    public ProgrammingPatternPosition Current
    {
        get
        {
            EnsurePersonality();
            ProgrammingPersonality personality = _personalities[_currentPersonalityIndex];
            return new ProgrammingPatternPosition(personality.Name!, personality.Lanes![_laneIndex], _laneIndex);
        }
    }

    public void Advance()
    {
        EnsurePersonality();
        _laneIndex++;
        if (_laneIndex >= _personalities[_currentPersonalityIndex].Lanes!.Count)
        {
            SelectNextPersonality();
        }
    }

    private void EnsurePersonality()
    {
        if (_currentPersonalityIndex < 0)
        {
            SelectNextPersonality();
        }
    }

    private void SelectNextPersonality()
    {
        int previous = _currentPersonalityIndex;
        int candidateCount = _personalities.Length - (previous >= 0 && _personalities.Length > 1 ? 1 : 0);
        int choice = _random.Next(candidateCount);
        if (previous >= 0 && _personalities.Length > 1 && choice >= previous)
        {
            choice++;
        }

        _currentPersonalityIndex = choice;
        _laneIndex = 0;
    }

    private sealed class ProgrammingSequenceRandom
    {
        private ulong _state;

        public ProgrammingSequenceRandom(int seed)
        {
            _state = unchecked((uint)seed) ^ 0xD1B54A32D192ED03UL;
        }

        public int Next(int maximumExclusive)
        {
            if (maximumExclusive <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumExclusive));
            }

            _state += 0x9E3779B97F4A7C15UL;
            ulong value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return (int)(value % (uint)maximumExclusive);
        }
    }
}

public static class ProgrammingWeighting
{
    public static double GetContentGroupWeight(
        string contentGroupId,
        IReadOnlyCollection<PlaylistAsset> presentations,
        ProgrammingConfiguration configuration,
        PlaylistPolicy policy,
        DateOnly scheduleDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentGroupId);
        ArgumentNullException.ThrowIfNull(presentations);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(policy);
        if (presentations.Count == 0
            || presentations.Any(asset => !string.Equals(
                asset.ContentGroupId,
                contentGroupId,
                StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Presentations must be a non-empty collection from one content group.",
                nameof(presentations));
        }

        double weight = configuration.ReleaseAgeHotRotationEnabled
            ? presentations.Max(asset => policy.GetRotationWeight(asset.RotationStartDate, scheduleDate))
            : 1.0;
        ActiveCampaign campaign = configuration.ActiveCampaign!;
        if (campaign.Enabled
            && string.Equals(campaign.ContentGroupId, contentGroupId, StringComparison.Ordinal))
        {
            weight *= campaign.WeightMultiplier;
        }

        return weight;
    }

    public static double GetPresentationWeight(
        PlaylistAsset asset,
        ProgrammingConfiguration configuration) =>
        configuration.GetAssetOverride(asset.AssetId).WeightMultiplier;
}

public static class ProgrammingPolicyAdapter
{
    public static PlaylistPolicy Apply(
        PlaylistPolicy basePolicy,
        ProgrammingConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(basePolicy);
        ArgumentNullException.ThrowIfNull(configuration);
        ProgrammingConfigurationValidator.ValidateStructure(configuration);

        ProgrammingRepetitionPolicy repetition = configuration.Repetition!;
        StationImagingPolicy imaging = configuration.StationImaging!;
        ProgrammingPromoCadence promo = configuration.PromoCadence!;
        return basePolicy with
        {
            ExactAssetCooldown = TimeSpan.FromMinutes(repetition.ExactAssetCooldownMinutes),
            ContentGroupCooldown = TimeSpan.Zero,
            ContentGroupMinimumCooldown = TimeSpan.Zero,
            ContentGroupMusicFirstRescueCooldown = TimeSpan.Zero,
            ShortToShortPreferredCooldown = TimeSpan.Zero,
            ShortToShortMinimumCooldown = TimeSpan.Zero,
            FullToShortPreferredCooldown = TimeSpan.Zero,
            FullToShortMinimumCooldown = TimeSpan.Zero,
            ShortToFullPreferredCooldown = TimeSpan.Zero,
            ShortToFullMinimumCooldown = TimeSpan.Zero,
            BumperCadence = new ProgramCountCadence(
                imaging.MinimumSubstantialPieces,
                imaging.MaximumSubstantialPieces),
            PromoCadence = new TimeCadence(
                TimeSpan.FromMinutes(promo.MinimumIntervalMinutes),
                TimeSpan.FromMinutes(promo.MaximumIntervalMinutes)),
            PromoInsertionTypes = new HashSet<string>(StringComparer.Ordinal)
            {
                AssetTypes.Promo,
                AssetTypes.Advertisement,
            },
            AdditionalNormalTypes = new HashSet<string>(StringComparer.Ordinal)
            {
                AssetTypes.Special,
            },
            UseSubstantialProgrammingForBumperCadence = true,
            HotRotationBands = configuration.ReleaseAgeHotRotationEnabled
                ? basePolicy.HotRotationBands
                : [],
            NormalRotationWeight = 1.0,
        };
    }
}
