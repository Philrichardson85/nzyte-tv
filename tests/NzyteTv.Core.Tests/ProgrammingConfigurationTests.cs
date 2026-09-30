using System.Text.Json;
using NzyteTv.Core;

namespace NzyteTv.Core.Tests;

public sealed class ProgrammingConfigurationTests
{
    [Fact]
    public void Defaults_RoundTripAndValidateAsSchemaVersionOne()
    {
        ProgrammingConfiguration configuration = ProgrammingConfiguration.CreateDefault();
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        string json = JsonSerializer.Serialize(configuration, options);
        ProgrammingConfiguration roundTrip = JsonSerializer.Deserialize<ProgrammingConfiguration>(json, options)!;

        ProgrammingConfigurationValidator.ValidateStructure(roundTrip);
        Assert.Equal(ProgrammingConfiguration.CurrentSchemaVersion, roundTrip.SchemaVersion);
        Assert.Equal(1, roundTrip.Revision);
        Assert.Equal(120, roundTrip.Repetition!.ExactAssetCooldownMinutes);
        Assert.Equal(2, roundTrip.Repetition.SameContentGroupLookback);
        Assert.Equal(3, roundTrip.Repetition.MaximumConsecutiveShortPieces);
        Assert.Equal(3, roundTrip.Personalities!.Count);
        Assert.Empty(roundTrip.AssetOverrides!);
    }

    [Fact]
    public void Defaults_ContainAllThreeValidInternalPatterns()
    {
        ProgrammingConfiguration configuration = ProgrammingConfiguration.CreateDefault();

        ProgrammingConfigurationValidator.ValidateStructure(configuration);

        Assert.Equal(
            ProgrammingPersonalityNames.Supported.Order(),
            configuration.Personalities!.Select(item => item.Name).Order());
        Assert.All(configuration.Personalities!, pattern => Assert.NotEmpty(pattern.Lanes!));
        Assert.All(
            configuration.Personalities!.SelectMany(pattern => pattern.Lanes!),
            lane => Assert.Contains(lane, ProgrammingLaneNames.Supported));
    }

    [Fact]
    public void MusicHeavyTemplateFavorsFullMusicLanes()
    {
        ProgrammingPersonality pattern = Pattern(ProgrammingPersonalityNames.MusicHeavy);

        IReadOnlyList<string> lanes = pattern.Lanes!;
        Assert.True(lanes.Count(lane => lane == ProgrammingLaneNames.FullMusic)
            > lanes.Count(lane => lane != ProgrammingLaneNames.FullMusic));
    }

    [Fact]
    public void MixedTemplateContainsFullShortAndPersonalityTexture()
    {
        ProgrammingPersonality pattern = Pattern(ProgrammingPersonalityNames.Mixed);

        Assert.Contains(ProgrammingLaneNames.FullMusic, pattern.Lanes!);
        Assert.Contains(ProgrammingLaneNames.ShortPerformance, pattern.Lanes!);
        Assert.Contains(ProgrammingLaneNames.Personality, pattern.Lanes!);
    }

    [Fact]
    public void FastPacedTemplateFavorsShortPerformanceMaterial()
    {
        ProgrammingPersonality pattern = Pattern(ProgrammingPersonalityNames.FastPaced);

        IReadOnlyList<string> lanes = pattern.Lanes!;
        Assert.Equal(3, lanes.Count(lane => lane == ProgrammingLaneNames.ShortPerformance));
        Assert.True(lanes.Count(lane => lane == ProgrammingLaneNames.ShortPerformance)
            > lanes.Count(lane => lane == ProgrammingLaneNames.Personality));
    }

    [Fact]
    public void PatternSelection_IsDeterministicAndNeverImmediatelyRepeatsAPersonality()
    {
        IReadOnlyList<ProgrammingPersonality> patterns = ProgrammingConfiguration.CreateDefault().Personalities!;
        string[] first = PatternStarts(new ProgrammingPatternSequencer(patterns, 8492), 12);
        string[] second = PatternStarts(new ProgrammingPatternSequencer(patterns, 8492), 12);

        Assert.Equal(first, second);
        Assert.All(first.Zip(first.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(10.1)]
    public void InvalidAssetWeight_IsRejected(double weight)
    {
        ProgrammingConfiguration configuration = ProgrammingConfiguration.CreateDefault() with
        {
            AssetOverrides = new Dictionary<string, AssetEditorialOverride>
            {
                ["asset-one"] = new() { WeightMultiplier = weight },
            },
        };

        Assert.Contains(
            ProgrammingConfigurationValidator.GetStructuralErrors(configuration),
            error => error.Contains("weightMultiplier", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidRepetitionCadenceAndLaneDefinitionsAreRejected()
    {
        ProgrammingConfiguration defaults = ProgrammingConfiguration.CreateDefault();
        ProgrammingConfiguration configuration = defaults with
        {
            Repetition = new ProgrammingRepetitionPolicy
            {
                ExactAssetCooldownMinutes = -1,
                SameContentGroupLookback = 0,
                MaximumConsecutiveShortPieces = 11,
            },
            StationImaging = new StationImagingPolicy
            {
                MinimumSubstantialPieces = 8,
                MaximumSubstantialPieces = 3,
            },
            PromoCadence = new ProgrammingPromoCadence
            {
                MinimumIntervalMinutes = 45,
                MaximumIntervalMinutes = 30,
            },
            Personalities =
            [
                .. defaults.Personalities!.Take(2),
                new ProgrammingPersonality
                {
                    Name = ProgrammingPersonalityNames.FastPaced,
                    Lanes = ["impossible-lane"],
                },
            ],
        };

        IReadOnlyList<string> errors = ProgrammingConfigurationValidator.GetStructuralErrors(configuration);

        Assert.Contains(errors, error => error.Contains("exactAssetCooldownMinutes", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("sameContentGroupLookback", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("maximumConsecutiveShortPieces", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("stationImaging", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("promoCadence", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("lanes[0]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void UnreasonableMaximumConsecutiveShortPiecesIsRejected(int maximum)
    {
        ProgrammingConfiguration configuration = ProgrammingConfiguration.CreateDefault() with
        {
            Repetition = ProgrammingConfiguration.CreateDefault().Repetition! with
            {
                MaximumConsecutiveShortPieces = maximum,
            },
        };

        Assert.Contains(
            ProgrammingConfigurationValidator.GetStructuralErrors(configuration),
            error => error.Contains("maximumConsecutiveShortPieces", StringComparison.Ordinal));
    }

    [Fact]
    public void ReferenceValidationRejectsUnknownCampaignAndAssetOverride()
    {
        ProgrammingConfiguration configuration = ProgrammingConfiguration.CreateDefault() with
        {
            ActiveCampaign = new ActiveCampaign
            {
                Enabled = true,
                ContentGroupId = "not-in-catalog",
            },
            AssetOverrides = new Dictionary<string, AssetEditorialOverride>
            {
                ["missing-asset"] = new() { DoNotAir = true },
            },
        };
        var catalog = new SongCatalog
        {
            Songs =
            [
                new SongCatalogEntry
                {
                    ContentGroupId = "known-song",
                    Title = "Known",
                    Artist = "Nzyte",
                },
            ],
        };

        IReadOnlyList<string> errors = ProgrammingConfigurationValidator.GetReferenceErrors(
            configuration,
            catalog,
            [new ProgrammingAssetInventoryEntry("known-asset", "known-song", AssetTypes.MusicVideo, true)]);

        Assert.Contains(errors, error => error.Contains("not-in-catalog", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("missing-asset", StringComparison.Ordinal));
    }

    [Fact]
    public void ReferenceValidationRejectsDoNotAirForEveryNormalAsset()
    {
        ProgrammingConfiguration configuration = ProgrammingConfiguration.CreateDefault() with
        {
            AssetOverrides = new Dictionary<string, AssetEditorialOverride>
            {
                ["one"] = new() { DoNotAir = true },
                ["two"] = new() { DoNotAir = true },
            },
        };
        var catalog = new SongCatalog { Songs = [] };

        IReadOnlyList<string> errors = ProgrammingConfigurationValidator.GetReferenceErrors(
            configuration,
            catalog,
            [
                new ProgrammingAssetInventoryEntry("one", null, AssetTypes.Vlog, true),
                new ProgrammingAssetInventoryEntry("two", null, AssetTypes.Special, true),
            ]);

        Assert.Contains(errors, error => error.Contains("every", StringComparison.Ordinal));
    }

    [Fact]
    public void CampaignFamilyWeightDoesNotGrowWithPresentationCount()
    {
        ProgrammingConfiguration normal = ProgrammingConfiguration.CreateDefault();
        ProgrammingConfiguration campaign = normal with
        {
            ActiveCampaign = new ActiveCampaign
            {
                Enabled = true,
                ContentGroupId = "song-a",
                WeightMultiplier = 2,
            },
        };
        PlaylistAsset[] many = Enumerable.Range(1, 12)
            .Select(index => Asset($"a-{index}", "song-a"))
            .ToArray();
        PlaylistAsset[] one = [Asset("b-1", "song-b")];
        var policy = new PlaylistPolicy();
        var date = new DateOnly(2026, 9, 30);

        Assert.Equal(1, ProgrammingWeighting.GetContentGroupWeight("song-a", many, normal, policy, date));
        Assert.Equal(1, ProgrammingWeighting.GetContentGroupWeight("song-b", one, normal, policy, date));
        Assert.Equal(2, ProgrammingWeighting.GetContentGroupWeight("song-a", many, campaign, policy, date));
        Assert.Equal(1, ProgrammingWeighting.GetContentGroupWeight("song-b", one, campaign, policy, date));
    }

    [Fact]
    public void ActivePolicyDisablesLegacyHotRotationByDefaultButCanExplicitlyEnableIt()
    {
        var legacy = new PlaylistPolicy();

        PlaylistPolicy defaultActive = ProgrammingPolicyAdapter.Apply(
            legacy,
            ProgrammingConfiguration.CreateDefault());
        PlaylistPolicy explicitlyEnabled = ProgrammingPolicyAdapter.Apply(
            legacy,
            ProgrammingConfiguration.CreateDefault() with { ReleaseAgeHotRotationEnabled = true });

        Assert.Empty(defaultActive.HotRotationBands);
        Assert.Equal(1, defaultActive.GetRotationWeight(new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 30)));
        Assert.NotEmpty(explicitlyEnabled.HotRotationBands);
        Assert.Equal(2.5, explicitlyEnabled.GetRotationWeight(new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 30)));
    }

    [Theory]
    [InlineData(AssetTypes.MusicVideo, true)]
    [InlineData(AssetTypes.LyricVideo, true)]
    [InlineData(AssetTypes.Visualizer, true)]
    [InlineData(AssetTypes.AnimatedVisual, true)]
    [InlineData(AssetTypes.Performance, true)]
    [InlineData(AssetTypes.ShortForm, true)]
    [InlineData(AssetTypes.Vlog, true)]
    [InlineData(AssetTypes.Special, true)]
    [InlineData(AssetTypes.Bumper, false)]
    [InlineData(AssetTypes.Promo, false)]
    [InlineData(AssetTypes.Interstitial, false)]
    [InlineData(AssetTypes.Advertisement, false)]
    public void SubstantialClassifierUsesExistingContentTypes(string type, bool expected) =>
        Assert.Equal(expected, ProgrammingContentClassifier.IsSubstantial(type));

    [Theory]
    [InlineData(AssetTypes.ShortForm, 300, true)]
    [InlineData(AssetTypes.MusicVideo, 60, true)]
    [InlineData(AssetTypes.LyricVideo, 60, true)]
    [InlineData(AssetTypes.Visualizer, 60, true)]
    [InlineData(AssetTypes.AnimatedVisual, 60, true)]
    [InlineData(AssetTypes.Performance, 60, true)]
    [InlineData(AssetTypes.MusicVideo, 61, false)]
    [InlineData(AssetTypes.Vlog, 30, false)]
    [InlineData(AssetTypes.Special, 30, false)]
    [InlineData(AssetTypes.Bumper, 10, false)]
    [InlineData(AssetTypes.Promo, 10, false)]
    [InlineData(AssetTypes.Interstitial, 10, false)]
    [InlineData(AssetTypes.Advertisement, 10, false)]
    public void ShortProgrammingPieceClassifierUsesSongTypeAndCentralDurationThreshold(
        string type,
        double durationSeconds,
        bool expected)
    {
        PlaylistAsset asset = new(
            "asset",
            AssetTypes.IsSongBased(type) ? "song" : null,
            "Asset",
            "Nzyte",
            type,
            null,
            $"{type}/asset.mp4",
            durationSeconds,
            null);

        Assert.Equal(
            expected,
            ProgrammingContentClassifier.IsShortProgrammingPiece(asset, new PlaylistPolicy()));
    }

    private static string[] PatternStarts(ProgrammingPatternSequencer sequencer, int count)
    {
        var starts = new List<string>();
        while (starts.Count < count)
        {
            ProgrammingPatternPosition current = sequencer.Current;
            if (current.LaneIndex == 0)
            {
                starts.Add(current.PersonalityName);
            }

            sequencer.Advance();
        }

        return starts.ToArray();
    }

    private static PlaylistAsset Asset(string assetId, string contentGroupId) => new(
        assetId,
        contentGroupId,
        assetId,
        "Nzyte",
        AssetTypes.MusicVideo,
        null,
        $"Music Videos/{assetId}.mp4",
        180,
        null);

    private static ProgrammingPersonality Pattern(string name) =>
        ProgrammingConfiguration.CreateDefault().Personalities!.Single(pattern => pattern.Name == name);
}
