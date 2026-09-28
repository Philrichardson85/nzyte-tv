using NzyteTv.Core;

namespace NzyteTv.Core.Tests;

public sealed class PlaylistGeneratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Generate_ZeroEligibleAssetsFailsClearly()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Generate(
            [],
            Policy(TimeSpan.FromHours(6), (AssetTypes.MusicVideo, 1)),
            seed: 1));

        Assert.Contains("No playlist-eligible assets", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_SameSeedAndInputsProducesSameOrdering()
    {
        PlaylistAsset[] assets = Enumerable.Range(1, 6)
            .Select(index => Asset($"song-{index}", AssetTypes.MusicVideo, 60, $"group-{index}"))
            .ToArray();
        PlaylistPolicy policy = Policy(TimeSpan.FromMinutes(8), (AssetTypes.MusicVideo, 1));

        PlaylistDocument first = Generate(assets, policy, seed: 42).Playlist;
        PlaylistDocument second = Generate(assets, policy, seed: 42).Playlist;

        Assert.Equal(first.Items.Select(item => item.AssetId), second.Items.Select(item => item.AssetId));
        Assert.Equal(first.ActualDurationSeconds, second.ActualDurationSeconds);
    }

    [Fact]
    public void Generate_DifferentSeedsCanProduceDifferentValidOrdering()
    {
        PlaylistAsset[] assets = Enumerable.Range(1, 8)
            .Select(index => Asset($"song-{index}", AssetTypes.MusicVideo, 60, $"group-{index}"))
            .ToArray();
        PlaylistPolicy policy = Policy(TimeSpan.FromMinutes(8), (AssetTypes.MusicVideo, 1));

        string[] first = Generate(assets, policy, seed: 1).Playlist.Items.Select(item => item.AssetId).ToArray();
        string[] second = Generate(assets, policy, seed: 2).Playlist.Items.Select(item => item.AssetId).ToArray();

        Assert.NotEqual(first, second);
        Assert.Equal(8, first.Length);
        Assert.Equal(8, second.Length);
    }

    [Fact]
    public void Generate_UsesWholeAssetsUntilTargetIsReachedOrExceeded()
    {
        PlaylistDocument playlist = Generate(
            [Asset("only", AssetTypes.MusicVideo, 60, "only-song")],
            Policy(TimeSpan.FromSeconds(100), (AssetTypes.MusicVideo, 1)),
            seed: 1).Playlist;

        Assert.Equal(120, playlist.ActualDurationSeconds);
        Assert.Equal(20, playlist.OverrunSeconds);
        Assert.Equal(2, playlist.Items.Count);
        Assert.All(playlist.Items, item => Assert.Equal(60, item.DurationSeconds));
    }

    [Fact]
    public void Generate_AirtimeSummaryUsesDurationRatherThanAssetCount()
    {
        PlaylistDocument playlist = Generate(
            [
                Asset("long-music", AssetTypes.MusicVideo, 300, "music"),
                Asset("short-lyric-1", AssetTypes.LyricVideo, 60, "lyric-1"),
                Asset("short-lyric-2", AssetTypes.LyricVideo, 60, "lyric-2"),
                Asset("short-lyric-3", AssetTypes.LyricVideo, 60, "lyric-3"),
            ],
            Policy(
                TimeSpan.FromMinutes(12),
                (AssetTypes.MusicVideo, 0.5),
                (AssetTypes.LyricVideo, 0.5)),
            seed: 7).Playlist;

        double musicSeconds = playlist.Items
            .Where(item => item.Type == AssetTypes.MusicVideo)
            .Sum(item => item.DurationSeconds);
        double expected = Math.Round(musicSeconds / playlist.ActualDurationSeconds * 100, 2);
        Assert.Equal(expected, playlist.Summary.AirtimePercentages[AssetTypes.MusicVideo]);
        Assert.NotEqual(
            Math.Round(playlist.Items.Count(item => item.Type == AssetTypes.MusicVideo) /
                (double)playlist.Items.Count * 100, 2),
            expected);
    }

    [Fact]
    public void Generate_CategoryTargetsDriveAirtimeWhenRulesCanBeSatisfied()
    {
        PlaylistPolicy policy = Policy(
            TimeSpan.FromHours(3),
            (AssetTypes.MusicVideo, 0.50),
            (AssetTypes.LyricVideo, 0.25),
            (AssetTypes.Vlog, 0.25)) with
        {
            ExactAssetCooldown = TimeSpan.Zero,
            ContentGroupCooldown = TimeSpan.Zero,
            ContentGroupMinimumCooldown = TimeSpan.Zero,
            ContentGroupMusicFirstRescueCooldown = TimeSpan.Zero,
        };
        PlaylistDocument playlist = Generate(
            [
                Asset("music", AssetTypes.MusicVideo, 300, "music"),
                Asset("lyric", AssetTypes.LyricVideo, 120, "lyric"),
                Asset("vlog", AssetTypes.Vlog, 180),
            ],
            policy,
            seed: 13).Playlist;

        Assert.InRange(playlist.Summary.AirtimePercentages[AssetTypes.MusicVideo], 45, 55);
        Assert.InRange(playlist.Summary.AirtimePercentages[AssetTypes.LyricVideo], 20, 30);
        Assert.InRange(playlist.Summary.AirtimePercentages[AssetTypes.Vlog], 20, 30);
    }

    [Fact]
    public void Policy_DefaultNormalProgramMixIncludesNewMusicCategoriesAndTotalsOneHundredPercent()
    {
        IReadOnlyDictionary<string, double> targets = new PlaylistPolicy().CategoryAirtimeTargets;

        Assert.Equal(0.20, targets[AssetTypes.MusicVideo]);
        Assert.Equal(0.15, targets[AssetTypes.LyricVideo]);
        Assert.Equal(0.15, targets[AssetTypes.Visualizer]);
        Assert.Equal(0.20, targets[AssetTypes.AnimatedVisual]);
        Assert.Equal(0.10, targets[AssetTypes.Performance]);
        Assert.Equal(0.05, targets[AssetTypes.ShortForm]);
        Assert.Equal(0.15, targets[AssetTypes.Vlog]);
        Assert.Equal(1.0, targets.Values.Sum(), precision: 10);
        Assert.DoesNotContain(AssetTypes.Promo, targets.Keys);
        Assert.DoesNotContain(AssetTypes.Bumper, targets.Keys);
        Assert.DoesNotContain(AssetTypes.Interstitial, targets.Keys);
        Assert.True(new HashSet<string>(StringComparer.Ordinal)
        {
            AssetTypes.MusicVideo,
            AssetTypes.LyricVideo,
            AssetTypes.Visualizer,
            AssetTypes.AnimatedVisual,
            AssetTypes.Performance,
            AssetTypes.ShortForm,
        }.SetEquals(new PlaylistPolicy().MusicOrientedNormalTypes));
    }

    [Fact]
    public void Generate_NewMusicCategoriesReceiveTargetedAndSeparatelyReportedAirtime()
    {
        PlaylistDocument playlist = Generate(
            [
                Asset("visualizer-a", AssetTypes.Visualizer, 60, "visualizer-a"),
                Asset("visualizer-b", AssetTypes.Visualizer, 60, "visualizer-b"),
                Asset("animated-a", AssetTypes.AnimatedVisual, 60, "animated-a"),
                Asset("animated-b", AssetTypes.AnimatedVisual, 60, "animated-b"),
            ],
            Policy(
                TimeSpan.FromMinutes(4),
                (AssetTypes.Visualizer, 0.5),
                (AssetTypes.AnimatedVisual, 0.5)) with
            {
                ExactAssetCooldown = TimeSpan.Zero,
                ContentGroupCooldown = TimeSpan.Zero,
                ContentGroupMinimumCooldown = TimeSpan.Zero,
                ContentGroupMusicFirstRescueCooldown = TimeSpan.Zero,
            },
            seed: 41).Playlist;

        Assert.Contains(playlist.Items, item => item.Type == AssetTypes.Visualizer);
        Assert.Contains(playlist.Items, item => item.Type == AssetTypes.AnimatedVisual);
        Assert.True(playlist.Summary.AirtimePercentages.ContainsKey(AssetTypes.Visualizer));
        Assert.True(playlist.Summary.AirtimePercentages.ContainsKey(AssetTypes.AnimatedVisual));
        Assert.Equal(100.0, playlist.Summary.AirtimePercentages.Values.Sum(), precision: 2);
    }

    [Fact]
    public void Generate_AvoidsImmediateExactDuplicateAndRespectsTwoHourCooldownWhenPossible()
    {
        PlaylistAsset[] assets =
        [
            Asset("a", AssetTypes.MusicVideo, 3600, "a"),
            Asset("b", AssetTypes.MusicVideo, 3600, "b"),
            Asset("c", AssetTypes.MusicVideo, 3600, "c"),
        ];
        PlaylistDocument playlist = Generate(
            assets,
            Policy(TimeSpan.FromHours(4), (AssetTypes.MusicVideo, 1)),
            seed: 9).Playlist;

        Assert.Equal(0, playlist.Summary.ExactAssetCooldownRelaxations);
        Assert.All(playlist.Items.Zip(playlist.Items.Skip(1)), pair =>
            Assert.NotEqual(pair.First.AssetId, pair.Second.AssetId));
        foreach (IGrouping<string, PlaylistItem> group in playlist.Items.GroupBy(item => item.AssetId))
        {
            PlaylistItem[] plays = group.OrderBy(item => item.StartOffsetSeconds).ToArray();
            Assert.All(plays.Zip(plays.Skip(1)), pair =>
                Assert.True(pair.Second.StartOffsetSeconds - pair.First.StartOffsetSeconds >= 7200));
        }
    }

    [Fact]
    public void Generate_TreatsMusicLyricAndShortFormAsOneSongFamily()
    {
        PlaylistAsset[] assets =
        [
            Asset("cash-music", AssetTypes.MusicVideo, 1800, "cash"),
            Asset("cash-lyric", AssetTypes.LyricVideo, 1800, "cash"),
            Asset("cash-pov", AssetTypes.ShortForm, 1800, "cash"),
            Asset("other-music", AssetTypes.MusicVideo, 1800, "other"),
            Asset("third-music", AssetTypes.MusicVideo, 1800, "third"),
        ];
        PlaylistPolicy policy = Policy(
            TimeSpan.FromHours(3),
            (AssetTypes.MusicVideo, 0.5),
            (AssetTypes.LyricVideo, 0.25),
            (AssetTypes.ShortForm, 0.25));

        PlaylistDocument playlist = Generate(assets, policy, seed: 11).Playlist;

        Assert.Equal(0, playlist.Summary.ContentGroupCooldownRelaxations);
        PlaylistItem[] cash = playlist.Items.Where(item => item.ContentGroupId == "cash")
            .OrderBy(item => item.StartOffsetSeconds).ToArray();
        Assert.True(cash.Length >= 2);
        Assert.All(cash.Zip(cash.Skip(1)), pair =>
            Assert.True(pair.Second.StartOffsetSeconds - pair.First.StartOffsetSeconds >= 1800));
    }

    [Theory]
    [InlineData(AssetTypes.AnimatedVisual, 60)]
    [InlineData(AssetTypes.Visualizer, 60)]
    public void Generate_ShortPresentationUsesControlledShortToShortWindow(string type, double duration)
    {
        PlaylistHistoryDocument history = History(Now,
            new PlaylistHistoryEntry("prior", "song", type, Now.AddMinutes(-12), duration));
        PlaylistDocument playlist = Generate(
            [Asset("next", type, duration, "song")],
            ShortPresentationPolicy(TimeSpan.FromSeconds(duration), (type, 1)),
            seed: 101,
            history: history).Playlist;

        Assert.Single(playlist.Items);
        Assert.Equal(1, playlist.Summary.ShortToShortPreferredRelaxations);
        Assert.Equal(0, playlist.Summary.EmergencyShortToShortFloorViolations);
    }

    [Fact]
    public void Generate_ShortPresentationFloorViolationIsSeparatelyReported()
    {
        PlaylistHistoryDocument history = History(Now,
            new PlaylistHistoryEntry("prior", "song", AssetTypes.AnimatedVisual, Now.AddMinutes(-9), 30));
        PlaylistDocument playlist = Generate(
            [Asset("next", AssetTypes.AnimatedVisual, 30, "song")],
            ShortPresentationPolicy(TimeSpan.FromSeconds(30), (AssetTypes.AnimatedVisual, 1)),
            seed: 102,
            history: history).Playlist;

        Assert.Equal(1, playlist.Summary.EmergencyShortToShortFloorViolations);
        Assert.Equal(0, playlist.Summary.EmergencyContentGroupFloorViolations);
    }

    [Theory]
    [InlineData(AssetTypes.MusicVideo, 180, AssetTypes.AnimatedVisual, 30, 25, 1, 0)]
    [InlineData(AssetTypes.AnimatedVisual, 30, AssetTypes.MusicVideo, 180, 20, 0, 1)]
    public void Generate_DirectionalShortPresentationWindowsAreReported(
        string priorType,
        double priorDuration,
        string nextType,
        double nextDuration,
        int elapsedMinutes,
        int expectedFullToShort,
        int expectedShortToFull)
    {
        PlaylistHistoryDocument history = History(Now,
            new PlaylistHistoryEntry("prior", "song", priorType, Now.AddMinutes(-elapsedMinutes), priorDuration));
        PlaylistDocument playlist = Generate(
            [Asset("next", nextType, nextDuration, "song")],
            ShortPresentationPolicy(TimeSpan.FromSeconds(nextDuration), (nextType, 1)),
            seed: 103,
            history: history).Playlist;

        Assert.Equal(expectedFullToShort, playlist.Summary.FullToShortPreferredRelaxations);
        Assert.Equal(expectedShortToFull, playlist.Summary.ShortToFullPreferredRelaxations);
        Assert.Equal(0, playlist.Summary.EmergencyFullToShortFloorViolations);
        Assert.Equal(0, playlist.Summary.EmergencyShortToFullFloorViolations);
    }

    [Theory]
    [InlineData(104)]
    [InlineData(20261002)]
    [InlineData(509)]
    public void Generate_ProductionShapedShortVisualLibraryAvoidsPathologicalVlogFallback(int seed)
    {
        PlaylistAsset[] assets = Enumerable.Range(1, 13)
            .SelectMany(group => Enumerable.Range(1, 11).Select(clip =>
                Asset($"short-{group}-{clip}", AssetTypes.AnimatedVisual, 25, $"song-{group}")))
            .Concat(Enumerable.Range(1, 13).SelectMany(group => new[]
            {
                Asset($"full-{group}-video", AssetTypes.MusicVideo, 240, $"song-{group}"),
                Asset($"full-{group}-lyric", AssetTypes.LyricVideo, 240, $"song-{group}"),
                Asset($"full-{group}-visualizer", AssetTypes.Visualizer, 240, $"song-{group}"),
                Asset($"full-{group}-performance", AssetTypes.Performance, 240, $"song-{group}"),
            }))
            .Concat(Enumerable.Range(1, 19).Select(index => Asset($"vlog-{index}", AssetTypes.Vlog, 360)))
            .Concat(Enumerable.Range(1, 18).Select(index => Asset($"promo-{index}", AssetTypes.Promo, 30)))
            .ToArray();
        PlaylistPolicy policy = new()
        {
            TargetDuration = TimeSpan.FromHours(6),
            BumperCadence = null,
            InterstitialCadence = null,
        };

        PlaylistDocument playlist = Generate(assets, policy, seed).Playlist;

        Assert.Empty(playlist.ExcludedAssets);
        Assert.Equal(0, playlist.Summary.EmergencyShortToShortFloorViolations);
        Assert.Equal(0, playlist.Summary.EmergencyFullToShortFloorViolations);
        Assert.Equal(0, playlist.Summary.EmergencyShortToFullFloorViolations);
        Assert.Equal(0, playlist.Summary.EmergencyContentGroupFloorViolations);
        Assert.InRange(playlist.Summary.EmergencyVlogRunViolations, 0, 2);
        Assert.True(playlist.ActualDurationSeconds >= policy.TargetDuration.TotalSeconds);
        Assert.True(playlist.Summary.AirtimePercentages.GetValueOrDefault(AssetTypes.Vlog) < 40,
            $"Vlog airtime was {playlist.Summary.AirtimePercentages.GetValueOrDefault(AssetTypes.Vlog)}%; " +
            $"music substitutions {playlist.Summary.MusicFirstCategorySubstitutions}; " +
            $"vlog fallbacks {playlist.Summary.VlogAboveTargetFallbacks}; " +
            $"emergency vlog runs {playlist.Summary.EmergencyVlogRunViolations}; " +
            $"age {playlist.Summary.CooldownAgePreferenceSubstitutions}; " +
            $"projected vlog {playlist.Summary.ProjectedVlogOvershootSubstitutions}; " +
            $"long efficiency {playlist.Summary.LongMusicAirtimeEfficiencySubstitutions}; " +
            $"airtime {string.Join(", ", playlist.Summary.AirtimePercentages.Select(item => $"{item.Key}={item.Value}"))}.");
        double fullMusicAirtime = new[]
        {
            AssetTypes.MusicVideo,
            AssetTypes.LyricVideo,
            AssetTypes.Visualizer,
            AssetTypes.Performance,
        }.Sum(type => playlist.Summary.AirtimePercentages.GetValueOrDefault(type));
        Assert.True(fullMusicAirtime > 30,
            $"Full-presentation categories received {fullMusicAirtime}% airtime; " +
            $"airtime {string.Join(", ", playlist.Summary.AirtimePercentages.Select(item => $"{item.Key}={item.Value}"))}; " +
            $"efficiency substitutions {playlist.Summary.LongMusicAirtimeEfficiencySubstitutions}.");
        Assert.True(playlist.Summary.MusicFirstCategorySubstitutions > 0);
    }

    [Fact]
    public void Generate_MissingShortFormTargetRelaxesToMusicBeforeOverTargetVlog()
    {
        PlaylistPolicy policy = Policy(
            TimeSpan.FromMinutes(3),
            (AssetTypes.MusicVideo, 0.2),
            (AssetTypes.ShortForm, 0.6),
            (AssetTypes.Vlog, 0.2)) with
        {
            ExactAssetCooldown = TimeSpan.Zero,
            ContentGroupCooldown = TimeSpan.Zero,
            ContentGroupMinimumCooldown = TimeSpan.Zero,
            ContentGroupMusicFirstRescueCooldown = TimeSpan.Zero,
        };
        PlaylistDocument playlist = Generate(
            [
                Asset("music", AssetTypes.MusicVideo, 60, "music"),
                Asset("vlog", AssetTypes.Vlog, 60),
            ],
            policy,
            seed: 201).Playlist;

        Assert.Equal(3, playlist.Items.Count(item => item.Type == AssetTypes.MusicVideo));
        Assert.DoesNotContain(playlist.Items, item => item.Type == AssetTypes.Vlog);
        Assert.Equal(0, playlist.Summary.VlogAboveTargetFallbacks);
        Assert.True(playlist.Summary.ProjectedVlogOvershootSubstitutions > 0);
    }

    [Fact]
    public void Generate_OverTargetAnimatedVisualStillBeatsOverTargetVlog()
    {
        PlaylistPolicy policy = ShortPresentationPolicy(
            TimeSpan.FromMinutes(3),
            (AssetTypes.AnimatedVisual, 0.2),
            (AssetTypes.ShortForm, 0.6),
            (AssetTypes.Vlog, 0.2)) with
        {
            ExactAssetCooldown = TimeSpan.Zero,
            ShortToShortPreferredCooldown = TimeSpan.Zero,
            ShortToShortMinimumCooldown = TimeSpan.Zero,
        };
        PlaylistDocument playlist = Generate(
            [
                Asset("animated", AssetTypes.AnimatedVisual, 60, "song"),
                Asset("vlog", AssetTypes.Vlog, 60),
            ],
            policy,
            seed: 202).Playlist;

        Assert.Equal(3, playlist.Items.Count(item => item.Type == AssetTypes.AnimatedVisual));
        Assert.DoesNotContain(playlist.Items, item => item.Type == AssetTypes.Vlog);
        Assert.True(playlist.Summary.ProjectedVlogOvershootSubstitutions > 0);
    }

    [Fact]
    public void Generate_AboveTargetVlogRemainsFallbackWhenFullMusicIsBelowHardFloor()
    {
        PlaylistDocument playlist = Generate(
            [
                Asset("full", AssetTypes.MusicVideo, 600, "song"),
                Asset("vlog", AssetTypes.Vlog, 360),
            ],
            Policy(
                TimeSpan.FromSeconds(1320),
                (AssetTypes.MusicVideo, 0.85),
                (AssetTypes.Vlog, 0.15)),
            seed: 203).Playlist;

        Assert.Equal(1, playlist.Items.Count(item => item.Type == AssetTypes.MusicVideo));
        Assert.Equal(2, playlist.Items.Count(item => item.Type == AssetTypes.Vlog));
        Assert.True(playlist.Summary.VlogAboveTargetFallbacks > 0);
        Assert.Equal(0, playlist.Summary.EmergencyContentGroupFloorViolations);
    }

    [Fact]
    public void Generate_MusicFirstDoesNotCrossShortPresentationFloor()
    {
        PlaylistDocument playlist = Generate(
            [
                Asset("short", AssetTypes.AnimatedVisual, 30, "song"),
                Asset("vlog", AssetTypes.Vlog, 300),
            ],
            ShortPresentationPolicy(
                TimeSpan.FromSeconds(630),
                (AssetTypes.AnimatedVisual, 0.85),
                (AssetTypes.Vlog, 0.15)),
            seed: 204).Playlist;

        Assert.Equal(1, playlist.Items.Count(item => item.Type == AssetTypes.AnimatedVisual));
        Assert.Equal(2, playlist.Items.Count(item => item.Type == AssetTypes.Vlog));
        Assert.True(playlist.Summary.VlogAboveTargetFallbacks > 0);
        Assert.Equal(0, playlist.Summary.EmergencyShortToShortFloorViolations);
    }

    [Theory]
    [InlineData(AssetTypes.MusicVideo)]
    [InlineData(AssetTypes.LyricVideo)]
    [InlineData(AssetTypes.Visualizer)]
    [InlineData(AssetTypes.Performance)]
    public void Generate_UnderTargetFullPresentationBeatsOverTargetShortPresentation(string fullType)
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("prior-full", "full-song", fullType, Now.AddMinutes(-70), 180));
        PlaylistDocument playlist = Generate(
            [
                Asset("short-a", AssetTypes.AnimatedVisual, 30, "short-a"),
                Asset("short-b", AssetTypes.AnimatedVisual, 30, "short-b"),
                Asset("full", fullType, 180, "full-song"),
            ],
            ShortPresentationPolicy(
                TimeSpan.FromSeconds(210),
                (fullType, 0.5),
                (AssetTypes.AnimatedVisual, 0.5)),
            seed: 205,
            history: history).Playlist;

        Assert.Equal(AssetTypes.AnimatedVisual, playlist.Items[0].Type);
        Assert.Equal(fullType, playlist.Items[1].Type);
        Assert.Equal(1, playlist.Summary.FullPresentationPrioritySubstitutions);
        Assert.Equal(1, playlist.Summary.ContentGroupCooldownRelaxations);
    }

    [Fact]
    public void Generate_UnderTargetFullPresentationProtectsAgainstSameGroupShortReset()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("prior-full", "song", AssetTypes.MusicVideo, Now.AddMinutes(-70), 180));
        PlaylistDocument playlist = Generate(
            [
                Asset("short", AssetTypes.AnimatedVisual, 30, "song"),
                Asset("full", AssetTypes.MusicVideo, 180, "song"),
            ],
            ShortPresentationPolicy(
                TimeSpan.FromSeconds(180),
                (AssetTypes.MusicVideo, 0.5),
                (AssetTypes.AnimatedVisual, 0.5)),
            seed: 206,
            history: history).Playlist;

        Assert.Equal("full", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(1, playlist.Summary.FullPresentationPrioritySubstitutions);
    }

    [Fact]
    public void Generate_ShortMayWinWhenFullCategoryIsAlreadyAboveTarget()
    {
        PlaylistDocument playlist = Generate(
            [
                Asset("full", AssetTypes.MusicVideo, 120, "full-song"),
                Asset("short", AssetTypes.AnimatedVisual, 30, "short-song"),
            ],
            ShortPresentationPolicy(
                TimeSpan.FromSeconds(150),
                (AssetTypes.MusicVideo, 0.9),
                (AssetTypes.AnimatedVisual, 0.1)),
            seed: 207).Playlist;

        Assert.Equal(AssetTypes.MusicVideo, playlist.Items[0].Type);
        Assert.Equal(AssetTypes.AnimatedVisual, playlist.Items[1].Type);
        Assert.Equal(0, playlist.Summary.FullPresentationPrioritySubstitutions);
    }

    [Fact]
    public void Generate_CategoryDeficitUsesAirtimeRatherThanAssetCount()
    {
        PlaylistAsset[] assets = Enumerable.Range(1, 20)
            .Select(index => Asset($"short-{index}", AssetTypes.AnimatedVisual, 10, $"short-{index}"))
            .Append(Asset("full", AssetTypes.MusicVideo, 100, "full"))
            .ToArray();
        PlaylistDocument playlist = Generate(
            assets,
            ShortPresentationPolicy(
                TimeSpan.FromSeconds(400),
                (AssetTypes.MusicVideo, 0.5),
                (AssetTypes.AnimatedVisual, 0.5)) with
            {
                ExactAssetCooldown = TimeSpan.Zero,
                ContentGroupCooldown = TimeSpan.Zero,
                ContentGroupMinimumCooldown = TimeSpan.Zero,
                ContentGroupMusicFirstRescueCooldown = TimeSpan.Zero,
                ShortToShortPreferredCooldown = TimeSpan.Zero,
                ShortToShortMinimumCooldown = TimeSpan.Zero,
            },
            seed: 208).Playlist;

        Assert.InRange(playlist.Summary.AirtimePercentages[AssetTypes.MusicVideo], 40, 60);
        Assert.True(playlist.Items.Count(item => item.Type == AssetTypes.AnimatedVisual)
            > playlist.Items.Count(item => item.Type == AssetTypes.MusicVideo));
    }

    [Fact]
    public void Generate_OlderComparableContentGroupWinsAndReportsAgePreference()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("recent-prior", "recent", AssetTypes.AnimatedVisual, Now.AddMinutes(-20), 30),
            new PlaylistHistoryEntry("old-prior", "old", AssetTypes.AnimatedVisual, Now.AddMinutes(-40), 30));
        PlaylistDocument playlist = Generate(
            [
                Asset("recent-next", AssetTypes.AnimatedVisual, 30, "recent"),
                Asset("old-next", AssetTypes.AnimatedVisual, 30, "old"),
            ],
            ShortPresentationPolicy(TimeSpan.FromSeconds(30), (AssetTypes.AnimatedVisual, 1)),
            seed: 301,
            history: history).Playlist;

        Assert.Equal("old-next", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(1, playlist.Summary.CooldownAgePreferenceSubstitutions);
    }

    [Fact]
    public void Generate_ElevenMinuteShortGroupLosesToFortyMinuteShortGroup()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("recent-prior", "recent", AssetTypes.AnimatedVisual, Now.AddMinutes(-11), 30),
            new PlaylistHistoryEntry("old-prior", "old", AssetTypes.AnimatedVisual, Now.AddMinutes(-40), 30));
        PlaylistDocument playlist = Generate(
            [
                Asset("recent-next", AssetTypes.AnimatedVisual, 30, "recent"),
                Asset("old-next", AssetTypes.AnimatedVisual, 30, "old"),
            ],
            ShortPresentationPolicy(TimeSpan.FromSeconds(30), (AssetTypes.AnimatedVisual, 1)),
            seed: 302,
            history: history).Playlist;

        Assert.Equal("old-next", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(0, playlist.Summary.EmergencyShortToShortFloorViolations);
    }

    [Theory]
    [InlineData(AssetTypes.MusicVideo)]
    [InlineData(AssetTypes.LyricVideo)]
    [InlineData(AssetTypes.Visualizer)]
    public void Generate_LongUnderTargetMusicUsesAirtimeEfficiencyWhenMusicIsDeficient(string fullType)
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("prior-full", "full-song", fullType, Now.AddMinutes(-70), 180));
        PlaylistPolicy policy = ShortPresentationPolicy(
            TimeSpan.FromSeconds(300),
            (fullType, 0.15),
            (AssetTypes.AnimatedVisual, 0.05),
            (AssetTypes.Vlog, 0.80)) with
        {
            ProjectedVlogOvershootTolerance = 0.50,
        };
        PlaylistDocument playlist = Generate(
            [
                Asset("vlog", AssetTypes.Vlog, 120),
                Asset("short", AssetTypes.AnimatedVisual, 20, "short-song"),
                Asset("full", fullType, 180, "full-song"),
            ],
            policy,
            seed: 303,
            history: history).Playlist;

        Assert.Equal(AssetTypes.Vlog, playlist.Items[0].Type);
        Assert.Equal(fullType, playlist.Items[1].Type);
        Assert.True(playlist.Summary.LongMusicAirtimeEfficiencySubstitutions > 0);
        Assert.Equal(0, playlist.Summary.EmergencyContentGroupFloorViolations);
    }

    [Fact]
    public void Generate_LongVlogProjectedFromFourteenPercentLosesToLegalMusic()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("long-vlog", null, AssetTypes.Vlog, Now.AddMinutes(-105)),
            new PlaylistHistoryEntry("next-music-prior", "next-music", AssetTypes.MusicVideo, Now.AddMinutes(-50), 180));
        PlaylistDocument playlist = Generate(
            [
                Asset("opening-music", AssetTypes.MusicVideo, 860, "opening-music"),
                Asset("short-vlog", AssetTypes.Vlog, 140),
                Asset("long-vlog", AssetTypes.Vlog, 600),
                Asset("next-music", AssetTypes.MusicVideo, 180, "next-music"),
            ],
            Policy(
                TimeSpan.FromSeconds(1180),
                (AssetTypes.MusicVideo, 0.85),
                (AssetTypes.Vlog, 0.15)),
            seed: 304,
            history: history).Playlist;

        Assert.Equal(["opening-music", "short-vlog", "next-music"],
            playlist.Items.Select(item => item.AssetId));
        Assert.Equal(1, playlist.Summary.ProjectedVlogOvershootSubstitutions);
    }

    [Fact]
    public void Generate_LongVlogProjectedFromFourteenPercentRemainsFallbackWithoutLegalMusic()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("long-vlog", null, AssetTypes.Vlog, Now.AddMinutes(-105)));
        PlaylistDocument playlist = Generate(
            [
                Asset("opening-music", AssetTypes.MusicVideo, 860, "opening-music"),
                Asset("short-vlog", AssetTypes.Vlog, 140),
                Asset("long-vlog", AssetTypes.Vlog, 600),
            ],
            Policy(
                TimeSpan.FromSeconds(1600),
                (AssetTypes.MusicVideo, 0.85),
                (AssetTypes.Vlog, 0.15)),
            seed: 305,
            history: history).Playlist;

        Assert.Equal(["opening-music", "short-vlog", "long-vlog"],
            playlist.Items.Select(item => item.AssetId));
        Assert.Equal(0, playlist.Summary.ProjectedVlogOvershootSubstitutions);
        Assert.Equal(0, playlist.Summary.EmergencyContentGroupFloorViolations);
    }

    [Fact]
    public void Generate_AvoidsConsecutiveVlogsWhenAlternativeExists()
    {
        PlaylistDocument playlist = Generate(
            [
                Asset("vlog-a", AssetTypes.Vlog, 60),
                Asset("vlog-b", AssetTypes.Vlog, 60),
                Asset("music", AssetTypes.MusicVideo, 60, "music"),
            ],
            Policy(
                TimeSpan.FromMinutes(8),
                (AssetTypes.Vlog, 0.8),
                (AssetTypes.MusicVideo, 0.2)) with
            {
                ExactAssetCooldown = TimeSpan.Zero,
                ContentGroupCooldown = TimeSpan.Zero,
                ContentGroupMinimumCooldown = TimeSpan.Zero,
                ContentGroupMusicFirstRescueCooldown = TimeSpan.Zero,
            },
            seed: 5).Playlist;

        Assert.Equal(0, playlist.Summary.ConsecutiveVlogViolations);
        Assert.DoesNotContain(playlist.Items.Zip(playlist.Items.Skip(1)), pair =>
            pair.First.Type == AssetTypes.Vlog && pair.Second.Type == AssetTypes.Vlog);
    }

    [Fact]
    public void Generate_RelaxesCategoryBeforeExactAssetCooldown()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("music", "music-group", AssetTypes.MusicVideo, Now.AddMinutes(-1)));
        PlaylistDocument playlist = Generate(
            [
                Asset("music", AssetTypes.MusicVideo, 60, "music-group"),
                Asset("vlog", AssetTypes.Vlog, 60),
            ],
            Policy(
                TimeSpan.FromMinutes(1),
                (AssetTypes.MusicVideo, 0.9),
                (AssetTypes.Vlog, 0.1)),
            seed: 3,
            history: history).Playlist;

        Assert.Equal("vlog", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(1, playlist.Summary.CategoryTargetRelaxations);
        Assert.Equal(0, playlist.Summary.ExactAssetCooldownRelaxations);
        Assert.Equal(0, playlist.Summary.VlogAboveTargetFallbacks);
    }

    [Fact]
    public void Generate_RelaxesExactAssetBeforeSameSongCooldown()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("repeat", null, AssetTypes.Vlog, Now.AddMinutes(-1)),
            new PlaylistHistoryEntry("prior-same-group", "blocked-group", AssetTypes.LyricVideo, Now.AddMinutes(-1)));
        PlaylistDocument playlist = Generate(
            [
                Asset("repeat", AssetTypes.Vlog, 60),
                Asset("same-song-new-video", AssetTypes.MusicVideo, 60, "blocked-group"),
            ],
            Policy(
                TimeSpan.FromMinutes(1),
                (AssetTypes.MusicVideo, 0.5),
                (AssetTypes.Vlog, 0.5)),
            seed: 1,
            history: history).Playlist;

        Assert.Equal("repeat", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(1, playlist.Summary.ExactAssetCooldownRelaxations);
        Assert.Equal(0, playlist.Summary.ContentGroupCooldownRelaxations);
    }

    [Fact]
    public void Generate_BypassesHotPreferenceBeforeSameSongCooldown()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("prior-hot", "hot-group", AssetTypes.MusicVideo, Now.AddMinutes(-1)));
        PlaylistAsset[] assets =
        [
            Asset("hot", AssetTypes.MusicVideo, 60, "hot-group", NowDate.AddDays(-1)),
            Asset("normal", AssetTypes.MusicVideo, 60, "normal-group"),
        ];
        PlaylistPolicy policy = Policy(TimeSpan.FromMinutes(1), (AssetTypes.MusicVideo, 1));
        PlaylistDocument playlist = Generate(assets, policy, seed: 1, history).Playlist;

        Assert.Equal("normal", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(1, playlist.Summary.NewReleasePreferenceBypasses);
        Assert.Equal(0, playlist.Summary.ContentGroupCooldownRelaxations);
    }

    [Fact]
    public void Generate_AllowsSecondVlogBeforeRelaxingSongCooldown()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("prior-music", "music-group", AssetTypes.MusicVideo, Now.AddMinutes(-1)),
            new PlaylistHistoryEntry("prior-vlog", null, AssetTypes.Vlog, Now));
        PlaylistDocument playlist = Generate(
            [
                Asset("music", AssetTypes.MusicVideo, 60, "music-group"),
                Asset("vlog", AssetTypes.Vlog, 60),
            ],
            Policy(
                TimeSpan.FromMinutes(1),
                (AssetTypes.MusicVideo, 0.5),
                (AssetTypes.Vlog, 0.5)),
            seed: 8,
            history: history).Playlist;

        Assert.Equal("vlog", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(0, playlist.Summary.ContentGroupCooldownRelaxations);
        Assert.Equal(0, playlist.Summary.EmergencyContentGroupFloorViolations);
        Assert.Equal(1, playlist.Summary.ConsecutiveVlogViolations);
        Assert.Equal(0, playlist.Summary.EmergencyVlogRunViolations);
    }

    [Fact]
    public void Generate_ContentGroupAtNinetyMinutesIsFullyPreferred()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("song-music", "song", AssetTypes.MusicVideo, Now.AddMinutes(-90)));

        PlaylistDocument playlist = Generate(
            [Asset("song-lyric", AssetTypes.LyricVideo, 60, "song")],
            Policy(TimeSpan.FromMinutes(1), (AssetTypes.LyricVideo, 1)),
            seed: 2,
            history: history).Playlist;

        Assert.Equal("song-lyric", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(0, playlist.Summary.ContentGroupCooldownRelaxations);
        Assert.Equal(0, playlist.Summary.EmergencyContentGroupFloorViolations);
    }

    [Fact]
    public void Generate_DoesNotNormallyScheduleThirdConsecutiveVlog()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("prior-song", "song", AssetTypes.MusicVideo, Now.AddMinutes(-70)),
            new PlaylistHistoryEntry("vlog-a", null, AssetTypes.Vlog, Now.AddMinutes(-1)),
            new PlaylistHistoryEntry("vlog-b", null, AssetTypes.Vlog, Now));

        PlaylistDocument playlist = Generate(
            [
                Asset("song-alternate", AssetTypes.LyricVideo, 60, "song"),
                Asset("vlog-c", AssetTypes.Vlog, 60),
            ],
            Policy(
                TimeSpan.FromMinutes(1),
                (AssetTypes.LyricVideo, 0.5),
                (AssetTypes.Vlog, 0.5)),
            seed: 4,
            history: history).Playlist;

        Assert.Equal("song-alternate", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(1, playlist.Summary.ContentGroupCooldownRelaxations);
        Assert.Equal(0, playlist.Summary.EmergencyContentGroupFloorViolations);
        Assert.Equal(0, playlist.Summary.EmergencyVlogRunViolations);
    }

    [Fact]
    public void Generate_UsesMusicFirstRescueBeforeThirdConsecutiveVlog()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("song-music", "song", AssetTypes.MusicVideo, Now.AddMinutes(-50)),
            new PlaylistHistoryEntry("vlog-a", null, AssetTypes.Vlog, Now.AddMinutes(-1)),
            new PlaylistHistoryEntry("vlog-b", null, AssetTypes.Vlog, Now));

        PlaylistDocument playlist = Generate(
            [
                Asset("song-performance", AssetTypes.Performance, 60, "song"),
                Asset("vlog-c", AssetTypes.Vlog, 60),
            ],
            Policy(
                TimeSpan.FromMinutes(1),
                (AssetTypes.Performance, 0.5),
                (AssetTypes.Vlog, 0.5)),
            seed: 21,
            history: history).Playlist;

        Assert.Equal("song-performance", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(1, playlist.Summary.MusicFirstRescueRelaxations);
        Assert.Equal(0, playlist.Summary.ContentGroupCooldownRelaxations);
        Assert.Equal(0, playlist.Summary.EmergencyContentGroupFloorViolations);
        Assert.Equal(0, playlist.Summary.EmergencyVlogRunViolations);
    }

    [Fact]
    public void Generate_DoesNotUseMusicFirstRescueForCategoryTargeting()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("rescue-prior", "rescue", AssetTypes.MusicVideo, Now.AddMinutes(-50)));

        PlaylistDocument playlist = Generate(
            [
                Asset("rescue-lyric", AssetTypes.LyricVideo, 60, "rescue"),
                Asset("normal-music", AssetTypes.MusicVideo, 60, "normal"),
            ],
            Policy(
                TimeSpan.FromMinutes(1),
                (AssetTypes.LyricVideo, 0.99),
                (AssetTypes.MusicVideo, 0.01)),
            seed: 22,
            history: history).Playlist;

        Assert.Equal("normal-music", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(0, playlist.Summary.MusicFirstRescueRelaxations);
        Assert.Equal(0, playlist.Summary.EmergencyContentGroupFloorViolations);
    }

    [Fact]
    public void Generate_NormalFloorCandidateWinsBeforeMusicFirstRescue()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("rescue-prior", "rescue", AssetTypes.MusicVideo, Now.AddMinutes(-50)),
            new PlaylistHistoryEntry("normal-prior", "normal", AssetTypes.MusicVideo, Now.AddMinutes(-70)),
            new PlaylistHistoryEntry("vlog-a", null, AssetTypes.Vlog, Now.AddMinutes(-1)),
            new PlaylistHistoryEntry("vlog-b", null, AssetTypes.Vlog, Now));

        PlaylistDocument playlist = Generate(
            [
                Asset("rescue-lyric", AssetTypes.LyricVideo, 60, "rescue"),
                Asset("normal-performance", AssetTypes.Performance, 60, "normal"),
                Asset("vlog-c", AssetTypes.Vlog, 60),
            ],
            Policy(
                TimeSpan.FromMinutes(1),
                (AssetTypes.LyricVideo, 0.34),
                (AssetTypes.Performance, 0.33),
                (AssetTypes.Vlog, 0.33)),
            seed: 23,
            history: history).Playlist;

        Assert.Equal("normal-performance", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(1, playlist.Summary.ContentGroupCooldownRelaxations);
        Assert.Equal(0, playlist.Summary.MusicFirstRescueRelaxations);
        Assert.Equal(0, playlist.Summary.EmergencyVlogRunViolations);
    }

    [Fact]
    public void Generate_SubFortyFiveMinuteRepeatIsEmergencyFloorViolation()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("song-music", "song", AssetTypes.MusicVideo, Now.AddMinutes(-44)));

        PlaylistDocument playlist = Generate(
            [Asset("song-lyric", AssetTypes.LyricVideo, 60, "song")],
            Policy(TimeSpan.FromMinutes(1), (AssetTypes.LyricVideo, 1)),
            seed: 24,
            history: history).Playlist;

        Assert.Equal("song-lyric", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(0, playlist.Summary.MusicFirstRescueRelaxations);
        Assert.Equal(1, playlist.Summary.EmergencyContentGroupFloorViolations);
    }

    [Fact]
    public void Generate_ThirdVlogEmergencyRemainsWhenNoRescueCandidateExists()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("song-music", "song", AssetTypes.MusicVideo, Now.AddMinutes(-44)),
            new PlaylistHistoryEntry("vlog-a", null, AssetTypes.Vlog, Now.AddMinutes(-1)),
            new PlaylistHistoryEntry("vlog-b", null, AssetTypes.Vlog, Now));

        PlaylistDocument playlist = Generate(
            [
                Asset("song-lyric", AssetTypes.LyricVideo, 60, "song"),
                Asset("vlog-c", AssetTypes.Vlog, 60),
            ],
            Policy(
                TimeSpan.FromMinutes(1),
                (AssetTypes.LyricVideo, 0.5),
                (AssetTypes.Vlog, 0.5)),
            seed: 25,
            history: history).Playlist;

        Assert.Equal("vlog-c", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(0, playlist.Summary.MusicFirstRescueRelaxations);
        Assert.Equal(1, playlist.Summary.EmergencyVlogRunViolations);
        Assert.Equal(0, playlist.Summary.EmergencyContentGroupFloorViolations);
    }

    [Fact]
    public void Generate_ContentGroupMayRelaxOnlyIntoSixtyToNinetyMinuteWindow()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("song-music", "song", AssetTypes.MusicVideo, Now.AddMinutes(-70)));

        PlaylistDocument playlist = Generate(
            [Asset("song-performance", AssetTypes.Performance, 60, "song")],
            Policy(TimeSpan.FromMinutes(1), (AssetTypes.Performance, 1)),
            seed: 5,
            history: history).Playlist;

        Assert.Equal("song-performance", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(1, playlist.Summary.ContentGroupCooldownRelaxations);
        Assert.Equal(0, playlist.Summary.EmergencyContentGroupFloorViolations);
    }

    [Fact]
    public void Generate_NormalCandidatePreventsSubSixtyMinuteSongRepeat()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("cash-music", "cash", AssetTypes.MusicVideo, Now.AddMinutes(-10)));

        PlaylistDocument playlist = Generate(
            [
                Asset("cash-lyric", AssetTypes.LyricVideo, 60, "cash"),
                Asset("other-song", AssetTypes.MusicVideo, 60, "other"),
            ],
            Policy(
                TimeSpan.FromMinutes(1),
                (AssetTypes.MusicVideo, 0.5),
                (AssetTypes.LyricVideo, 0.5)),
            seed: 6,
            history: history).Playlist;

        Assert.Equal("other-song", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(0, playlist.Summary.ContentGroupCooldownRelaxations);
        Assert.Equal(0, playlist.Summary.EmergencyContentGroupFloorViolations);
    }

    [Theory]
    [InlineData(AssetTypes.MusicVideo, AssetTypes.Performance)]
    [InlineData(AssetTypes.MusicVideo, AssetTypes.ShortForm)]
    [InlineData(AssetTypes.MusicVideo, AssetTypes.Visualizer)]
    [InlineData(AssetTypes.LyricVideo, AssetTypes.AnimatedVisual)]
    [InlineData(AssetTypes.Visualizer, AssetTypes.AnimatedVisual)]
    public void Generate_AlternateVisualCannotBypassSharedContentGroupClock(
        string priorType,
        string alternateType)
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("cash-prior", "cash", priorType, Now.AddMinutes(-30)));

        PlaylistDocument playlist = Generate(
            [
                Asset("cash-alternate", alternateType, 60, "cash"),
                Asset("other-song", AssetTypes.MusicVideo, 60, "other"),
            ],
            Policy(
                TimeSpan.FromMinutes(1),
                (alternateType, 0.5),
                (AssetTypes.MusicVideo, 0.5)),
            seed: 7,
            history: history).Playlist;

        Assert.Equal(
            alternateType == AssetTypes.ShortForm ? "cash-alternate" : "other-song",
            Assert.Single(playlist.Items).AssetId);
        Assert.Equal(0, playlist.Summary.EmergencyContentGroupFloorViolations);
    }

    [Fact]
    public void Generate_OneSongLibraryUsesObservableEmergencyFloorFallback()
    {
        PlaylistDocument playlist = Generate(
            [Asset("only-song", AssetTypes.MusicVideo, 300, "only-group")],
            Policy(TimeSpan.FromMinutes(20), (AssetTypes.MusicVideo, 1)),
            seed: 8).Playlist;

        Assert.Equal(4, playlist.Items.Count);
        Assert.Equal(0, playlist.Summary.ContentGroupCooldownRelaxations);
        Assert.Equal(3, playlist.Summary.EmergencyContentGroupFloorViolations);
        Assert.All(playlist.Items.Zip(playlist.Items.Skip(1)), pair =>
            Assert.True(pair.Second.StartOffsetSeconds - pair.First.StartOffsetSeconds < 3600));
    }

    [Fact]
    public void Generate_SmallCatalogAlwaysMakesProgressAndReportsRelaxations()
    {
        PlaylistDocument playlist = Generate(
            [Asset("only-vlog", AssetTypes.Vlog, 60)],
            Policy(TimeSpan.FromMinutes(4), (AssetTypes.Vlog, 1)),
            seed: 1).Playlist;

        Assert.Equal(4, playlist.Items.Count);
        Assert.True(playlist.Summary.ExactAssetCooldownRelaxations > 0);
        Assert.True(playlist.Summary.ConsecutiveVlogViolations > 0);
        Assert.True(playlist.Summary.EmergencyVlogRunViolations > 0);
        Assert.True(playlist.Summary.ContentGroupCooldownRelaxations == 0);
    }

    [Fact]
    public void Policy_HotRotationWeightsUseRotationStartDateAndNullIsNormal()
    {
        var policy = new PlaylistPolicy();

        Assert.Equal(2.5, policy.GetRotationWeight(NowDate.AddDays(-7), NowDate));
        Assert.Equal(2.0, policy.GetRotationWeight(NowDate.AddDays(-8), NowDate));
        Assert.Equal(1.5, policy.GetRotationWeight(NowDate.AddDays(-45), NowDate));
        Assert.Equal(1.0, policy.GetRotationWeight(NowDate.AddDays(-46), NowDate));
        Assert.Equal(1.0, policy.GetRotationWeight(null, NowDate));
    }

    [Fact]
    public void Generate_HotRotationStronglyFavorsRecentAssetWithFixedSeed()
    {
        PlaylistPolicy policy = Policy(TimeSpan.FromMinutes(20), (AssetTypes.MusicVideo, 1)) with
        {
            ExactAssetCooldown = TimeSpan.Zero,
            ContentGroupCooldown = TimeSpan.Zero,
            ContentGroupMinimumCooldown = TimeSpan.Zero,
            ContentGroupMusicFirstRescueCooldown = TimeSpan.Zero,
        };
        PlaylistDocument playlist = Generate(
            [
                Asset("hot", AssetTypes.MusicVideo, 60, "hot", NowDate.AddDays(-1)),
                Asset("normal-a", AssetTypes.MusicVideo, 60, "a"),
                Asset("normal-b", AssetTypes.MusicVideo, 60, "b"),
            ],
            policy,
            seed: 17).Playlist;

        int hotPlays = playlist.Items.Count(item => item.AssetId == "hot");
        Assert.True(hotPlays > playlist.Items.Count(item => item.AssetId == "normal-a"));
        Assert.True(hotPlays > playlist.Items.Count(item => item.AssetId == "normal-b"));
        Assert.Equal(0, playlist.Summary.NewReleasePreferenceBypasses);
    }

    [Fact]
    public void Generate_HistoryBlocksImmediateCrossBoundarySongRepeat()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("cash-music", "cash", AssetTypes.MusicVideo, Now.AddMinutes(-3)));
        PlaylistDocument playlist = Generate(
            [
                Asset("cash-lyric", AssetTypes.LyricVideo, 60, "cash"),
                Asset("other", AssetTypes.MusicVideo, 60, "other"),
            ],
            Policy(
                TimeSpan.FromMinutes(1),
                (AssetTypes.MusicVideo, 0.5),
                (AssetTypes.LyricVideo, 0.5)),
            seed: 1,
            history: history).Playlist;

        Assert.Equal("other", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(0, playlist.Summary.ContentGroupCooldownRelaxations);
    }

    [Fact]
    public void Generate_CadenceCategoriesAppearWhenConfiguredAndMayBeAbsent()
    {
        PlaylistPolicy cadencePolicy = Policy(TimeSpan.FromMinutes(15), (AssetTypes.MusicVideo, 1)) with
        {
            ExactAssetCooldown = TimeSpan.Zero,
            ContentGroupCooldown = TimeSpan.Zero,
            ContentGroupMinimumCooldown = TimeSpan.Zero,
            ContentGroupMusicFirstRescueCooldown = TimeSpan.Zero,
            BumperCadence = new ProgramCountCadence(2, 3),
            PromoCadence = new TimeCadence(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3)),
            InterstitialCadence = new TimeCadence(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3)),
        };
        PlaylistDocument withCadence = Generate(
            [
                Asset("music", AssetTypes.MusicVideo, 60, "music"),
                Asset("bumper", AssetTypes.Bumper, 15),
                Asset("promo", AssetTypes.Promo, 30),
                Asset("interstitial", AssetTypes.Interstitial, 30),
            ],
            cadencePolicy,
            seed: 4).Playlist;
        PlaylistDocument withoutCadenceAssets = Generate(
            [Asset("music", AssetTypes.MusicVideo, 60, "music")],
            cadencePolicy,
            seed: 4).Playlist;

        Assert.Contains(withCadence.Items, item => item.Type == AssetTypes.Bumper);
        Assert.Contains(withCadence.Items, item => item.Type == AssetTypes.Promo);
        Assert.Contains(withCadence.Items, item => item.Type == AssetTypes.Interstitial);
        Assert.NotEmpty(withoutCadenceAssets.Items);
        Assert.All(withoutCadenceAssets.Items, item => Assert.Equal(AssetTypes.MusicVideo, item.Type));
        Assert.Equal(0, withoutCadenceAssets.Summary.BumperCadenceMisses);
        Assert.Equal(0, withoutCadenceAssets.Summary.PromoCadenceMisses);
        Assert.Equal(0, withoutCadenceAssets.Summary.InterstitialCadenceMisses);
    }

    [Fact]
    public void Generate_OnePromoRespectsThirtyMinuteMinimumUnderFallbackPressure()
    {
        PlaylistPolicy policy = Policy(TimeSpan.FromHours(6), (AssetTypes.MusicVideo, 1)) with
        {
            PromoCadence = new TimeCadence(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(45)),
        };
        PlaylistAsset[] assets =
        [
            .. Enumerable.Range(1, 6)
                .Select(index => Asset($"music-{index}", AssetTypes.MusicVideo, 300, $"song-{index}")),
            Asset("only-promo", AssetTypes.Promo, 15.433),
        ];

        PlaylistDocument playlist = Generate(assets, policy, seed: 20260927).Playlist;
        PlaylistItem[] promos = playlist.Items
            .Where(item => item.Type == AssetTypes.Promo)
            .OrderBy(item => item.StartOffsetSeconds)
            .ToArray();

        Assert.NotEmpty(promos);
        Assert.True(promos[0].StartOffsetSeconds >= policy.PromoCadence.MinimumInterval.TotalSeconds);
        Assert.True(promos[0].StartOffsetSeconds
            <= policy.PromoCadence.MaximumInterval.TotalSeconds + 300);
        Assert.All(promos.Zip(promos.Skip(1)), pair =>
            Assert.True(pair.Second.StartOffsetSeconds - pair.First.StartOffsetSeconds
                >= policy.PromoCadence.MinimumInterval.TotalSeconds));
        Assert.All(promos.Zip(promos.Skip(1)), pair =>
            Assert.True(pair.Second.StartOffsetSeconds - pair.First.StartOffsetSeconds
                <= policy.PromoCadence.MaximumInterval.TotalSeconds + 300));
        Assert.DoesNotContain(playlist.Items.Zip(playlist.Items.Skip(1)), pair =>
            pair.First.Type == AssetTypes.Promo && pair.Second.Type == AssetTypes.Promo);
        Assert.True(promos.Length <= Math.Ceiling(
            policy.TargetDuration.TotalSeconds / policy.PromoCadence.MinimumInterval.TotalSeconds));
        Assert.Equal(promos.Length, playlist.Summary.PromoInsertions);
    }

    [Fact]
    public void Generate_DuePromoMayRelaxExactCooldownWithoutRelaxingMinimumSpacing()
    {
        PlaylistPolicy policy = Policy(TimeSpan.FromMinutes(95), (AssetTypes.MusicVideo, 1)) with
        {
            PromoCadence = new TimeCadence(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(45)),
        };

        PlaylistDocument playlist = Generate(
            [
                Asset("music", AssetTypes.MusicVideo, 300, "music"),
                Asset("promo", AssetTypes.Promo, 15),
            ],
            policy,
            seed: 7).Playlist;
        PlaylistItem[] promos = playlist.Items.Where(item => item.Type == AssetTypes.Promo).ToArray();

        Assert.True(promos.Length >= 2);
        Assert.Contains(promos.Zip(promos.Skip(1)), pair =>
            pair.Second.StartOffsetSeconds - pair.First.StartOffsetSeconds
                < policy.ExactAssetCooldown.TotalSeconds);
        Assert.All(promos.Zip(promos.Skip(1)), pair =>
            Assert.True(pair.Second.StartOffsetSeconds - pair.First.StartOffsetSeconds
                >= policy.PromoCadence.MinimumInterval.TotalSeconds));
        Assert.True(playlist.Summary.ExactAssetCooldownRelaxations > 0);
    }

    [Fact]
    public void Generate_PromoIsPreferredInsideCadenceWindow()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("prior-promo", null, AssetTypes.Promo, Now.AddMinutes(-35)));
        PlaylistPolicy policy = Policy(TimeSpan.FromMinutes(1), (AssetTypes.MusicVideo, 1)) with
        {
            PromoCadence = new TimeCadence(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(45)),
        };

        PlaylistDocument playlist = Generate(
            [
                Asset("music", AssetTypes.MusicVideo, 60, "music"),
                Asset("promo", AssetTypes.Promo, 15),
            ],
            policy,
            seed: 1,
            history: history).Playlist;

        Assert.Equal(AssetTypes.Promo, playlist.Items[0].Type);
    }

    [Fact]
    public void Generate_OverduePromoOverridesItsOrdinaryExactAssetCooldown()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("promo", null, AssetTypes.Promo, Now.AddMinutes(-46)));
        PlaylistPolicy policy = Policy(TimeSpan.FromSeconds(15), (AssetTypes.MusicVideo, 1)) with
        {
            PromoCadence = new TimeCadence(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(45)),
        };

        PlaylistDocument playlist = Generate(
            [
                Asset("music", AssetTypes.MusicVideo, 60, "music"),
                Asset("promo", AssetTypes.Promo, 15),
            ],
            policy,
            seed: 3,
            history: history).Playlist;

        Assert.Equal("promo", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(1, playlist.Summary.ExactAssetCooldownRelaxations);
        Assert.Equal(1, playlist.Summary.PromoInsertions);
    }

    [Fact]
    public void Generate_MultiplePromosPreferAnAssetOutsideExactCooldown()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("promo-a", null, AssetTypes.Promo, Now.AddMinutes(-46)));
        PlaylistPolicy policy = Policy(TimeSpan.FromSeconds(15), (AssetTypes.MusicVideo, 1)) with
        {
            PromoCadence = new TimeCadence(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(45)),
        };

        PlaylistDocument playlist = Generate(
            [
                Asset("music", AssetTypes.MusicVideo, 60, "music"),
                Asset("promo-a", AssetTypes.Promo, 15),
                Asset("promo-b", AssetTypes.Promo, 15),
            ],
            policy,
            seed: 11,
            history: history).Playlist;

        Assert.Equal("promo-b", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(0, playlist.Summary.ExactAssetCooldownRelaxations);
    }

    [Fact]
    public void Generate_MultipleBlockedPromosRotateToLeastRecentlyPlayedAssetWhenOverdue()
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("promo-b", null, AssetTypes.Promo, Now.AddMinutes(-60)),
            new PlaylistHistoryEntry("promo-a", null, AssetTypes.Promo, Now.AddMinutes(-46)));
        PlaylistPolicy policy = Policy(TimeSpan.FromSeconds(15), (AssetTypes.MusicVideo, 1)) with
        {
            PromoCadence = new TimeCadence(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(45)),
        };

        PlaylistDocument playlist = Generate(
            [
                Asset("music", AssetTypes.MusicVideo, 60, "music"),
                Asset("promo-a", AssetTypes.Promo, 15),
                Asset("promo-b", AssetTypes.Promo, 15),
            ],
            policy,
            seed: 19,
            history: history).Playlist;

        Assert.Equal("promo-b", Assert.Single(playlist.Items).AssetId);
        Assert.Equal(1, playlist.Summary.ExactAssetCooldownRelaxations);
    }

    [Fact]
    public void Generate_InterstitialCannotBypassTwentyMinuteMinimum()
    {
        PlaylistPolicy policy = Policy(TimeSpan.FromMinutes(70), (AssetTypes.MusicVideo, 1)) with
        {
            InterstitialCadence = new TimeCadence(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30)),
        };

        PlaylistDocument playlist = Generate(
            [
                Asset("music", AssetTypes.MusicVideo, 60, "music"),
                Asset("interstitial", AssetTypes.Interstitial, 15),
            ],
            policy,
            seed: 5).Playlist;
        PlaylistItem[] interstitials = playlist.Items
            .Where(item => item.Type == AssetTypes.Interstitial)
            .ToArray();

        Assert.NotEmpty(interstitials);
        Assert.True(interstitials[0].StartOffsetSeconds
            >= policy.InterstitialCadence.MinimumInterval.TotalSeconds);
        Assert.All(interstitials.Zip(interstitials.Skip(1)), pair =>
            Assert.True(pair.Second.StartOffsetSeconds - pair.First.StartOffsetSeconds
                >= policy.InterstitialCadence.MinimumInterval.TotalSeconds));
        Assert.Equal(interstitials.Length, playlist.Summary.InterstitialInsertions);
    }

    [Fact]
    public void Generate_BumperRequiresFourNormalProgramsBeforeInsertion()
    {
        PlaylistPolicy policy = Policy(TimeSpan.FromMinutes(15), (AssetTypes.MusicVideo, 1)) with
        {
            BumperCadence = new ProgramCountCadence(4, 5),
        };

        PlaylistDocument playlist = Generate(
            [
                Asset("music", AssetTypes.MusicVideo, 60, "music"),
                Asset("bumper", AssetTypes.Bumper, 15),
            ],
            policy,
            seed: 9).Playlist;
        int normalPrograms = 0;
        int bumpers = 0;
        foreach (PlaylistItem item in playlist.Items)
        {
            if (item.Type == AssetTypes.Bumper)
            {
                Assert.True(normalPrograms >= policy.BumperCadence.MinimumPrograms);
                normalPrograms = 0;
                bumpers++;
            }
            else if (item.Type is not (AssetTypes.Promo or AssetTypes.Interstitial))
            {
                normalPrograms++;
            }
        }

        Assert.True(bumpers > 0);
        Assert.Equal(bumpers, playlist.Summary.BumperInsertions);
    }

    [Fact]
    public void Generate_SixHoursWithOneNormalAssetStillMakesProgressWithoutCadenceAssets()
    {
        PlaylistDocument playlist = Generate(
            [Asset("only-music", AssetTypes.MusicVideo, 300, "only-song")],
            new PlaylistPolicy(),
            seed: 12).Playlist;

        Assert.True(playlist.ActualDurationSeconds >= TimeSpan.FromHours(6).TotalSeconds);
        Assert.All(playlist.Items, item => Assert.Equal(AssetTypes.MusicVideo, item.Type));
        Assert.Equal(0, playlist.Summary.BumperInsertions);
        Assert.Equal(0, playlist.Summary.PromoInsertions);
        Assert.Equal(0, playlist.Summary.InterstitialInsertions);
    }

    [Fact]
    public void Generate_PolicyDisabledAssetsAreReportedAsExcluded()
    {
        PlaylistDocument playlist = Generate(
            [
                Asset("music", AssetTypes.MusicVideo, 60, "music"),
                Asset("special", AssetTypes.Special, 60),
            ],
            Policy(TimeSpan.FromMinutes(1), (AssetTypes.MusicVideo, 1)),
            seed: 1).Playlist;

        PlaylistExclusion exclusion = Assert.Single(playlist.ExcludedAssets);
        Assert.Equal("special.mp4", exclusion.RelativePath);
        Assert.Contains("no airtime target", exclusion.Reasons[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, playlist.Summary.EligibleAssets);
        Assert.Equal(1, playlist.Summary.ExcludedAssets);
    }

    private static DateOnly NowDate => DateOnly.FromDateTime(Now.UtcDateTime);

    private static PlaylistGenerationResult Generate(
        IReadOnlyCollection<PlaylistAsset> assets,
        PlaylistPolicy policy,
        int seed,
        PlaylistHistoryDocument? history = null) =>
        new PlaylistGenerator().Generate(assets, [], history ?? PlaylistHistoryDocument.Empty, policy, seed, Now);

    private static PlaylistPolicy Policy(TimeSpan duration, params (string Type, double Weight)[] targets) => new()
    {
        TargetDuration = duration,
        CategoryAirtimeTargets = targets.ToDictionary(item => item.Type, item => item.Weight, StringComparer.Ordinal),
        BumperCadence = null,
        PromoCadence = null,
        InterstitialCadence = null,
        // Legacy spacing tests exercise full-song rules explicitly. Dedicated
        // short-presentation tests use the production threshold.
        ShortSongPresentationMaximumDuration = TimeSpan.Zero,
    };

    private static PlaylistPolicy ShortPresentationPolicy(
        TimeSpan duration,
        params (string Type, double Weight)[] targets) => Policy(duration, targets) with
        {
            ShortSongPresentationMaximumDuration = TimeSpan.FromSeconds(60),
        };

    private static PlaylistAsset Asset(
        string id,
        string type,
        double durationSeconds,
        string? groupId = null,
        DateOnly? rotationStartDate = null) => new(
            id,
            groupId,
            id,
            "Nzyte",
            type,
            type == AssetTypes.ShortForm ? "pov" : null,
            $"{id}.mp4",
            durationSeconds,
            rotationStartDate);

    private static PlaylistHistoryDocument History(
        DateTimeOffset scheduleEnd,
        params PlaylistHistoryEntry[] plays) => new()
        {
            ScheduleEndUtc = scheduleEnd,
            Plays = plays,
        };
}
