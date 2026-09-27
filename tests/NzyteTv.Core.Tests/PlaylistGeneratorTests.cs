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
            Assert.True(pair.Second.StartOffsetSeconds - pair.First.StartOffsetSeconds >= 5400));
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
    [InlineData(AssetTypes.Performance)]
    [InlineData(AssetTypes.ShortForm)]
    public void Generate_AlternateVisualCannotBypassSharedContentGroupClock(string alternateType)
    {
        PlaylistHistoryDocument history = History(
            Now,
            new PlaylistHistoryEntry("cash-music", "cash", AssetTypes.MusicVideo, Now.AddMinutes(-30)));

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

        Assert.Equal("other-song", Assert.Single(playlist.Items).AssetId);
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
