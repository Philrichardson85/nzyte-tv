using NzyteTv.Core;

namespace NzyteTv.Core.Tests;

public sealed class ProgrammingPlaylistGeneratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EligibleAssetNeedsNoProgrammingOverride()
    {
        ProgrammingConfiguration configuration = Config(ProgrammingLaneNames.FullMusic) with
        {
            Repetition = new ProgrammingRepetitionPolicy
            {
                ExactAssetCooldownMinutes = 120,
                SameContentGroupLookback = 2,
            },
        };
        PlaylistGenerationResult result = Generate(
            [Song("ordinary", "ordinary-song")],
            configuration,
            TimeSpan.FromMinutes(1));

        Assert.Single(result.Playlist.Items);
        Assert.Equal("ordinary", result.Playlist.Items[0].AssetId);
        Assert.True(result.Playlist.Summary.ProgrammingPolicyActive);
    }

    [Fact]
    public void DoNotAirExcludesAssetAndClearingItRestoresDefaultEligibility()
    {
        PlaylistAsset excluded = Song("excluded", "song-a");
        PlaylistAsset available = Song("available", "song-b");
        ProgrammingConfiguration blocked = Config(ProgrammingLaneNames.FullMusic) with
        {
            AssetOverrides = new Dictionary<string, AssetEditorialOverride>
            {
                [excluded.AssetId] = new() { DoNotAir = true },
            },
        };

        PlaylistGenerationResult blockedResult = Generate(
            [excluded, available],
            blocked,
            TimeSpan.FromMinutes(4));
        PlaylistGenerationResult clearedResult = Generate(
            [excluded, available],
            Config(ProgrammingLaneNames.FullMusic),
            TimeSpan.FromMinutes(4));

        Assert.DoesNotContain(blockedResult.Playlist.Items, item => item.AssetId == excluded.AssetId);
        Assert.Contains(
            blockedResult.Playlist.ExcludedAssets,
            item => item.RelativePath == excluded.RelativePath
                && item.Reasons.Any(reason => reason.Contains("Do Not Air", StringComparison.Ordinal)));
        Assert.Contains(clearedResult.Playlist.Items, item => item.AssetId == excluded.AssetId);
    }

    [Fact]
    public void AssetWeightChangesPresentationPreferenceWithinChosenSongFamily()
    {
        PlaylistAsset favored = Song("favored", "same-song", seconds: 30);
        PlaylistAsset ordinary = Song("ordinary", "same-song", seconds: 30);
        ProgrammingConfiguration configuration = Config(ProgrammingLaneNames.FullMusic) with
        {
            AssetOverrides = new Dictionary<string, AssetEditorialOverride>
            {
                [favored.AssetId] = new() { WeightMultiplier = 10 },
                [ordinary.AssetId] = new() { WeightMultiplier = 0.1 },
            },
        };

        int favoredSelections = Enumerable.Range(1, 20)
            .Count(seed => Generate(
                [favored, ordinary],
                configuration,
                TimeSpan.FromSeconds(1),
                seed).Playlist.Items[0].AssetId == favored.AssetId);

        Assert.InRange(favoredSelections, 18, 20);
    }

    [Fact]
    public void PresentationCountDoesNotChangeWhichSongFamilyWinsForTheSameSeed()
    {
        PlaylistAsset[] onePresentationPerGroup =
        [
            Song("a-one", "song-a", 30),
            Song("b-one", "song-b", 30),
        ];
        PlaylistAsset[] manyPresentationsForA =
        [
            .. Enumerable.Range(1, 12).Select(index => Song($"a-{index}", "song-a", 30)),
            Song("b-one", "song-b", 30),
        ];
        ProgrammingConfiguration normal = Config(ProgrammingLaneNames.FullMusic);
        ProgrammingConfiguration campaign = normal with
        {
            ActiveCampaign = new ActiveCampaign
            {
                Enabled = true,
                ContentGroupId = "song-a",
                WeightMultiplier = 2,
            },
        };

        foreach (ProgrammingConfiguration configuration in new[] { normal, campaign })
        {
            foreach (int seed in Enumerable.Range(1, 50))
            {
                string? one = Generate(
                    onePresentationPerGroup,
                    configuration,
                    TimeSpan.FromSeconds(1),
                    seed).Playlist.Items[0].ContentGroupId;
                string? many = Generate(
                    manyPresentationsForA,
                    configuration,
                    TimeSpan.FromSeconds(1),
                    seed).Playlist.Items[0].ContentGroupId;
                Assert.Equal(one, many);
            }
        }
    }

    [Fact]
    public void ExactAssetCooldownStronglyProtectsOnePresentationWhenAlternativesExist()
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("a", "song-a", 30),
                Song("b", "song-b", 30),
                Song("c", "song-c", 30),
            ],
            Config(ProgrammingLaneNames.FullMusic),
            TimeSpan.FromMinutes(1.5));
        string[] ids = result.Playlist.Items.Select(item => item.AssetId).ToArray();

        Assert.Equal(3, ids.Length);
        Assert.Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(0, result.Playlist.Summary.ExactAssetCooldownRelaxations);
    }

    [Fact]
    public void CampaignDoesNotOverrideDoNotAir()
    {
        PlaylistAsset campaignAsset = Song("campaign-video", "campaign-song");
        PlaylistAsset normal = Song("normal-video", "normal-song");
        ProgrammingConfiguration configuration = Config(ProgrammingLaneNames.FullMusic) with
        {
            ActiveCampaign = new ActiveCampaign
            {
                Enabled = true,
                ContentGroupId = campaignAsset.ContentGroupId,
                WeightMultiplier = 10,
            },
            AssetOverrides = new Dictionary<string, AssetEditorialOverride>
            {
                [campaignAsset.AssetId] = new() { DoNotAir = true },
            },
        };

        PlaylistGenerationResult result = Generate(
            [campaignAsset, normal],
            configuration,
            TimeSpan.FromMinutes(4));

        Assert.DoesNotContain(result.Playlist.Items, item => item.AssetId == campaignAsset.AssetId);
    }

    [Fact]
    public void CampaignCannotOverrideHardSameSongAdjacencyWhenAlternativeExists()
    {
        ProgrammingConfiguration configuration = Config(ProgrammingLaneNames.FullMusic) with
        {
            ActiveCampaign = new ActiveCampaign
            {
                Enabled = true,
                ContentGroupId = "campaign",
                WeightMultiplier = 10,
            },
        };
        PlaylistAsset[] assets =
        [
            Song("campaign-one", "campaign"),
            Song("campaign-two", "campaign"),
            Song("alternative", "other"),
        ];

        PlaylistGenerationResult result = Generate(assets, configuration, TimeSpan.FromMinutes(8));

        AssertNoAdjacentSubstantialSong(result.Playlist.Items);
        Assert.Equal(0, result.Playlist.Summary.ContentGroupAdjacencyViolations);
    }

    [Fact]
    public void BumperBetweenPresentationsDoesNotSeparateSameSongFamily()
    {
        ProgrammingConfiguration configuration = Config(
            ProgrammingLaneNames.FullMusic,
            bumperMinimum: 1,
            bumperMaximum: 1);
        PlaylistAsset[] assets =
        [
            Song("song-a-one", "song-a"),
            Song("song-a-two", "song-a"),
            Song("song-b", "song-b"),
            Bumper("bumper-one"),
            Bumper("bumper-two"),
        ];

        PlaylistGenerationResult result = Generate(assets, configuration, TimeSpan.FromMinutes(8));

        Assert.Contains(result.Playlist.Items, item => item.Type == AssetTypes.Bumper);
        AssertNoAdjacentSubstantialSong(result.Playlist.Items);
    }

    [Fact]
    public void PromoBetweenPresentationsDoesNotSeparateSameSongFamily()
    {
        ProgrammingConfiguration configuration = Config(
            ProgrammingLaneNames.FullMusic,
            bumperMinimum: 100,
            bumperMaximum: 100,
            promoMinimumMinutes: 1,
            promoMaximumMinutes: 1);
        PlaylistAsset[] assets =
        [
            Song("song-a-one", "song-a"),
            Song("song-a-two", "song-a"),
            Song("song-b", "song-b"),
            Promo("promo-one"),
        ];

        PlaylistGenerationResult result = Generate(assets, configuration, TimeSpan.FromMinutes(8));

        Assert.Contains(result.Playlist.Items, item => item.Type == AssetTypes.Promo);
        AssertNoAdjacentSubstantialSong(result.Playlist.Items);
    }

    [Fact]
    public void SameSongReturnsAfterConfiguredTwoPieceLookback()
    {
        PlaylistAsset[] assets =
        [
            Song("a", "song-a"),
            Song("b", "song-b"),
            Song("c", "song-c"),
        ];

        PlaylistGenerationResult result = Generate(
            assets,
            Config(ProgrammingLaneNames.FullMusic, lookback: 2),
            TimeSpan.FromMinutes(10));
        string[] groups = result.Playlist.Items
            .Where(item => ProgrammingContentClassifier.IsSubstantial(item.Type))
            .Select(item => item.ContentGroupId!)
            .ToArray();

        Assert.True(groups.Length >= 6);
        for (int index = 0; index < groups.Length; index++)
        {
            Assert.DoesNotContain(groups[index], groups.Skip(Math.Max(0, index - 2)).Take(Math.Min(2, index)));
        }
    }

    [Fact]
    public void LimitedInventoryRelaxesClusterAndEventuallyAdjacencyRatherThanDeadlocking()
    {
        PlaylistGenerationResult result = Generate(
            [Song("only-one", "only-song"), Song("only-two", "only-song")],
            Config(ProgrammingLaneNames.FullMusic),
            TimeSpan.FromMinutes(5));

        Assert.True(result.Playlist.Items.Count >= 3);
        Assert.True(result.Playlist.Summary.ContentGroupClusterRelaxations > 0);
        Assert.True(result.Playlist.Summary.ContentGroupAdjacencyViolations > 0);
    }

    [Fact]
    public void BumpersLandWithinConfiguredWindowRotateAndNeverRunBackToBack()
    {
        PlaylistAsset[] assets =
        [
            .. Enumerable.Range(1, 8).Select(index => Song($"song-{index}", $"group-{index}", 60)),
            .. Enumerable.Range(1, 5).Select(index => Bumper($"bumper-{index}")),
        ];
        PlaylistGenerationResult result = Generate(
            assets,
            Config(ProgrammingLaneNames.FullMusic, bumperMinimum: 3, bumperMaximum: 5),
            TimeSpan.FromMinutes(25),
            seed: 222);
        PlaylistItem[] items = result.Playlist.Items.ToArray();
        int substantialSinceBumper = 0;
        var bumperGaps = new List<int>();
        foreach (PlaylistItem item in items)
        {
            if (item.Type == AssetTypes.Bumper)
            {
                bumperGaps.Add(substantialSinceBumper);
                substantialSinceBumper = 0;
            }
            else if (ProgrammingContentClassifier.IsSubstantial(item.Type))
            {
                substantialSinceBumper++;
            }
        }

        Assert.NotEmpty(bumperGaps);
        Assert.All(bumperGaps, gap => Assert.InRange(gap, 3, 5));
        Assert.DoesNotContain(items.Zip(items.Skip(1)), pair =>
            pair.First.Type == AssetTypes.Bumper && pair.Second.Type == AssetTypes.Bumper);
        string[] firstFive = items.Where(item => item.Type == AssetTypes.Bumper)
            .Take(5)
            .Select(item => item.AssetId)
            .ToArray();
        Assert.Equal(firstFive.Length, firstFive.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PromoAndAdvertisementShareCadenceAndDoNotFormNormalPods()
    {
        PlaylistAsset[] assets =
        [
            .. Enumerable.Range(1, 5).Select(index => Song($"song-{index}", $"group-{index}", 60)),
            Promo("promo"),
            Promo("advertisement", AssetTypes.Advertisement),
        ];
        PlaylistGenerationResult result = Generate(
            assets,
            Config(
                ProgrammingLaneNames.FullMusic,
                bumperMinimum: 100,
                bumperMaximum: 100,
                promoMinimumMinutes: 2,
                promoMaximumMinutes: 3),
            TimeSpan.FromMinutes(12));
        PlaylistItem[] commercials = result.Playlist.Items
            .Where(item => ProgrammingContentClassifier.IsCommercialInsertion(item.Type))
            .ToArray();

        Assert.True(commercials.Length >= 2);
        Assert.All(commercials.Zip(commercials.Skip(1)), pair =>
            Assert.True(pair.Second.StartOffsetSeconds - pair.First.StartOffsetSeconds >= 120));
        Assert.DoesNotContain(result.Playlist.Items.Zip(result.Playlist.Items.Skip(1)), pair =>
            ProgrammingContentClassifier.IsCommercialInsertion(pair.First.Type)
            && ProgrammingContentClassifier.IsCommercialInsertion(pair.Second.Type));
    }

    [Fact]
    public void MissingRequestedLaneFallsBackSafely()
    {
        PlaylistGenerationResult result = Generate(
            [Song("music", "music")],
            Config(ProgrammingLaneNames.Personality),
            TimeSpan.FromMinutes(3));

        Assert.NotEmpty(result.Playlist.Items);
        Assert.True(result.Playlist.Summary.ProgrammingPatternFallbacks > 0);
    }

    [Fact]
    public void ShortFormParticipatesInOrdinaryFlowWithoutADedicatedBlock()
    {
        PlaylistAsset[] assets =
        [
            Song("full-a", "group-a", 120),
            Song("short-b", "group-b", 30, AssetTypes.ShortForm),
            Song("full-c", "group-c", 120),
            Vlog("vlog", 60),
        ];
        PlaylistGenerationResult result = Generate(
            assets,
            ProgrammingConfiguration.CreateDefault(),
            TimeSpan.FromMinutes(12),
            seed: 812);
        PlaylistItem[] items = result.Playlist.Items.ToArray();

        Assert.Contains(items, item => item.Type == AssetTypes.ShortForm);
        Assert.Contains(items.Zip(items.Skip(1)), pair =>
            (pair.First.Type == AssetTypes.ShortForm) != (pair.Second.Type == AssetTypes.ShortForm));
    }

    [Fact]
    public void VlogsDoNotClusterConsecutivelyWhenSubstantialAlternativeExists()
    {
        PlaylistAsset[] assets =
        [
            Song("music-a", "group-a", 60),
            Song("music-b", "group-b", 60),
            Vlog("vlog-a", 60),
            Vlog("vlog-b", 60),
        ];
        PlaylistGenerationResult result = Generate(
            assets,
            Config(ProgrammingLaneNames.Personality),
            TimeSpan.FromMinutes(8),
            categoryTargets: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [AssetTypes.MusicVideo] = 0.5,
                [AssetTypes.Vlog] = 0.5,
            });
        PlaylistItem[] substantial = result.Playlist.Items
            .Where(item => ProgrammingContentClassifier.IsSubstantial(item.Type))
            .ToArray();

        Assert.DoesNotContain(substantial.Zip(substantial.Skip(1)), pair =>
            pair.First.Type == AssetTypes.Vlog && pair.Second.Type == AssetTypes.Vlog);
    }

    [Fact]
    public void ThreeConsecutiveShortPiecesAreAllowed()
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("short-three", "short-three", 30, AssetTypes.AnimatedVisual),
                Song("full", "full", 120),
            ],
            Config(ProgrammingLaneNames.ShortPerformance),
            TimeSpan.FromSeconds(1),
            history: History(
                (AssetTypes.ShortForm, 30),
                (AssetTypes.Performance, 30)));

        Assert.Equal("short-three", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.ShortRunRelaxations);
        Assert.Equal(1, result.Playlist.Summary.MaximumObservedConsecutiveShortPieces);
    }

    [Fact]
    public void FourthShortPieceIsRejectedWhenAFullAlternativeExists()
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("short-four", "short-four", 30, AssetTypes.AnimatedVisual),
                Song("full", "full", 120),
            ],
            Config(ProgrammingLaneNames.ShortPerformance),
            TimeSpan.FromSeconds(1),
            history: ThreeShortPieceHistory());

        Assert.Equal("full", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.ShortRunRelaxations);
        Assert.Equal(0, result.Playlist.Summary.MaximumObservedConsecutiveShortPieces);
        Assert.Equal(1, result.Playlist.Summary.ProgrammingPatternFallbacks);
    }

    [Fact]
    public void FullBreakerOutsideExactAssetCooldownIsSelectedNormally()
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("preferred-short", "preferred-short", 30, AssetTypes.AnimatedVisual),
                Song("full-breaker", "full-breaker", 120),
            ],
            Config(
                ProgrammingLaneNames.ShortPerformance,
                exactAssetCooldownMinutes: 120),
            TimeSpan.FromSeconds(1),
            history: ShortCapHistoryWithEarlierPlay(
                "full-breaker",
                "full-breaker",
                minutesAgo: 121));

        Assert.Equal("full-breaker", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.ExactAssetCooldownRelaxations);
        Assert.Equal(0, result.Playlist.Summary.ShortRunRelaxations);
    }

    [Fact]
    public void FullBreakerInsideExactAssetCooldownIsPreferredToFourthShort()
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("preferred-short", "preferred-short", 30, AssetTypes.AnimatedVisual),
                Song("full-breaker", "full-breaker", 120),
            ],
            Config(
                ProgrammingLaneNames.ShortPerformance,
                exactAssetCooldownMinutes: 120),
            TimeSpan.FromSeconds(1),
            history: ShortCapHistoryWithEarlierPlay(
                "full-breaker",
                "full-breaker",
                minutesAgo: 105));

        Assert.Equal("full-breaker", result.Playlist.Items[0].AssetId);
        Assert.Equal(1, result.Playlist.Summary.ExactAssetCooldownRelaxations);
        Assert.Equal(0, result.Playlist.Summary.ShortRunRelaxations);
    }

    [Fact]
    public void FullBreakerMayRelaxSoftSongClusterPreferenceBeforeFourthShort()
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("preferred-short", "preferred-short", 30, AssetTypes.AnimatedVisual),
                Song("full-breaker", "breaker-song", 120),
            ],
            Config(ProgrammingLaneNames.ShortPerformance, lookback: 2),
            TimeSpan.FromSeconds(1),
            history: HistoryWithOffsets(
                ("history-short-one", "history-one", AssetTypes.ShortForm, 30, 3),
                ("breaker-short-presentation", "breaker-song", AssetTypes.AnimatedVisual, 30, 2),
                ("history-short-three", "history-three", AssetTypes.Performance, 30, 1)));

        Assert.Equal("full-breaker", result.Playlist.Items[0].AssetId);
        Assert.Equal(1, result.Playlist.Summary.ContentGroupClusterRelaxations);
        Assert.Equal(0, result.Playlist.Summary.ExactAssetCooldownRelaxations);
        Assert.Equal(0, result.Playlist.Summary.ShortRunRelaxations);
    }

    [Fact]
    public void CampaignWeightCannotBeatFullBreakerInsideExactAssetCooldown()
    {
        ProgrammingConfiguration configuration = Config(
            ProgrammingLaneNames.ShortPerformance,
            exactAssetCooldownMinutes: 120) with
        {
            ActiveCampaign = new ActiveCampaign
            {
                Enabled = true,
                ContentGroupId = "campaign-short",
                WeightMultiplier = 10,
            },
        };
        PlaylistGenerationResult result = Generate(
            [
                Song("campaign-short", "campaign-short", 30, AssetTypes.AnimatedVisual),
                Song("full-breaker", "full-breaker", 120),
            ],
            configuration,
            TimeSpan.FromSeconds(1),
            history: ShortCapHistoryWithEarlierPlay(
                "full-breaker",
                "full-breaker",
                minutesAgo: 105));

        Assert.Equal("full-breaker", result.Playlist.Items[0].AssetId);
        Assert.Equal(1, result.Playlist.Summary.ExactAssetCooldownRelaxations);
        Assert.Equal(0, result.Playlist.Summary.ShortRunRelaxations);
    }

    [Fact]
    public void PresentationWeightCannotBeatFullBreakerInsideExactAssetCooldown()
    {
        PlaylistAsset favoredShort = Song(
            "favored-short",
            "favored-short",
            30,
            AssetTypes.AnimatedVisual);
        ProgrammingConfiguration configuration = Config(
            ProgrammingLaneNames.ShortPerformance,
            exactAssetCooldownMinutes: 120) with
        {
            AssetOverrides = new Dictionary<string, AssetEditorialOverride>
            {
                [favoredShort.AssetId] = new() { WeightMultiplier = 10 },
            },
        };
        PlaylistGenerationResult result = Generate(
            [favoredShort, Song("full-breaker", "full-breaker", 120)],
            configuration,
            TimeSpan.FromSeconds(1),
            history: ShortCapHistoryWithEarlierPlay(
                "full-breaker",
                "full-breaker",
                minutesAgo: 105));

        Assert.Equal("full-breaker", result.Playlist.Items[0].AssetId);
        Assert.Equal(1, result.Playlist.Summary.ExactAssetCooldownRelaxations);
        Assert.Equal(0, result.Playlist.Summary.ShortRunRelaxations);
    }

    [Fact]
    public void LaneAndCategoryPreferenceCannotBeatFullBreakerInsideExactAssetCooldown()
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("preferred-short", "preferred-short", 30, AssetTypes.AnimatedVisual),
                Song("full-breaker", "full-breaker", 120),
            ],
            Config(
                ProgrammingLaneNames.ShortPerformance,
                exactAssetCooldownMinutes: 120),
            TimeSpan.FromSeconds(1),
            categoryTargets: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [AssetTypes.AnimatedVisual] = 0.99,
                [AssetTypes.MusicVideo] = 0.01,
            },
            history: ShortCapHistoryWithEarlierPlay(
                "full-breaker",
                "full-breaker",
                minutesAgo: 105));

        Assert.Equal("full-breaker", result.Playlist.Items[0].AssetId);
        Assert.Equal(1, result.Playlist.Summary.ExactAssetCooldownRelaxations);
        Assert.Equal(0, result.Playlist.Summary.ShortRunRelaxations);
    }

    [Fact]
    public void AdjacentFullCandidateIsNotUsedAsShortRunBreakerWhenSafeShortExists()
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("adjacent-full", "last-song", 120),
                Song("safe-short", "safe-short", 30, AssetTypes.AnimatedVisual),
            ],
            Config(ProgrammingLaneNames.ShortPerformance),
            TimeSpan.FromSeconds(1),
            history: HistoryWithOffsets(
                ("history-short-one", "history-one", AssetTypes.ShortForm, 30, 3),
                ("history-short-two", "history-two", AssetTypes.Performance, 30, 2),
                ("history-short-three", "last-song", AssetTypes.AnimatedVisual, 30, 1)));

        Assert.Equal("safe-short", result.Playlist.Items[0].AssetId);
        Assert.Equal(1, result.Playlist.Summary.ShortRunRelaxations);
        Assert.Equal(0, result.Playlist.Summary.ContentGroupAdjacencyViolations);
    }

    [Fact]
    public void ExactAssetCooldownRemainsPreferredBeforeShortCapIsReached()
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("recent-full", "recent-full", 120),
                Song("fresh-full", "fresh-full", 120),
            ],
            Config(
                ProgrammingLaneNames.FullMusic,
                exactAssetCooldownMinutes: 120),
            TimeSpan.FromSeconds(1),
            history: HistoryWithOffsets(
                ("recent-full", "recent-full", AssetTypes.MusicVideo, 120, 105),
                ("personality-reset", null, AssetTypes.Vlog, 90, 1)));

        Assert.Equal("fresh-full", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.ExactAssetCooldownRelaxations);
    }

    [Fact]
    public void ConfiguredMaximumConsecutiveShortPiecesIsApplied()
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("short-two", "short-two", 30, AssetTypes.AnimatedVisual),
                Song("full", "full", 120),
            ],
            Config(
                ProgrammingLaneNames.ShortPerformance,
                maximumConsecutiveShortPieces: 1),
            TimeSpan.FromSeconds(1),
            history: History((AssetTypes.ShortForm, 30)));

        Assert.Equal("full", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.MaximumObservedConsecutiveShortPieces);
    }

    [Fact]
    public void FullMusicResetsShortRun()
    {
        PlaylistGenerationResult full = Generate(
            [Song("full-reset", "full-reset", 120)],
            Config(ProgrammingLaneNames.FullMusic),
            TimeSpan.FromSeconds(1),
            history: ThreeShortPieceHistory());
        PlaylistGenerationResult afterReset = Generate(
            [Song("short-after-full", "short-after-full", 30, AssetTypes.ShortForm)],
            Config(ProgrammingLaneNames.ShortPerformance),
            TimeSpan.FromSeconds(1),
            history: full.UpdatedHistory);

        Assert.Equal("full-reset", full.Playlist.Items[0].AssetId);
        Assert.Equal("short-after-full", afterReset.Playlist.Items[0].AssetId);
        Assert.Equal(0, afterReset.Playlist.Summary.ShortRunRelaxations);
        Assert.Equal(1, afterReset.Playlist.Summary.MaximumObservedConsecutiveShortPieces);
    }

    [Fact]
    public void VlogPersonalityResetsShortRun()
    {
        PlaylistGenerationResult vlog = Generate(
            [Vlog("vlog-reset", 120)],
            Config(ProgrammingLaneNames.Personality),
            TimeSpan.FromSeconds(1),
            history: ThreeShortPieceHistory());
        PlaylistGenerationResult afterReset = Generate(
            [Song("short-after-vlog", "short-after-vlog", 30, AssetTypes.ShortForm)],
            Config(ProgrammingLaneNames.ShortPerformance),
            TimeSpan.FromSeconds(1),
            history: vlog.UpdatedHistory);

        Assert.Equal("vlog-reset", vlog.Playlist.Items[0].AssetId);
        Assert.Equal("short-after-vlog", afterReset.Playlist.Items[0].AssetId);
        Assert.Equal(0, afterReset.Playlist.Summary.ShortRunRelaxations);
        Assert.Equal(1, afterReset.Playlist.Summary.MaximumObservedConsecutiveShortPieces);
    }

    [Theory]
    [InlineData(AssetTypes.Bumper)]
    [InlineData(AssetTypes.Promo)]
    [InlineData(AssetTypes.Interstitial)]
    [InlineData(AssetTypes.Advertisement)]
    public void InsertionDoesNotResetShortRun(string insertionType)
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("short-four", "short-four", 30, AssetTypes.AnimatedVisual),
                Song("full", "full", 120),
            ],
            Config(ProgrammingLaneNames.ShortPerformance),
            TimeSpan.FromSeconds(1),
            history: History(
                (AssetTypes.ShortForm, 30),
                (AssetTypes.Performance, 30),
                (insertionType, 10),
                (AssetTypes.AnimatedVisual, 30)));

        Assert.Equal("full", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.MaximumObservedConsecutiveShortPieces);
    }

    [Fact]
    public void ShortRunUsesControlledRelaxationWhenNoNonShortCandidateExists()
    {
        PlaylistGenerationResult result = Generate(
            [Song("only-short", "only-short", 30, AssetTypes.AnimatedVisual)],
            Config(ProgrammingLaneNames.ShortPerformance),
            TimeSpan.FromSeconds(1),
            history: ThreeShortPieceHistory());

        Assert.Equal("only-short", result.Playlist.Items[0].AssetId);
        Assert.Equal(1, result.Playlist.Summary.ShortRunRelaxations);
        Assert.Equal(1, result.Playlist.Summary.MaximumObservedConsecutiveShortPieces);
    }

    [Fact]
    public void ActiveCampaignCannotBypassMaximumShortRun()
    {
        ProgrammingConfiguration configuration = Config(ProgrammingLaneNames.Personality) with
        {
            ActiveCampaign = new ActiveCampaign
            {
                Enabled = true,
                ContentGroupId = "campaign-short",
                WeightMultiplier = 10,
            },
        };
        PlaylistGenerationResult result = Generate(
            [
                Song("campaign-short", "campaign-short", 30, AssetTypes.ShortForm),
                Vlog("vlog-alternative", 120),
            ],
            configuration,
            TimeSpan.FromSeconds(1),
            categoryTargets: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [AssetTypes.ShortForm] = 0.9,
                [AssetTypes.Vlog] = 0.1,
            },
            history: ThreeShortPieceHistory());

        Assert.Equal("vlog-alternative", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.ShortRunRelaxations);
    }

    [Fact]
    public void CategoryPreferenceCannotReintroduceShortAfterMaximumRun()
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("category-short", "category-short", 30, AssetTypes.AnimatedVisual),
                Vlog("vlog-alternative", 120),
            ],
            Config(ProgrammingLaneNames.Personality),
            TimeSpan.FromSeconds(1),
            categoryTargets: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [AssetTypes.AnimatedVisual] = 0.9,
                [AssetTypes.Vlog] = 0.1,
            },
            history: ThreeShortPieceHistory());

        Assert.Equal("vlog-alternative", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.ShortRunRelaxations);
    }

    [Fact]
    public void PresentationWeightCannotReintroduceShortAfterMaximumRun()
    {
        PlaylistAsset favoredShort = Song(
            "favored-short",
            "favored-short",
            30,
            AssetTypes.AnimatedVisual);
        ProgrammingConfiguration configuration = Config(ProgrammingLaneNames.Personality) with
        {
            AssetOverrides = new Dictionary<string, AssetEditorialOverride>
            {
                [favoredShort.AssetId] = new() { WeightMultiplier = 10 },
            },
        };
        PlaylistGenerationResult result = Generate(
            [favoredShort, Vlog("vlog-alternative", 120)],
            configuration,
            TimeSpan.FromSeconds(1),
            categoryTargets: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [AssetTypes.AnimatedVisual] = 0.9,
                [AssetTypes.Vlog] = 0.1,
            },
            history: ThreeShortPieceHistory());

        Assert.Equal("vlog-alternative", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.ShortRunRelaxations);
    }

    [Fact]
    public void CategoryCapacityFallbackCannotBypassMaximumShortRun()
    {
        PlaylistGenerationResult result = Generate(
            [
                .. Enumerable.Range(1, 4).Select(index => Song(
                    $"short-{index}",
                    $"short-group-{index}",
                    30,
                    AssetTypes.AnimatedVisual)),
                .. Enumerable.Range(1, 6).Select(index => Song(
                    $"full-{index}",
                    $"full-group-{index}",
                    120)),
            ],
            Config(
                ProgrammingLaneNames.ShortPerformance,
                exactAssetCooldownMinutes: 120),
            TimeSpan.FromMinutes(5),
            categoryTargets: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [AssetTypes.AnimatedVisual] = 0.9,
                [AssetTypes.MusicVideo] = 0.1,
            });

        Assert.Contains(AssetTypes.AnimatedVisual, result.Playlist.Summary.CapacityLimitedCategories);
        Assert.True(result.Playlist.Summary.MaximumObservedConsecutiveShortPieces <= 3);
        Assert.Equal(0, result.Playlist.Summary.ShortRunRelaxations);
    }

    [Fact]
    public void EveryUnavoidableShortBeyondMaximumIsCountedFromFinalEmissions()
    {
        PlaylistGenerationResult result = Generate(
            Enumerable.Range(1, 6)
                .Select(index => Song(
                    $"short-{index}",
                    $"short-group-{index}",
                    30,
                    AssetTypes.AnimatedVisual))
                .ToArray(),
            Config(ProgrammingLaneNames.ShortPerformance),
            TimeSpan.FromMinutes(3));

        Assert.Equal(6, result.Playlist.Items.Count);
        Assert.Equal(3, result.Playlist.Summary.ShortRunRelaxations);
        Assert.Equal(6, result.Playlist.Summary.MaximumObservedConsecutiveShortPieces);
        Assert.Equal(
            CalculateMaximumShortRun(result.Playlist.Items),
            result.Playlist.Summary.MaximumObservedConsecutiveShortPieces);
    }

    [Fact]
    public void LaneAndCategoryFallbackCannotReintroduceAdjacentPerformance()
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("performance-b", "same-song", 120, AssetTypes.Performance),
                Vlog("vlog-alternative", 120),
            ],
            Config(ProgrammingLaneNames.Personality),
            TimeSpan.FromSeconds(1),
            categoryTargets: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [AssetTypes.Performance] = 0.9,
                [AssetTypes.Vlog] = 0.1,
            },
            history: HistoryWithIdentity(
                ("performance-a", "same-song", AssetTypes.Performance, 120)));

        Assert.Equal("vlog-alternative", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.ContentGroupAdjacencyViolations);
    }

    [Fact]
    public void SameGroupPerformancePresentationCannotFollowWhenAnotherGroupExists()
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("performance-b", "same-song", 120, AssetTypes.Performance),
                Song("other-performance", "other-song", 120, AssetTypes.Performance),
            ],
            Config(ProgrammingLaneNames.ShortPerformance),
            TimeSpan.FromSeconds(1),
            history: HistoryWithIdentity(
                ("performance-a", "same-song", AssetTypes.Performance, 120)));

        Assert.Equal("other-performance", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.ContentGroupAdjacencyViolations);
    }

    [Fact]
    public void CampaignCannotReintroduceAdjacentPerformance()
    {
        ProgrammingConfiguration configuration = Config(ProgrammingLaneNames.Personality) with
        {
            ActiveCampaign = new ActiveCampaign
            {
                Enabled = true,
                ContentGroupId = "same-song",
                WeightMultiplier = 10,
            },
        };
        PlaylistGenerationResult result = Generate(
            [
                Song("performance-b", "same-song", 120, AssetTypes.Performance),
                Vlog("vlog-alternative", 120),
            ],
            configuration,
            TimeSpan.FromSeconds(1),
            categoryTargets: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [AssetTypes.Performance] = 0.9,
                [AssetTypes.Vlog] = 0.1,
            },
            history: HistoryWithIdentity(
                ("performance-a", "same-song", AssetTypes.Performance, 120)));

        Assert.Equal("vlog-alternative", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.ContentGroupAdjacencyViolations);
    }

    [Fact]
    public void PresentationWeightCannotReintroduceAdjacentPerformance()
    {
        PlaylistAsset adjacent = Song(
            "performance-b",
            "same-song",
            120,
            AssetTypes.Performance);
        ProgrammingConfiguration configuration = Config(ProgrammingLaneNames.Personality) with
        {
            AssetOverrides = new Dictionary<string, AssetEditorialOverride>
            {
                [adjacent.AssetId] = new() { WeightMultiplier = 10 },
            },
        };
        PlaylistGenerationResult result = Generate(
            [adjacent, Vlog("vlog-alternative", 120)],
            configuration,
            TimeSpan.FromSeconds(1),
            categoryTargets: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [AssetTypes.Performance] = 0.9,
                [AssetTypes.Vlog] = 0.1,
            },
            history: HistoryWithIdentity(
                ("performance-a", "same-song", AssetTypes.Performance, 120)));

        Assert.Equal("vlog-alternative", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.ContentGroupAdjacencyViolations);
    }

    [Theory]
    [InlineData(AssetTypes.Bumper)]
    [InlineData(AssetTypes.Promo)]
    public void InsertionInHistoryDoesNotLegalizeAdjacentSong(string insertionType)
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("performance-b", "same-song", 120, AssetTypes.Performance),
                Vlog("vlog-alternative", 120),
            ],
            Config(ProgrammingLaneNames.Personality),
            TimeSpan.FromSeconds(1),
            categoryTargets: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [AssetTypes.Performance] = 0.9,
                [AssetTypes.Vlog] = 0.1,
            },
            history: HistoryWithIdentity(
                ("performance-a", "same-song", AssetTypes.Performance, 120),
                ("insertion", null, insertionType, 10)));

        Assert.Equal("vlog-alternative", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.ContentGroupAdjacencyViolations);
    }

    [Fact]
    public void ShortRunAndAdjacencyConstraintsSelectValidAlternativeTogether()
    {
        PlaylistGenerationResult result = Generate(
            [
                Song("adjacent-short", "last-song", 30, AssetTypes.ShortForm),
                Song("other-short", "other-short", 30, AssetTypes.AnimatedVisual),
                Song("valid-full", "valid-full", 120),
            ],
            Config(ProgrammingLaneNames.ShortPerformance),
            TimeSpan.FromSeconds(1),
            categoryTargets: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [AssetTypes.ShortForm] = 0.45,
                [AssetTypes.AnimatedVisual] = 0.45,
                [AssetTypes.MusicVideo] = 0.1,
            },
            history: HistoryWithIdentity(
                ("history-one", "history-one", AssetTypes.ShortForm, 30),
                ("history-two", "history-two", AssetTypes.Performance, 30),
                ("history-three", "last-song", AssetTypes.AnimatedVisual, 30)));

        Assert.Equal("valid-full", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.ShortRunRelaxations);
        Assert.Equal(0, result.Playlist.Summary.ContentGroupAdjacencyViolations);
    }

    [Fact]
    public void SongClusterLookbackRemainsAheadOfCampaignWeight()
    {
        ProgrammingConfiguration configuration = Config(ProgrammingLaneNames.ShortPerformance) with
        {
            ActiveCampaign = new ActiveCampaign
            {
                Enabled = true,
                ContentGroupId = "recent-song",
                WeightMultiplier = 10,
            },
        };
        PlaylistGenerationResult result = Generate(
            [
                Song("recent-performance", "recent-song", 120, AssetTypes.Performance),
                Song("fresh-performance", "fresh-song", 120, AssetTypes.Performance),
            ],
            configuration,
            TimeSpan.FromSeconds(1),
            history: HistoryWithIdentity(
                ("recent-history", "recent-song", AssetTypes.MusicVideo, 120),
                ("other-history", "other-song", AssetTypes.MusicVideo, 120)));

        Assert.Equal("fresh-performance", result.Playlist.Items[0].AssetId);
        Assert.Equal(0, result.Playlist.Summary.ContentGroupClusterRelaxations);
    }

    [Fact]
    public void PlannedHistoryCarriesShortRunAndAdjacencyContextsAcrossBlocks()
    {
        PlaylistGenerationResult first = Generate(
            [
                Song("short-a", "group-a", 30, AssetTypes.ShortForm),
                Song("short-b", "group-b", 30, AssetTypes.AnimatedVisual),
                Song("short-c", "group-c", 30, AssetTypes.Performance),
            ],
            Config(ProgrammingLaneNames.ShortPerformance),
            TimeSpan.FromMinutes(1.5),
            seed: 40);
        string previousGroup = first.Playlist.Items[^1].ContentGroupId!;
        PlaylistGenerationResult second = Generate(
            [
                Song("adjacent-short", previousGroup, 30, AssetTypes.ShortForm),
                Song("other-short", "other-short", 30, AssetTypes.AnimatedVisual),
                Song("valid-full", "valid-full", 120),
            ],
            Config(ProgrammingLaneNames.ShortPerformance),
            TimeSpan.FromSeconds(1),
            history: first.UpdatedHistory);

        Assert.Equal(3, CalculateMaximumShortRun(first.Playlist.Items));
        Assert.Equal("valid-full", second.Playlist.Items[0].AssetId);
        Assert.Equal(0, second.Playlist.Summary.ShortRunRelaxations);
        Assert.Equal(0, second.Playlist.Summary.ContentGroupAdjacencyViolations);
    }

    [Fact]
    public void PlannedHistoryUsesExactCooldownRelaxationForCrossPlaylistBreaker()
    {
        ProgrammingConfiguration fullConfiguration = Config(
            ProgrammingLaneNames.FullMusic,
            exactAssetCooldownMinutes: 120);
        PlaylistGenerationResult fullBlock = Generate(
            [Song("full-breaker", "full-breaker", 120)],
            fullConfiguration,
            TimeSpan.FromSeconds(1));
        PlaylistGenerationResult shortBlock = Generate(
            [
                Song("short-a", "short-a", 30, AssetTypes.ShortForm),
                Song("short-b", "short-b", 30, AssetTypes.AnimatedVisual),
                Song("short-c", "short-c", 30, AssetTypes.Performance),
            ],
            Config(
                ProgrammingLaneNames.ShortPerformance,
                exactAssetCooldownMinutes: 120),
            TimeSpan.FromSeconds(90),
            history: fullBlock.UpdatedHistory);
        PlaylistGenerationResult resumedProgramming = Generate(
            [
                Song("preferred-short", "preferred-short", 30, AssetTypes.AnimatedVisual),
                Song("full-breaker", "full-breaker", 120),
            ],
            Config(
                ProgrammingLaneNames.ShortPerformance,
                exactAssetCooldownMinutes: 120),
            TimeSpan.FromSeconds(1),
            history: shortBlock.UpdatedHistory);

        Assert.Equal(3, CalculateMaximumShortRun(shortBlock.Playlist.Items));
        Assert.Equal("full-breaker", resumedProgramming.Playlist.Items[0].AssetId);
        Assert.Equal(1, resumedProgramming.Playlist.Summary.ExactAssetCooldownRelaxations);
        Assert.Equal(0, resumedProgramming.Playlist.Summary.ShortRunRelaxations);
    }

    [Fact]
    public void ProductionShapedShortInventoryRelaxesExactCooldownInsteadOfBuildingLongRun()
    {
        PlaylistAsset[] assets =
        [
            .. Enumerable.Range(1, 30).Select(index => Song(
                $"short-{index}",
                $"short-group-{index}",
                30,
                AssetTypes.AnimatedVisual)),
            Song("full-one", "full-one", 158, AssetTypes.AnimatedVisual),
            Song("full-two", "full-two", 158, AssetTypes.AnimatedVisual),
            Vlog("limited-vlog", 90),
        ];
        ProgrammingConfiguration configuration = Config(
            ProgrammingLaneNames.ShortPerformance,
            exactAssetCooldownMinutes: 120);
        IReadOnlyDictionary<string, double> targets = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [AssetTypes.AnimatedVisual] = 0.95,
            [AssetTypes.Vlog] = 0.05,
        };

        PlaylistGenerationResult first = Generate(
            assets,
            configuration,
            TimeSpan.FromMinutes(30),
            seed: 20260930,
            categoryTargets: targets);
        PlaylistGenerationResult second = Generate(
            assets,
            configuration,
            TimeSpan.FromMinutes(30),
            seed: 20260930,
            categoryTargets: targets);

        Assert.Equal(first.Playlist.Items, second.Playlist.Items);
        Assert.True(first.Playlist.Summary.ExactAssetCooldownRelaxations > 0);
        Assert.Equal(0, first.Playlist.Summary.ShortRunRelaxations);
        Assert.True(first.Playlist.Summary.MaximumObservedConsecutiveShortPieces <= 3);
        Assert.Equal(
            CalculateMaximumShortRun(first.Playlist.Items),
            first.Playlist.Summary.MaximumObservedConsecutiveShortPieces);
        Assert.Equal(0, first.Playlist.Summary.ContentGroupAdjacencyViolations);
    }

    [Fact]
    public void ActiveProgrammingGenerationIsDeterministicForFixedInputs()
    {
        PlaylistAsset[] assets =
        [
            Song("a-one", "a"),
            Song("a-two", "a", type: AssetTypes.LyricVideo),
            Song("b", "b"),
            Song("c-short", "c", 30, AssetTypes.ShortForm),
            Vlog("vlog", 90),
            Bumper("bumper"),
            Promo("promo"),
        ];
        ProgrammingConfiguration configuration = ProgrammingConfiguration.CreateDefault();

        PlaylistGenerationResult first = Generate(assets, configuration, TimeSpan.FromMinutes(30), 55);
        PlaylistGenerationResult second = Generate(assets, configuration, TimeSpan.FromMinutes(30), 55);

        Assert.Equal(
            first.Playlist.Items.Select(item => item.AssetId),
            second.Playlist.Items.Select(item => item.AssetId));
        Assert.Equal(first.UpdatedHistory.Plays, second.UpdatedHistory.Plays);
        Assert.Equal(
            first.Playlist.Summary.ShortRunRelaxations,
            second.Playlist.Summary.ShortRunRelaxations);
        Assert.Equal(
            first.Playlist.Summary.MaximumObservedConsecutiveShortPieces,
            second.Playlist.Summary.MaximumObservedConsecutiveShortPieces);
        Assert.Equal(
            CalculateMaximumShortRun(first.Playlist.Items),
            first.Playlist.Summary.MaximumObservedConsecutiveShortPieces);
    }

    [Fact]
    public void UpdatedHistoryStillRepresentsTheGeneratedPlannedSchedule()
    {
        PlaylistGenerationResult result = Generate(
            [Song("a", "a"), Song("b", "b"), Song("c", "c")],
            Config(ProgrammingLaneNames.FullMusic),
            TimeSpan.FromMinutes(4));
        PlaylistHistoryEntry[] plays = result.UpdatedHistory.Plays.ToArray();

        Assert.Equal(result.Playlist.Items.Count, plays.Length);
        Assert.Equal(
            result.Playlist.Items.Select(item => item.AssetId),
            plays.Select(play => play.AssetId));
        Assert.Equal(
            result.Playlist.Items.Select(item => result.Playlist.ScheduleStartUtc.AddSeconds(item.StartOffsetSeconds)),
            plays.Select(play => play.PlayedAtUtc));
    }

    [Fact]
    public void SixHourActiveProgrammingGenerationRemainsDeterministic()
    {
        PlaylistAsset[] assets =
        [
            .. Enumerable.Range(1, 12).Select(index => Song($"full-{index}", $"full-group-{index}", 180)),
            .. Enumerable.Range(1, 8).Select(index => Song($"short-{index}", $"short-group-{index}", 30, AssetTypes.ShortForm)),
            .. Enumerable.Range(1, 4).Select(index => Vlog($"vlog-{index}", 240)),
            .. Enumerable.Range(1, 5).Select(index => Bumper($"bumper-{index}")),
            Promo("promo-a"),
            Promo("promo-b", AssetTypes.Advertisement),
        ];

        PlaylistGenerationResult first = Generate(
            assets,
            ProgrammingConfiguration.CreateDefault(),
            TimeSpan.FromHours(6),
            20260930);
        PlaylistGenerationResult second = Generate(
            assets,
            ProgrammingConfiguration.CreateDefault(),
            TimeSpan.FromHours(6),
            20260930);

        Assert.Equal(first.Playlist.Items, second.Playlist.Items);
        Assert.True(first.Playlist.ActualDurationSeconds >= TimeSpan.FromHours(6).TotalSeconds);
        Assert.True(first.Playlist.Summary.MaximumObservedConsecutiveShortPieces <= 3);
        Assert.Equal(0, first.Playlist.Summary.ShortRunRelaxations);
        Assert.Equal(0, first.Playlist.Summary.ContentGroupAdjacencyViolations);
        AssertNoAdjacentSubstantialSong(first.Playlist.Items);
    }

    [Fact]
    public void ActiveProgrammingKeepsAvailabilityAwareCategoryCapacityPlanning()
    {
        ProgrammingConfiguration configuration = Config(ProgrammingLaneNames.FullMusic) with
        {
            Repetition = new ProgrammingRepetitionPolicy
            {
                ExactAssetCooldownMinutes = 120,
                SameContentGroupLookback = 2,
            },
        };
        PlaylistGenerationResult result = Generate(
            [
                Song("only-music", "music", 60),
                .. Enumerable.Range(1, 8).Select(index => Vlog($"vlog-{index}", 240)),
            ],
            configuration,
            TimeSpan.FromHours(6),
            categoryTargets: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [AssetTypes.MusicVideo] = 0.8,
                [AssetTypes.Vlog] = 0.2,
            });

        Assert.Contains(AssetTypes.MusicVideo, result.Playlist.Summary.CapacityLimitedCategories);
        Assert.True(result.Playlist.Summary.RedistributedTargetAirtimeSeconds > 0);
        Assert.True(result.Playlist.Summary.EffectiveAirtimeTargetPercentages[AssetTypes.MusicVideo]
            < result.Playlist.Summary.ConfiguredAirtimeTargetPercentages[AssetTypes.MusicVideo]);
    }

    private static PlaylistGenerationResult Generate(
        IReadOnlyCollection<PlaylistAsset> assets,
        ProgrammingConfiguration configuration,
        TimeSpan target,
        int seed = 19,
        IReadOnlyDictionary<string, double>? categoryTargets = null,
        PlaylistHistoryDocument? history = null)
    {
        IReadOnlyDictionary<string, double> targets = categoryTargets ?? assets
            .Where(asset => ProgrammingContentClassifier.IsSubstantial(asset.Type))
            .Select(asset => asset.Type)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(type => type, _ => 1.0, StringComparer.Ordinal);
        var policy = new PlaylistPolicy
        {
            TargetDuration = target,
            CategoryAirtimeTargets = targets,
            InterstitialCadence = null,
        };
        return new PlaylistGenerator().Generate(
            assets,
            [],
            history ?? PlaylistHistoryDocument.Empty,
            policy,
            seed,
            Now,
            configuration);
    }

    private static ProgrammingConfiguration Config(
        string lane,
        int lookback = 2,
        int bumperMinimum = 3,
        int bumperMaximum = 5,
        int promoMinimumMinutes = 30,
        int promoMaximumMinutes = 45,
        int maximumConsecutiveShortPieces = 3,
        int exactAssetCooldownMinutes = 1)
    {
        return ProgrammingConfiguration.CreateDefault() with
        {
            Repetition = new ProgrammingRepetitionPolicy
            {
                ExactAssetCooldownMinutes = exactAssetCooldownMinutes,
                SameContentGroupLookback = lookback,
                MaximumConsecutiveShortPieces = maximumConsecutiveShortPieces,
            },
            StationImaging = new StationImagingPolicy
            {
                MinimumSubstantialPieces = bumperMinimum,
                MaximumSubstantialPieces = bumperMaximum,
            },
            PromoCadence = new ProgrammingPromoCadence
            {
                MinimumIntervalMinutes = promoMinimumMinutes,
                MaximumIntervalMinutes = promoMaximumMinutes,
            },
            Personalities = ProgrammingPersonalityNames.Supported
                .Order(StringComparer.Ordinal)
                .Select(name => new ProgrammingPersonality { Name = name, Lanes = [lane] })
                .ToArray(),
        };
    }

    private static PlaylistAsset Song(
        string assetId,
        string contentGroupId,
        double seconds = 60,
        string type = AssetTypes.MusicVideo) => new(
        assetId,
        contentGroupId,
        assetId,
        "Nzyte",
        type,
        null,
        $"{type}/{assetId}.mp4",
        seconds,
        null);

    private static PlaylistAsset Bumper(string assetId) => new(
        assetId,
        null,
        assetId,
        "Nzyte",
        AssetTypes.Bumper,
        null,
        $"Bumpers/{assetId}.mp4",
        5,
        null);

    private static PlaylistAsset Promo(string assetId, string type = AssetTypes.Promo) => new(
        assetId,
        null,
        assetId,
        "Nzyte",
        type,
        null,
        $"Promos/{assetId}.mp4",
        10,
        null);

    private static PlaylistAsset Vlog(string assetId, double seconds) => new(
        assetId,
        null,
        assetId,
        "Nzyte",
        AssetTypes.Vlog,
        null,
        $"Vlog Episodes/{assetId}.mp4",
        seconds,
        null);

    private static PlaylistHistoryDocument ThreeShortPieceHistory() => History(
        (AssetTypes.ShortForm, 30),
        (AssetTypes.Performance, 30),
        (AssetTypes.AnimatedVisual, 30));

    private static PlaylistHistoryDocument History(params (string Type, double DurationSeconds)[] plays) => new()
    {
        ScheduleEndUtc = Now,
        Plays = plays.Select((play, index) => new PlaylistHistoryEntry(
            $"history-{index}",
            AssetTypes.IsSongBased(play.Type) ? $"history-group-{index}" : null,
            play.Type,
            Now.AddMinutes(index - plays.Length),
            play.DurationSeconds)).ToArray(),
    };

    private static PlaylistHistoryDocument HistoryWithIdentity(
        params (string AssetId, string? ContentGroupId, string Type, double DurationSeconds)[] plays) => new()
        {
            ScheduleEndUtc = Now,
            Plays = plays.Select((play, index) => new PlaylistHistoryEntry(
                play.AssetId,
                play.ContentGroupId,
                play.Type,
                Now.AddMinutes(index - plays.Length),
                play.DurationSeconds)).ToArray(),
        };

    private static PlaylistHistoryDocument ShortCapHistoryWithEarlierPlay(
        string assetId,
        string contentGroupId,
        double minutesAgo) => HistoryWithOffsets(
            (assetId, contentGroupId, AssetTypes.MusicVideo, 120, minutesAgo),
            ("history-short-one", "history-one", AssetTypes.ShortForm, 30, 3),
            ("history-short-two", "history-two", AssetTypes.Performance, 30, 2),
            ("history-short-three", "history-three", AssetTypes.AnimatedVisual, 30, 1));

    private static PlaylistHistoryDocument HistoryWithOffsets(
        params (
            string AssetId,
            string? ContentGroupId,
            string Type,
            double DurationSeconds,
            double MinutesAgo)[] plays) => new()
            {
                ScheduleEndUtc = Now,
                Plays = plays.Select(play => new PlaylistHistoryEntry(
                    play.AssetId,
                    play.ContentGroupId,
                    play.Type,
                    Now.AddMinutes(-play.MinutesAgo),
                    play.DurationSeconds)).ToArray(),
            };

    private static int CalculateMaximumShortRun(IReadOnlyList<PlaylistItem> items)
    {
        var policy = new PlaylistPolicy();
        int current = 0;
        int maximum = 0;
        foreach (PlaylistItem item in items)
        {
            if (!ProgrammingContentClassifier.IsSubstantial(item.Type))
            {
                continue;
            }

            current = ProgrammingContentClassifier.IsShortProgrammingPiece(
                item.Type,
                item.DurationSeconds,
                policy)
                ? current + 1
                : 0;
            maximum = Math.Max(maximum, current);
        }

        return maximum;
    }

    private static void AssertNoAdjacentSubstantialSong(IReadOnlyList<PlaylistItem> items)
    {
        PlaylistItem[] substantial = items
            .Where(item => ProgrammingContentClassifier.IsSubstantial(item.Type))
            .ToArray();
        Assert.DoesNotContain(substantial.Zip(substantial.Skip(1)), pair =>
            !string.IsNullOrWhiteSpace(pair.First.ContentGroupId)
            && string.Equals(pair.First.ContentGroupId, pair.Second.ContentGroupId, StringComparison.Ordinal));
    }
}
