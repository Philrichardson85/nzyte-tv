using NzyteTv.Core;

namespace NzyteTv.Core.Tests;

public sealed class PlaylistTargetPlannerTests
{
    [Fact]
    public void Calculate_SufficientInventoryKeepsConfiguredTargets()
    {
        PlaylistPolicy policy = Policy(
            TimeSpan.FromMinutes(10),
            (AssetTypes.MusicVideo, 0.5),
            (AssetTypes.Vlog, 0.5));

        PlaylistTargetPlan plan = PlaylistTargetPlanner.Calculate(
            [Asset("music", AssetTypes.MusicVideo, 300), Asset("vlog", AssetTypes.Vlog, 300)],
            policy);

        Assert.Equal(0.5, plan.EffectiveTargets[AssetTypes.MusicVideo], precision: 10);
        Assert.Equal(0.5, plan.EffectiveTargets[AssetTypes.Vlog], precision: 10);
        Assert.Empty(plan.CapacityLimitedCategories);
    }

    [Fact]
    public void Calculate_CapsDesiredAirtimeAtPracticalCapacity()
    {
        PlaylistPolicy policy = Policy(
            TimeSpan.FromHours(6),
            (AssetTypes.MusicVideo, 0.2),
            (AssetTypes.Vlog, 0.8));

        PlaylistTargetPlan plan = PlaylistTargetPlanner.Calculate(
            [Asset("music", AssetTypes.MusicVideo, 600), Asset("vlog", AssetTypes.Vlog, 21600)],
            policy);

        Assert.Equal(1800, plan.PracticalCapacitySeconds[AssetTypes.MusicVideo], precision: 6);
        Assert.Equal(1800 / 21600.0, plan.EffectiveTargets[AssetTypes.MusicVideo], precision: 10);
        Assert.Contains(AssetTypes.MusicVideo, plan.CapacityLimitedCategories);
    }

    [Fact]
    public void Calculate_AbsentCategoryHasZeroEffectiveTarget()
    {
        PlaylistPolicy policy = Policy(
            TimeSpan.FromHours(1),
            (AssetTypes.ShortForm, 0.5),
            (AssetTypes.Vlog, 0.5));

        PlaylistTargetPlan plan = PlaylistTargetPlanner.Calculate(
            [Asset("vlog", AssetTypes.Vlog, 3600)],
            policy);

        Assert.Equal(0, plan.EffectiveTargets[AssetTypes.ShortForm]);
        Assert.Equal(0, plan.PracticalCapacitySeconds[AssetTypes.ShortForm]);
        Assert.Contains(AssetTypes.ShortForm, plan.CapacityLimitedCategories);
    }

    [Fact]
    public void Calculate_MissingShortFormRedistributesToMusicBeforeVlog()
    {
        PlaylistPolicy policy = Policy(
            TimeSpan.FromMinutes(10),
            (AssetTypes.MusicVideo, 0.2),
            (AssetTypes.ShortForm, 0.6),
            (AssetTypes.Vlog, 0.2));

        PlaylistTargetPlan plan = PlaylistTargetPlanner.Calculate(
            [Asset("music", AssetTypes.MusicVideo, 600), Asset("vlog", AssetTypes.Vlog, 600)],
            policy);

        Assert.Equal(0.8, plan.EffectiveTargets[AssetTypes.MusicVideo], precision: 10);
        Assert.Equal(0, plan.EffectiveTargets[AssetTypes.ShortForm]);
        Assert.Equal(0.2, plan.EffectiveTargets[AssetTypes.Vlog], precision: 10);
    }

    [Fact]
    public void Calculate_MusicVideoCapacityRisesAutomaticallyWithInventory()
    {
        PlaylistPolicy policy = Policy(
            TimeSpan.FromHours(6),
            (AssetTypes.MusicVideo, 0.2),
            (AssetTypes.Vlog, 0.8));
        PlaylistTargetPlan small = PlaylistTargetPlanner.Calculate(
            [Asset("music", AssetTypes.MusicVideo, 618), Asset("vlog", AssetTypes.Vlog, 21600)],
            policy);
        PlaylistTargetPlan expanded = PlaylistTargetPlanner.Calculate(
            [
                Asset("music-1", AssetTypes.MusicVideo, 618),
                Asset("music-2", AssetTypes.MusicVideo, 618),
                Asset("music-3", AssetTypes.MusicVideo, 618),
                Asset("vlog", AssetTypes.Vlog, 21600),
            ],
            policy);

        Assert.True(expanded.EffectiveTargets[AssetTypes.MusicVideo]
            > small.EffectiveTargets[AssetTypes.MusicVideo]);
        Assert.Equal(0.2, expanded.EffectiveTargets[AssetTypes.MusicVideo], precision: 10);
    }

    [Fact]
    public void Calculate_LongerHorizonChangesPracticalRepeatCapacity()
    {
        PlaylistAsset[] assets = [Asset("music", AssetTypes.MusicVideo, 600)];

        PlaylistTargetPlan sixHours = PlaylistTargetPlanner.Calculate(
            assets,
            Policy(TimeSpan.FromHours(6), (AssetTypes.MusicVideo, 1)));
        PlaylistTargetPlan sevenHours = PlaylistTargetPlanner.Calculate(
            assets,
            Policy(TimeSpan.FromHours(7), (AssetTypes.MusicVideo, 1)));

        Assert.Equal(1800, sixHours.PracticalCapacitySeconds[AssetTypes.MusicVideo]);
        Assert.Equal(2400, sevenHours.PracticalCapacitySeconds[AssetTypes.MusicVideo]);
    }

    [Fact]
    public void Calculate_UsesDurationsRatherThanAssetCounts()
    {
        PlaylistPolicy policy = Policy(TimeSpan.FromHours(6), (AssetTypes.MusicVideo, 1));
        PlaylistTargetPlan shortAssets = PlaylistTargetPlanner.Calculate(
            [Asset("a", AssetTypes.MusicVideo, 30), Asset("b", AssetTypes.MusicVideo, 30)],
            policy);
        PlaylistTargetPlan longAssets = PlaylistTargetPlanner.Calculate(
            [Asset("a", AssetTypes.MusicVideo, 300), Asset("b", AssetTypes.MusicVideo, 300)],
            policy);

        Assert.Equal(10 * shortAssets.PracticalCapacitySeconds[AssetTypes.MusicVideo],
            longAssets.PracticalCapacitySeconds[AssetTypes.MusicVideo]);
    }

    [Fact]
    public void Calculate_EffectiveTargetsTotalOneWhenAggregateCapacityIsSufficient()
    {
        PlaylistPolicy policy = Policy(
            TimeSpan.FromMinutes(10),
            (AssetTypes.MusicVideo, 0.2),
            (AssetTypes.ShortForm, 0.5),
            (AssetTypes.Vlog, 0.3));

        PlaylistTargetPlan plan = PlaylistTargetPlanner.Calculate(
            [Asset("music", AssetTypes.MusicVideo, 600), Asset("vlog", AssetTypes.Vlog, 600)],
            policy);

        Assert.Equal(1, plan.EffectiveTargets.Values.Sum(), precision: 10);
        Assert.Equal(300, plan.RedistributedTargetAirtimeSeconds, precision: 6);
    }

    [Fact]
    public void Generate_CapacityPlanningDoesNotMakeExactAssetCooldownAHardLimit()
    {
        PlaylistPolicy policy = Policy(TimeSpan.FromMinutes(3), (AssetTypes.Vlog, 1));
        var generator = new PlaylistGenerator();

        PlaylistDocument playlist = generator.Generate(
            [Asset("vlog", AssetTypes.Vlog, 60)],
            [],
            PlaylistHistoryDocument.Empty,
            policy,
            seed: 1,
            generatedAtUtc: DateTimeOffset.UnixEpoch).Playlist;

        Assert.Equal(3, playlist.Items.Count);
        Assert.Equal(2, playlist.Summary.ExactAssetCooldownRelaxations);
        Assert.Contains(AssetTypes.Vlog, playlist.Summary.CapacityLimitedCategories);
    }

    [Fact]
    public void Calculate_IsDeterministic()
    {
        PlaylistPolicy policy = new() { TargetDuration = TimeSpan.FromHours(6) };
        PlaylistAsset[] assets = ProductionShapedAssets();

        PlaylistTargetPlan first = PlaylistTargetPlanner.Calculate(assets, policy);
        PlaylistTargetPlan second = PlaylistTargetPlanner.Calculate(assets.Reverse().ToArray(), policy);

        Assert.Equal(first.EffectiveTargets, second.EffectiveTargets);
        Assert.Equal(first.CapacityLimitedCategories, second.CapacityLimitedCategories);
        Assert.Equal(first.RedistributedTargetAirtimeSeconds, second.RedistributedTargetAirtimeSeconds);
    }

    [Fact]
    public void Calculate_ProductionShapedInventoryCapsImpossibleTargetsAndKeepsConfiguredPolicy()
    {
        var policy = new PlaylistPolicy { TargetDuration = TimeSpan.FromHours(6) };

        PlaylistTargetPlan plan = PlaylistTargetPlanner.Calculate(ProductionShapedAssets(), policy);

        Assert.Equal(0.20, policy.CategoryAirtimeTargets[AssetTypes.MusicVideo]);
        Assert.Equal(0.10, policy.CategoryAirtimeTargets[AssetTypes.Performance]);
        Assert.Equal(0.05, policy.CategoryAirtimeTargets[AssetTypes.ShortForm]);
        Assert.Equal(0, plan.EffectiveTargets[AssetTypes.ShortForm]);
        Assert.InRange(plan.EffectiveTargets[AssetTypes.MusicVideo], 0.085, 0.087);
        Assert.InRange(plan.EffectiveTargets[AssetTypes.Performance], 0.042, 0.044);
        Assert.Contains(AssetTypes.MusicVideo, plan.CapacityLimitedCategories);
        Assert.Contains(AssetTypes.Performance, plan.CapacityLimitedCategories);
        Assert.Contains(AssetTypes.ShortForm, plan.CapacityLimitedCategories);
        Assert.Equal(0.15, plan.EffectiveTargets[AssetTypes.Vlog], precision: 10);
        Assert.True(plan.EffectiveTargets[AssetTypes.AnimatedVisual] > 0.20);
        Assert.Equal(1, plan.EffectiveTargets.Values.Sum(), precision: 10);
    }

    private static PlaylistPolicy Policy(
        TimeSpan duration,
        params (string Type, double Target)[] targets) => new()
        {
            TargetDuration = duration,
            CategoryAirtimeTargets = targets.ToDictionary(item => item.Type, item => item.Target, StringComparer.Ordinal),
            BumperCadence = null,
            PromoCadence = null,
            InterstitialCadence = null,
        };

    private static PlaylistAsset Asset(string id, string type, double durationSeconds) =>
        new(id, type == AssetTypes.Vlog ? null : id, id, null, type, null, $"{type}/{id}.mp4", durationSeconds, null);

    private static PlaylistAsset[] ProductionShapedAssets() =>
        Assets(147, AssetTypes.AnimatedVisual, 71.10 * 60)
            .Concat(Assets(10, AssetTypes.LyricVideo, 28.21 * 60))
            .Concat(Assets(4, AssetTypes.MusicVideo, 10.30 * 60))
            .Concat(Assets(11, AssetTypes.Performance, 5.12 * 60))
            .Concat(Assets(9, AssetTypes.Visualizer, 25.88 * 60))
            .Concat(Assets(19, AssetTypes.Vlog, 24.74 * 60))
            .ToArray();

    private static IEnumerable<PlaylistAsset> Assets(int count, string type, double totalSeconds) =>
        Enumerable.Range(1, count).Select(index => Asset($"{type}-{index}", type, totalSeconds / count));
}
