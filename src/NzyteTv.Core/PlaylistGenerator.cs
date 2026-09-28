namespace NzyteTv.Core;

public sealed class PlaylistGenerator
{
    public PlaylistGenerationResult Generate(
        IReadOnlyCollection<PlaylistAsset> eligibleAssets,
        IReadOnlyCollection<PlaylistExclusion> exclusions,
        PlaylistHistoryDocument history,
        PlaylistPolicy policy,
        int seed,
        DateTimeOffset generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(eligibleAssets);
        ArgumentNullException.ThrowIfNull(exclusions);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        ValidateHistory(history);

        PlaylistAsset[] orderedAssets = eligibleAssets
            .OrderBy(asset => asset.AssetId, StringComparer.Ordinal)
            .ThenBy(asset => asset.RelativePath, StringComparer.Ordinal)
            .ToArray();
        ValidateAssets(orderedAssets);

        var allExclusions = new List<PlaylistExclusion>(exclusions);
        PlaylistAsset[] assets = orderedAssets.Where(asset =>
        {
            if (policy.IsCategoryEnabled(asset.Type))
            {
                return true;
            }

            allExclusions.Add(new PlaylistExclusion(
                asset.RelativePath,
                [$"Asset type '{asset.Type}' has no airtime target or cadence in the active playlist policy."]));
            return false;
        }).ToArray();
        if (assets.Length == 0)
        {
            throw new InvalidOperationException(
                "No playlist-eligible assets participate in the active scheduling policy.");
        }

        IReadOnlySet<string> availableTypes = assets.Select(asset => asset.Type)
            .ToHashSet(StringComparer.Ordinal);

        DateTimeOffset generated = generatedAtUtc.ToUniversalTime();
        DateTimeOffset scheduleStart = history.ScheduleEndUtc is DateTimeOffset priorEnd && priorEnd > generated
            ? priorEnd.ToUniversalTime()
            : generated;
        var random = new StableRandom(seed);
        var state = new SchedulerState(history, scheduleStart, policy);
        var items = new List<PlaylistItem>();
        var relaxation = new RelaxationCounts();
        double targetSeconds = policy.TargetDuration.TotalSeconds;
        PlaylistTargetPlan targetPlan = PlaylistTargetPlanner.Calculate(assets, policy);
        IReadOnlyDictionary<string, double> effectiveTargets = targetPlan.EffectiveTargets;
        double offsetSeconds = 0;

        while (offsetSeconds < targetSeconds)
        {
            DateTimeOffset playTime = scheduleStart.AddSeconds(offsetSeconds);
            IReadOnlySet<string> preferredTypes = GetPreferredTypes(
                assets,
                state,
                policy,
                effectiveTargets,
                playTime);
            bool vlogAtOrAboveTarget = IsCategoryAtOrAboveTarget(
                AssetTypes.Vlog,
                state,
                effectiveTargets);
            bool musicBelowCombinedTarget = IsMusicBelowCombinedTarget(state, policy, effectiveTargets);
            CandidateEvaluation[] evaluated = assets
                .Where(asset => IsCadenceEligible(asset.Type, state, policy, playTime))
                .Select(asset => new CandidateEvaluation(
                    asset,
                    preferredTypes.Contains(asset.Type),
                    IsExactAssetAllowed(asset, state, policy, playTime),
                    IsContentGroupPreferred(asset, state, policy, playTime),
                    IsContentGroupFloorAllowed(asset, state, policy, playTime),
                    IsContentGroupRescueAllowed(asset, state, policy, playTime),
                    IsVlogPreferred(asset, state, policy),
                    IsVlogWithinNormalLimit(asset, state, policy),
                    IsCadenceOverdue(asset.Type, state, policy, playTime),
                    state.GetLastAssetPlay(asset.AssetId),
                    policy.MusicOrientedNormalTypes.Contains(asset.Type),
                    IsShortSongPresentation(asset, policy),
                    IsCategoryBelowTarget(asset.Type, state, effectiveTargets),
                    GetCategoryDeficitImprovement(asset, state, effectiveTargets),
                    state.GetLastContentGroupPlay(asset.ContentGroupId),
                    WouldMateriallyOvershootVlogTarget(asset, state, policy, effectiveTargets),
                    policy.GetRotationWeight(
                        asset.RotationStartDate,
                        DateOnly.FromDateTime(playTime.UtcDateTime))))
                .ToArray();

            CandidateSelection selection = SelectCandidate(
                evaluated,
                policy,
                playTime,
                vlogAtOrAboveTarget,
                musicBelowCombinedTarget,
                random);
            CandidateEvaluation selected = selection.Candidate;
            if (!selected.CategoryPreferred)
            {
                relaxation.CategoryTarget++;
            }

            if (!selected.ExactAssetAllowed)
            {
                relaxation.ExactAsset++;
            }

            if (selection.HotPreferenceBypassed)
            {
                relaxation.HotPreference++;
            }

            if (selection.MusicFirstRescueUsed)
            {
                relaxation.MusicFirstRescue++;
            }

            if (!selected.ContentGroupRescueAllowed)
            {
                relaxation.CountEmergency(GetContentGroupPacing(selected.Asset, state, policy, playTime).Kind);
            }

            if (selection.MusicFirstCategorySubstitutionUsed)
            {
                relaxation.MusicFirstCategorySubstitution++;
            }

            if (selection.FullPresentationPrioritySubstitutionUsed)
            {
                relaxation.FullPresentationPrioritySubstitution++;
            }

            if (selection.VlogAboveTargetFallbackUsed)
            {
                relaxation.VlogAboveTargetFallback++;
            }

            if (selection.CooldownAgePreferenceSubstitutionUsed)
            {
                relaxation.CooldownAgePreferenceSubstitution++;
            }

            if (selection.ProjectedVlogOvershootSubstitutionUsed)
            {
                relaxation.ProjectedVlogOvershootSubstitution++;
            }

            if (selection.LongMusicAirtimeEfficiencySubstitutionUsed)
            {
                relaxation.LongMusicAirtimeEfficiencySubstitution++;
            }

            if (!selected.ContentGroupPreferred && selected.ContentGroupFloorAllowed)
            {
                relaxation.CountPreferred(GetContentGroupPacing(selected.Asset, state, policy, playTime).Kind);
            }

            if (!selected.VlogPreferred)
            {
                relaxation.ConsecutiveVlog++;
            }

            if (!selected.VlogWithinNormalLimit)
            {
                relaxation.EmergencyVlogRun++;
            }

            CountCadenceMisses(
                selected.Asset.Type,
                availableTypes,
                state,
                policy,
                playTime,
                relaxation);
            PlaylistAsset asset = selected.Asset;
            items.Add(new PlaylistItem(
                items.Count + 1,
                asset.AssetId,
                asset.ContentGroupId,
                asset.Title,
                asset.Type,
                asset.Subtype,
                asset.RelativePath,
                RoundSeconds(asset.DurationSeconds),
                RoundSeconds(offsetSeconds)));
            state.Record(asset, playTime, policy);
            offsetSeconds += asset.DurationSeconds;

        }

        double actualSeconds = RoundSeconds(offsetSeconds);
        IReadOnlyDictionary<string, double> airtime = CalculateAirtimePercentages(items, actualSeconds);
        var summary = new PlaylistSummary
        {
            EligibleAssets = assets.Length,
            ExcludedAssets = allExclusions.Count,
            CategoryTargetRelaxations = relaxation.CategoryTarget,
            ExactAssetCooldownRelaxations = relaxation.ExactAsset,
            NewReleasePreferenceBypasses = relaxation.HotPreference,
            ContentGroupCooldownRelaxations = relaxation.ContentGroup,
            MusicFirstRescueRelaxations = relaxation.MusicFirstRescue,
            EmergencyContentGroupFloorViolations = relaxation.EmergencyContentGroupFloor,
            ShortToShortPreferredRelaxations = relaxation.ShortToShortPreferred,
            FullToShortPreferredRelaxations = relaxation.FullToShortPreferred,
            ShortToFullPreferredRelaxations = relaxation.ShortToFullPreferred,
            EmergencyShortToShortFloorViolations = relaxation.EmergencyShortToShortFloor,
            EmergencyFullToShortFloorViolations = relaxation.EmergencyFullToShortFloor,
            EmergencyShortToFullFloorViolations = relaxation.EmergencyShortToFullFloor,
            MusicFirstCategorySubstitutions = relaxation.MusicFirstCategorySubstitution,
            FullPresentationPrioritySubstitutions = relaxation.FullPresentationPrioritySubstitution,
            VlogAboveTargetFallbacks = relaxation.VlogAboveTargetFallback,
            CooldownAgePreferenceSubstitutions = relaxation.CooldownAgePreferenceSubstitution,
            ProjectedVlogOvershootSubstitutions = relaxation.ProjectedVlogOvershootSubstitution,
            LongMusicAirtimeEfficiencySubstitutions = relaxation.LongMusicAirtimeEfficiencySubstitution,
            ConsecutiveVlogViolations = relaxation.ConsecutiveVlog,
            EmergencyVlogRunViolations = relaxation.EmergencyVlogRun,
            BumperCadenceMisses = relaxation.BumperCadence,
            PromoCadenceMisses = relaxation.PromoCadence,
            InterstitialCadenceMisses = relaxation.InterstitialCadence,
            BumperInsertions = items.Count(item => item.Type == AssetTypes.Bumper),
            PromoInsertions = items.Count(item => item.Type == AssetTypes.Promo),
            InterstitialInsertions = items.Count(item => item.Type == AssetTypes.Interstitial),
            ConfiguredAirtimeTargetPercentages = ToPercentages(targetPlan.ConfiguredTargets),
            EffectiveAirtimeTargetPercentages = ToPercentages(targetPlan.EffectiveTargets),
            PracticalCategoryCapacitySeconds = targetPlan.PracticalCapacitySeconds,
            CapacityLimitedCategories = targetPlan.CapacityLimitedCategories,
            RedistributedTargetAirtimeSeconds = RoundSeconds(targetPlan.RedistributedTargetAirtimeSeconds),
            AirtimePercentages = airtime,
        };
        var playlist = new PlaylistDocument
        {
            GeneratedAtUtc = generated,
            ScheduleStartUtc = scheduleStart,
            Seed = seed,
            TargetDurationSeconds = RoundSeconds(targetSeconds),
            ActualDurationSeconds = actualSeconds,
            OverrunSeconds = RoundSeconds(actualSeconds - targetSeconds),
            Items = items,
            ExcludedAssets = allExclusions
                .OrderBy(exclusion => exclusion.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            Summary = summary,
        };
        DateTimeOffset scheduleEnd = scheduleStart.AddSeconds(actualSeconds);
        TimeSpan retention = Max(policy.ExactAssetCooldown, policy.ContentGroupCooldown)
            + policy.HistorySafetyMargin;
        DateTimeOffset cutoff = scheduleEnd - retention;
        PlaylistHistoryEntry[] retained = history.Plays
            .Concat(items.Select(item => new PlaylistHistoryEntry(
                item.AssetId,
                item.ContentGroupId,
                item.Type,
                scheduleStart.AddSeconds(item.StartOffsetSeconds),
                item.DurationSeconds)))
            .Where(play => play.PlayedAtUtc >= cutoff && play.PlayedAtUtc <= scheduleEnd)
            .OrderBy(play => play.PlayedAtUtc)
            .ToArray();
        var updatedHistory = new PlaylistHistoryDocument
        {
            ScheduleEndUtc = scheduleEnd,
            Plays = retained,
        };
        return new PlaylistGenerationResult(playlist, updatedHistory);
    }

    private static CandidateSelection SelectCandidate(
        IReadOnlyList<CandidateEvaluation> candidates,
        PlaylistPolicy policy,
        DateTimeOffset playTime,
        bool vlogAtOrAboveTarget,
        bool musicBelowCombinedTarget,
        StableRandom random)
    {
        for (int stage = 0; stage <= 4; stage++)
        {
            CandidateEvaluation[] baseCandidates = candidates.Where(candidate => stage switch
            {
                0 => candidate.CategoryPreferred && candidate.ExactAssetAllowed,
                1 => candidate.ExactAssetAllowed,
                _ => true,
            }).ToArray();
            CandidateEvaluation[] available = baseCandidates
                .Where(candidate => stage switch
                {
                    <= 2 => candidate.ContentGroupPreferred && candidate.VlogPreferred,
                    3 => candidate.ContentGroupPreferred && candidate.VlogWithinNormalLimit,
                    _ => candidate.ContentGroupFloorAllowed && candidate.VlogWithinNormalLimit,
                })
                .ToArray();
            if (available.Length == 0)
            {
                if (stage == 0)
                {
                    CandidateEvaluation[] overdueCadence = candidates
                        .Where(candidate => candidate.CadenceOverdue
                            && candidate.ContentGroupPreferred
                            && candidate.VlogPreferred)
                        .ToArray();
                    if (overdueCadence.Length > 0)
                    {
                        DateTimeOffset oldestPlay = overdueCadence
                            .Min(candidate => candidate.LastAssetPlayedAt ?? DateTimeOffset.MinValue);
                        CandidateEvaluation[] rotationCandidates = overdueCadence
                            .Where(candidate => (candidate.LastAssetPlayedAt ?? DateTimeOffset.MinValue) == oldestPlay)
                            .ToArray();
                        return new CandidateSelection(
                            WeightedChoice(rotationCandidates, policy, playTime, random),
                            HotPreferenceBypassed: false,
                            MusicFirstRescueUsed: false);
                    }
                }

                continue;
            }

            CandidatePreference preference = ApplyCandidatePreferences(
                available,
                candidates,
                stage,
                vlogAtOrAboveTarget,
                musicBelowCombinedTarget);
            CandidateEvaluation selected = WeightedChoice(preference.Candidates, policy, playTime, random);
            bool hotPreferenceBypassed = MaximumRotationWeight(baseCandidates, policy, playTime)
                > MaximumRotationWeight(preference.Candidates, policy, playTime);
            return new CandidateSelection(
                selected,
                hotPreferenceBypassed,
                MusicFirstRescueUsed: false,
                MusicFirstCategorySubstitutionUsed: preference.MusicFirstCategorySubstitutionAvailable
                    && selected.MusicOriented,
                FullPresentationPrioritySubstitutionUsed: preference.FullPresentationPrioritySubstitutionAvailable
                    && selected.MusicOriented
                    && !selected.ShortPresentation
                    && selected.CategoryBelowTarget,
                VlogAboveTargetFallbackUsed: vlogAtOrAboveTarget
                    && selected.Asset.Type == AssetTypes.Vlog,
                CooldownAgePreferenceSubstitutionUsed:
                    preference.CooldownAgePreferredCandidates.Contains(selected),
                ProjectedVlogOvershootSubstitutionUsed: preference.ProjectedVlogOvershootSubstitutionAvailable
                    && selected.MusicOriented,
                LongMusicAirtimeEfficiencySubstitutionUsed:
                    preference.LongMusicAirtimeEfficiencySubstitutionAvailable
                    && preference.AirtimeEfficientFullCandidates.Contains(selected));
        }

        CandidateEvaluation[] emergencyVlogRun = candidates
            .Where(candidate => candidate.ContentGroupFloorAllowed
                && !candidate.VlogWithinNormalLimit
                && candidate.Asset.Type == AssetTypes.Vlog)
            .ToArray();
        if (emergencyVlogRun.Length > 0)
        {
            CandidateEvaluation[] musicFirstRescue = candidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Asset.ContentGroupId)
                    && candidate.ContentGroupRescueAllowed
                    && !candidate.ContentGroupFloorAllowed
                    && candidate.VlogWithinNormalLimit)
                .ToArray();
            if (musicFirstRescue.Length > 0)
            {
                return new CandidateSelection(
                    WeightedChoice(musicFirstRescue, policy, playTime, random),
                    HotPreferenceBypassed: false,
                    MusicFirstRescueUsed: true);
            }

            return new CandidateSelection(
                WeightedChoice(emergencyVlogRun, policy, playTime, random),
                HotPreferenceBypassed: false,
                MusicFirstRescueUsed: false,
                VlogAboveTargetFallbackUsed: vlogAtOrAboveTarget);
        }

        CandidateEvaluation[] emergencyContentGroupFloor = candidates
            .Where(candidate => candidate.VlogWithinNormalLimit
                && !candidate.ContentGroupRescueAllowed)
            .ToArray();
        if (emergencyContentGroupFloor.Length > 0)
        {
            return new CandidateSelection(
                WeightedChoice(emergencyContentGroupFloor, policy, playTime, random),
                HotPreferenceBypassed: false,
                MusicFirstRescueUsed: false);
        }

        if (candidates.Count > 0)
        {
            CandidateEvaluation selected = WeightedChoice(candidates, policy, playTime, random);
            return new CandidateSelection(
                selected,
                HotPreferenceBypassed: false,
                MusicFirstRescueUsed: false,
                VlogAboveTargetFallbackUsed: vlogAtOrAboveTarget
                    && selected.Asset.Type == AssetTypes.Vlog);
        }

        throw new InvalidOperationException("Playlist scheduling made no progress because no candidates are available.");
    }

    private static CandidatePreference ApplyCandidatePreferences(
        IReadOnlyCollection<CandidateEvaluation> available,
        IReadOnlyCollection<CandidateEvaluation> allCandidates,
        int stage,
        bool vlogAtOrAboveTarget,
        bool musicBelowCombinedTarget)
    {
        CandidateEvaluation[] preferred = [.. available];
        bool musicFirstSubstitutionAvailable = false;
        bool projectedVlogSubstitutionAvailable = false;
        HashSet<CandidateEvaluation> maturingFullResetCandidates = preferred
            .Where(candidate => WouldDelayMaturingFullPresentation(candidate, allCandidates))
            .ToHashSet();
        if (maturingFullResetCandidates.Count > 0
            && preferred.Any(candidate => candidate.Asset.Type == AssetTypes.Vlog))
        {
            preferred = preferred
                .Where(candidate => !maturingFullResetCandidates.Contains(candidate))
                .ToArray();
        }

        CandidateEvaluation[] legalMusic = allCandidates
            .Where(candidate => candidate.MusicOriented
                && IsCandidateLegalAtStage(candidate, stage)
                && !WouldDelayMaturingFullPresentation(candidate, allCandidates))
            .ToArray();
        CandidateEvaluation[] vlogCandidatesToDefer = preferred
            .Where(candidate => candidate.Asset.Type == AssetTypes.Vlog
                && (vlogAtOrAboveTarget || candidate.ProjectedVlogOvershoot))
            .ToArray();
        if (vlogCandidatesToDefer.Length > 0 && legalMusic.Length > 0)
        {
            preferred = preferred
                .Except(vlogCandidatesToDefer)
                .Concat(legalMusic)
                .Distinct()
                .ToArray();
            musicFirstSubstitutionAvailable = vlogAtOrAboveTarget;
            projectedVlogSubstitutionAvailable = !vlogAtOrAboveTarget
                && vlogCandidatesToDefer.Any(candidate => candidate.ProjectedVlogOvershoot);
        }

        CandidateEvaluation[] underTargetFull = allCandidates
            .Where(candidate => candidate.MusicOriented
                && !candidate.ShortPresentation
                && candidate.CategoryBelowTarget
                && IsCandidateLegalAtStage(candidate, stage))
            .ToArray();
        HashSet<CandidateEvaluation> shortCandidatesToDefer = preferred
            .Where(candidate => candidate.MusicOriented
                && candidate.ShortPresentation
                && (!candidate.CategoryBelowTarget
                    || underTargetFull.Any(full => HasSameContentGroup(full.Asset, candidate.Asset))))
            .ToHashSet();
        HashSet<CandidateEvaluation> efficientFullCandidates = [];
        var efficiencyShortCandidatesToDefer = new HashSet<CandidateEvaluation>();
        bool efficiencyActive = musicBelowCombinedTarget
            || vlogAtOrAboveTarget
            || projectedVlogSubstitutionAvailable;
        if (efficiencyActive && underTargetFull.Length > 0)
        {
            double maximumImprovement = underTargetFull.Max(candidate => candidate.CategoryDeficitImprovement);
            efficientFullCandidates = underTargetFull
                .Where(candidate => candidate.CategoryDeficitImprovement >= maximumImprovement - 0.000001)
                .ToHashSet();
            foreach (CandidateEvaluation shortCandidate in preferred.Where(candidate =>
                candidate.MusicOriented
                && candidate.ShortPresentation
                && candidate.CategoryDeficitImprovement < maximumImprovement - 0.000001))
            {
                shortCandidatesToDefer.Add(shortCandidate);
                efficiencyShortCandidatesToDefer.Add(shortCandidate);
            }
        }

        bool fullPriorityAvailable = underTargetFull.Length > 0 && shortCandidatesToDefer.Count > 0;
        if (fullPriorityAvailable)
        {
            preferred = preferred
                .Concat(efficientFullCandidates.Count > 0 ? efficientFullCandidates : underTargetFull)
                .Distinct()
                .Where(candidate => !shortCandidatesToDefer.Contains(candidate))
                .ToArray();
        }

        var cooldownAgePreferredCandidates = new HashSet<CandidateEvaluation>();
        var ageRanked = new HashSet<CandidateEvaluation>();
        foreach (IGrouping<
            (string Type, bool ShortPresentation, bool CategoryBelowTarget, double RotationWeight),
            CandidateEvaluation> group
            in preferred
                .Where(candidate => candidate.MusicOriented
                    && !string.IsNullOrWhiteSpace(candidate.Asset.ContentGroupId))
                .GroupBy(candidate => (
                    candidate.Asset.Type,
                    candidate.ShortPresentation,
                    candidate.CategoryBelowTarget,
                    candidate.RotationWeight)))
        {
            DateTimeOffset oldest = group.Min(candidate =>
                candidate.LastContentGroupPlayedAt ?? DateTimeOffset.MinValue);
            CandidateEvaluation[] oldestCandidates = group
                .Where(candidate => (candidate.LastContentGroupPlayedAt ?? DateTimeOffset.MinValue) == oldest)
                .ToArray();
            foreach (CandidateEvaluation candidate in oldestCandidates)
            {
                ageRanked.Add(candidate);
            }

            if (oldestCandidates.Length < group.Count())
            {
                cooldownAgePreferredCandidates.UnionWith(oldestCandidates);
            }
        }

        if (ageRanked.Count > 0)
        {
            preferred = preferred
                .Where(candidate => !candidate.MusicOriented
                    || string.IsNullOrWhiteSpace(candidate.Asset.ContentGroupId)
                    || ageRanked.Contains(candidate))
                .ToArray();
        }

        return new CandidatePreference(
            preferred,
            musicFirstSubstitutionAvailable,
            fullPriorityAvailable,
            projectedVlogSubstitutionAvailable,
            cooldownAgePreferredCandidates,
            efficientFullCandidates,
            efficientFullCandidates.Count > 0 && efficiencyShortCandidatesToDefer.Count > 0);
    }

    private static bool IsCandidateLegalAtStage(CandidateEvaluation candidate, int stage) =>
        (stage >= 2 || candidate.ExactAssetAllowed)
        && candidate.ContentGroupFloorAllowed
        && candidate.VlogWithinNormalLimit;

    private static bool WouldDelayMaturingFullPresentation(
        CandidateEvaluation candidate,
        IReadOnlyCollection<CandidateEvaluation> allCandidates) =>
        candidate.MusicOriented
        && candidate.ShortPresentation
        && !candidate.ContentGroupPreferred
        && allCandidates.Any(full => full.MusicOriented
            && !full.ShortPresentation
            && full.CategoryBelowTarget
            && full.ExactAssetAllowed
            && !full.ContentGroupFloorAllowed
            && HasSameContentGroup(full.Asset, candidate.Asset));

    private static bool HasSameContentGroup(PlaylistAsset first, PlaylistAsset second) =>
        !string.IsNullOrWhiteSpace(first.ContentGroupId)
        && string.Equals(first.ContentGroupId, second.ContentGroupId, StringComparison.Ordinal);

    private static CandidateEvaluation WeightedChoice(
        IReadOnlyList<CandidateEvaluation> candidates,
        PlaylistPolicy policy,
        DateTimeOffset playTime,
        StableRandom random)
    {
        CandidateEvaluation[] ordered = candidates
            .OrderBy(candidate => candidate.Asset.AssetId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Asset.RelativePath, StringComparer.Ordinal)
            .ToArray();
        DateOnly scheduleDate = DateOnly.FromDateTime(playTime.UtcDateTime);
        double totalWeight = ordered.Sum(candidate =>
            policy.GetRotationWeight(candidate.Asset.RotationStartDate, scheduleDate));
        double choice = random.NextDouble() * totalWeight;
        foreach (CandidateEvaluation candidate in ordered)
        {
            choice -= policy.GetRotationWeight(candidate.Asset.RotationStartDate, scheduleDate);
            if (choice < 0)
            {
                return candidate;
            }
        }

        return ordered[^1];
    }

    private static double MaximumRotationWeight(
        IReadOnlyCollection<CandidateEvaluation> candidates,
        PlaylistPolicy policy,
        DateTimeOffset playTime)
    {
        DateOnly scheduleDate = DateOnly.FromDateTime(playTime.UtcDateTime);
        return candidates.Count == 0
            ? 0
            : candidates.Max(candidate =>
                policy.GetRotationWeight(candidate.Asset.RotationStartDate, scheduleDate));
    }

    private static IReadOnlySet<string> GetPreferredTypes(
        IReadOnlyCollection<PlaylistAsset> assets,
        SchedulerState state,
        PlaylistPolicy policy,
        IReadOnlyDictionary<string, double> effectiveTargets,
        DateTimeOffset playTime)
    {
        HashSet<string> availableTypes = assets.Select(asset => asset.Type).ToHashSet(StringComparer.Ordinal);
        var overdue = new HashSet<string>(StringComparer.Ordinal);
        if (availableTypes.Contains(AssetTypes.Bumper)
            && policy.BumperCadence is not null
            && state.NormalProgramsSinceBumper >= policy.BumperCadence.MaximumPrograms)
        {
            overdue.Add(AssetTypes.Bumper);
        }

        if (availableTypes.Contains(AssetTypes.Promo)
            && policy.PromoCadence is not null
            && playTime - state.LastPromoAt > policy.PromoCadence.MaximumInterval)
        {
            overdue.Add(AssetTypes.Promo);
        }

        if (availableTypes.Contains(AssetTypes.Interstitial)
            && policy.InterstitialCadence is not null
            && playTime - state.LastInterstitialAt > policy.InterstitialCadence.MaximumInterval)
        {
            overdue.Add(AssetTypes.Interstitial);
        }

        if (overdue.Count > 0)
        {
            return overdue;
        }

        var preferred = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, double> availableTargets = effectiveTargets
            .Where(item => item.Value > 0 && availableTypes.Contains(item.Key))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        double targetTotal = availableTargets.Values.Sum();
        if (targetTotal > 0)
        {
            double targetedAirtime = availableTargets.Keys.Sum(state.GetAirtimeSeconds);
            double maximumDeficit = double.NegativeInfinity;
            foreach ((string type, double target) in availableTargets)
            {
                double desiredShare = target / targetTotal;
                double actualShare = targetedAirtime <= 0 ? 0 : state.GetAirtimeSeconds(type) / targetedAirtime;
                double deficit = desiredShare - actualShare;
                if (deficit > maximumDeficit + 0.000001)
                {
                    maximumDeficit = deficit;
                    preferred.Clear();
                    preferred.Add(type);
                }
                else if (Math.Abs(deficit - maximumDeficit) < 0.000001)
                {
                    preferred.Add(type);
                }
            }
        }

        if (availableTypes.Contains(AssetTypes.Bumper)
            && policy.BumperCadence is not null
            && state.NormalProgramsSinceBumper >= policy.BumperCadence.MinimumPrograms)
        {
            preferred.Add(AssetTypes.Bumper);
        }

        if (availableTypes.Contains(AssetTypes.Promo)
            && policy.PromoCadence is not null
            && playTime - state.LastPromoAt >= policy.PromoCadence.MinimumInterval)
        {
            preferred.Add(AssetTypes.Promo);
        }

        if (availableTypes.Contains(AssetTypes.Interstitial)
            && policy.InterstitialCadence is not null
            && playTime - state.LastInterstitialAt >= policy.InterstitialCadence.MinimumInterval)
        {
            preferred.Add(AssetTypes.Interstitial);
        }

        return preferred.Count > 0 ? preferred : availableTypes;
    }

    private static bool IsCategoryBelowTarget(
        string type,
        SchedulerState state,
        IReadOnlyDictionary<string, double> targets)
    {
        if (!targets.TryGetValue(type, out double target) || target <= 0)
        {
            return false;
        }

        double totalTarget = GetTotalTargetWeight(targets);
        double totalAirtime = GetTotalTargetedAirtime(state, targets);
        if (totalTarget <= 0 || totalAirtime <= 0)
        {
            return true;
        }

        double desiredShare = target / totalTarget;
        double actualShare = state.GetAirtimeSeconds(type) / totalAirtime;
        return actualShare < desiredShare - 0.000001;
    }

    private static double GetCategoryDeficitImprovement(
        PlaylistAsset asset,
        SchedulerState state,
        IReadOnlyDictionary<string, double> targets)
    {
        if (!targets.TryGetValue(asset.Type, out double target) || target <= 0)
        {
            return 0;
        }

        double totalTarget = GetTotalTargetWeight(targets);
        double totalAirtime = GetTotalTargetedAirtime(state, targets);
        if (totalTarget <= 0)
        {
            return 0;
        }

        double desiredShare = target / totalTarget;
        double currentShare = totalAirtime <= 0
            ? 0
            : state.GetAirtimeSeconds(asset.Type) / totalAirtime;
        double projectedShare = (state.GetAirtimeSeconds(asset.Type) + asset.DurationSeconds)
            / (totalAirtime + asset.DurationSeconds);
        return Math.Max(0, desiredShare - currentShare)
            - Math.Max(0, desiredShare - projectedShare);
    }

    private static bool IsMusicBelowCombinedTarget(
        SchedulerState state,
        PlaylistPolicy policy,
        IReadOnlyDictionary<string, double> targets)
    {
        double totalAirtime = GetTotalTargetedAirtime(state, targets);
        if (totalAirtime <= 0)
        {
            return true;
        }

        double totalTarget = GetTotalTargetWeight(targets);
        double musicTarget = targets
            .Where(item => policy.MusicOrientedNormalTypes.Contains(item.Key))
            .Sum(item => item.Value);
        double musicAirtime = policy.MusicOrientedNormalTypes.Sum(state.GetAirtimeSeconds);
        return totalTarget > 0
            && musicAirtime / totalAirtime < musicTarget / totalTarget - 0.000001;
    }

    private static bool WouldMateriallyOvershootVlogTarget(
        PlaylistAsset asset,
        SchedulerState state,
        PlaylistPolicy policy,
        IReadOnlyDictionary<string, double> targets)
    {
        if (asset.Type != AssetTypes.Vlog
            || !targets.TryGetValue(AssetTypes.Vlog, out double target)
            || target <= 0)
        {
            return false;
        }

        double totalTarget = GetTotalTargetWeight(targets);
        double totalAirtime = GetTotalTargetedAirtime(state, targets);
        if (totalTarget <= 0)
        {
            return false;
        }

        double desiredShare = target / totalTarget;
        double currentShare = totalAirtime <= 0
            ? 0
            : state.GetAirtimeSeconds(AssetTypes.Vlog) / totalAirtime;
        double projectedShare = (state.GetAirtimeSeconds(AssetTypes.Vlog) + asset.DurationSeconds)
            / (totalAirtime + asset.DurationSeconds);
        return currentShare < desiredShare - 0.000001
            && projectedShare > desiredShare + policy.ProjectedVlogOvershootTolerance;
    }

    private static double GetTotalTargetWeight(IReadOnlyDictionary<string, double> targets) =>
        targets.Values.Where(value => value > 0).Sum();

    private static double GetTotalTargetedAirtime(
        SchedulerState state,
        IReadOnlyDictionary<string, double> targets) =>
        targets.Keys.Sum(state.GetAirtimeSeconds);

    private static bool IsCategoryAtOrAboveTarget(
        string type,
        SchedulerState state,
        IReadOnlyDictionary<string, double> targets) =>
        targets.TryGetValue(type, out double target)
        && target > 0
        && GetTotalTargetedAirtime(state, targets) > 0
        && !IsCategoryBelowTarget(type, state, targets);

    private static bool IsExactAssetAllowed(
        PlaylistAsset asset,
        SchedulerState state,
        PlaylistPolicy policy,
        DateTimeOffset playTime) =>
        !state.LastAssetPlay.TryGetValue(asset.AssetId, out DateTimeOffset previous)
        || playTime - previous >= policy.ExactAssetCooldown;

    private static bool IsContentGroupPreferred(
        PlaylistAsset asset,
        SchedulerState state,
        PlaylistPolicy policy,
        DateTimeOffset playTime) => GetContentGroupPacing(asset, state, policy, playTime).Preferred;

    private static bool IsContentGroupFloorAllowed(
        PlaylistAsset asset,
        SchedulerState state,
        PlaylistPolicy policy,
        DateTimeOffset playTime) => GetContentGroupPacing(asset, state, policy, playTime).FloorAllowed;

    private static bool IsContentGroupRescueAllowed(
        PlaylistAsset asset,
        SchedulerState state,
        PlaylistPolicy policy,
        DateTimeOffset playTime) => GetContentGroupPacing(asset, state, policy, playTime).RescueAllowed;

    private static ContentGroupPacing GetContentGroupPacing(
        PlaylistAsset asset,
        SchedulerState state,
        PlaylistPolicy policy,
        DateTimeOffset playTime)
    {
        SongPresentation current = GetPresentation(asset.Type, asset.DurationSeconds, policy);
        if (current == SongPresentation.None
            || string.IsNullOrWhiteSpace(asset.ContentGroupId)
            || !state.LastContentGroupPresentation.TryGetValue(asset.ContentGroupId, out PresentationPlay? previous))
        {
            return ContentGroupPacing.Unrestricted;
        }

        TimeSpan elapsed = playTime - previous.PlayedAtUtc;
        if (current == SongPresentation.Full && previous.Presentation == SongPresentation.Full)
        {
            return new ContentGroupPacing(
                elapsed >= policy.ContentGroupCooldown,
                elapsed >= policy.ContentGroupMinimumCooldown,
                elapsed >= policy.ContentGroupMusicFirstRescueCooldown,
                GroupPacingKind.FullToFull);
        }

        (TimeSpan preferred, TimeSpan floor, GroupPacingKind kind) = (previous.Presentation, current) switch
        {
            (SongPresentation.Short, SongPresentation.Short) =>
                (policy.ShortToShortPreferredCooldown, policy.ShortToShortMinimumCooldown, GroupPacingKind.ShortToShort),
            (SongPresentation.Full, SongPresentation.Short) =>
                (policy.FullToShortPreferredCooldown, policy.FullToShortMinimumCooldown, GroupPacingKind.FullToShort),
            (SongPresentation.Short, SongPresentation.Full) =>
                (policy.ShortToFullPreferredCooldown, policy.ShortToFullMinimumCooldown, GroupPacingKind.ShortToFull),
            _ => throw new InvalidOperationException("Song presentation pacing is invalid."),
        };
        return new ContentGroupPacing(
            elapsed >= preferred,
            elapsed >= floor,
            elapsed >= floor,
            kind);
    }

    internal static bool IsShortSongPresentation(PlaylistAsset asset, PlaylistPolicy policy) =>
        GetPresentation(asset.Type, asset.DurationSeconds, policy) == SongPresentation.Short;

    private static SongPresentation GetPresentation(string type, double? durationSeconds, PlaylistPolicy policy)
    {
        if (!AssetTypes.IsSongBased(type))
        {
            return SongPresentation.None;
        }

        return type == AssetTypes.ShortForm
            || durationSeconds is double duration && duration <= policy.ShortSongPresentationMaximumDuration.TotalSeconds
                ? SongPresentation.Short
                : SongPresentation.Full;
    }

    private static bool IsVlogPreferred(PlaylistAsset asset, SchedulerState state, PlaylistPolicy policy) =>
        !policy.AvoidConsecutiveVlogs
        || asset.Type != AssetTypes.Vlog
        || state.ConsecutiveVlogCount == 0;

    private static bool IsVlogWithinNormalLimit(
        PlaylistAsset asset,
        SchedulerState state,
        PlaylistPolicy policy) =>
        asset.Type != AssetTypes.Vlog
        || state.ConsecutiveVlogCount < policy.MaximumConsecutiveVlogs;

    private static bool IsCadenceEligible(
        string type,
        SchedulerState state,
        PlaylistPolicy policy,
        DateTimeOffset playTime) => type switch
        {
            AssetTypes.Bumper when policy.BumperCadence is not null =>
                state.NormalProgramsSinceBumper >= policy.BumperCadence.MinimumPrograms,
            AssetTypes.Promo when policy.PromoCadence is not null =>
                playTime - state.LastPromoAt >= policy.PromoCadence.MinimumInterval,
            AssetTypes.Interstitial when policy.InterstitialCadence is not null =>
                playTime - state.LastInterstitialAt >= policy.InterstitialCadence.MinimumInterval,
            _ => true,
        };

    private static bool IsCadenceOverdue(
        string type,
        SchedulerState state,
        PlaylistPolicy policy,
        DateTimeOffset playTime) => type switch
        {
            AssetTypes.Bumper when policy.BumperCadence is not null =>
                state.NormalProgramsSinceBumper >= policy.BumperCadence.MaximumPrograms,
            AssetTypes.Promo when policy.PromoCadence is not null =>
                playTime - state.LastPromoAt > policy.PromoCadence.MaximumInterval,
            AssetTypes.Interstitial when policy.InterstitialCadence is not null =>
                playTime - state.LastInterstitialAt > policy.InterstitialCadence.MaximumInterval,
            _ => false,
        };

    private static void CountCadenceMisses(
        string selectedType,
        IReadOnlySet<string> availableTypes,
        SchedulerState state,
        PlaylistPolicy policy,
        DateTimeOffset playTime,
        RelaxationCounts relaxation)
    {
        if (availableTypes.Contains(AssetTypes.Bumper)
            && policy.BumperCadence is not null
            && state.NormalProgramsSinceBumper >= policy.BumperCadence.MaximumPrograms
            && selectedType != AssetTypes.Bumper)
        {
            relaxation.BumperCadence++;
        }

        if (availableTypes.Contains(AssetTypes.Promo)
            && policy.PromoCadence is not null
            && playTime - state.LastPromoAt > policy.PromoCadence.MaximumInterval
            && selectedType != AssetTypes.Promo)
        {
            relaxation.PromoCadence++;
        }

        if (availableTypes.Contains(AssetTypes.Interstitial)
            && policy.InterstitialCadence is not null
            && playTime - state.LastInterstitialAt > policy.InterstitialCadence.MaximumInterval
            && selectedType != AssetTypes.Interstitial)
        {
            relaxation.InterstitialCadence++;
        }
    }

    private static IReadOnlyDictionary<string, double> CalculateAirtimePercentages(
        IEnumerable<PlaylistItem> items,
        double totalSeconds) =>
        items.GroupBy(item => item.Type, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => totalSeconds <= 0
                    ? 0
                    : Math.Round(group.Sum(item => item.DurationSeconds) / totalSeconds * 100, 2),
                StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, double> ToPercentages(
        IReadOnlyDictionary<string, double> targets) =>
        targets.ToDictionary(
            item => item.Key,
            item => item.Value * 100,
            StringComparer.Ordinal);

    private static void ValidateAssets(IReadOnlyCollection<PlaylistAsset> assets)
    {
        var errors = new List<string>();
        foreach (PlaylistAsset asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.AssetId)
                || string.IsNullOrWhiteSpace(asset.Title)
                || string.IsNullOrWhiteSpace(asset.Type)
                || string.IsNullOrWhiteSpace(asset.RelativePath)
                || !double.IsFinite(asset.DurationSeconds)
                || asset.DurationSeconds <= 0)
            {
                errors.Add($"Invalid playlist asset '{asset.RelativePath}'.");
            }
        }

        foreach (IGrouping<string, PlaylistAsset> duplicate in assets
            .GroupBy(asset => asset.AssetId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            errors.Add($"Duplicate eligible assetId '{duplicate.Key}'.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Playlist assets are invalid:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}");
        }
    }

    private static void ValidateHistory(PlaylistHistoryDocument history)
    {
        if (history.SchemaVersion != PlaylistHistoryDocument.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported playlist history schemaVersion {history.SchemaVersion}; expected {PlaylistHistoryDocument.CurrentSchemaVersion}.");
        }

        if (history.Plays is null
            || history.Plays.Any(play => string.IsNullOrWhiteSpace(play.AssetId)
                || string.IsNullOrWhiteSpace(play.Type)))
        {
            throw new InvalidOperationException("Playlist history contains invalid play entries.");
        }
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first >= second ? first : second;

    private static double RoundSeconds(double seconds) =>
        Math.Round(seconds, 3, MidpointRounding.AwayFromZero);

    private enum SongPresentation
    {
        None,
        Short,
        Full,
    }

    private enum GroupPacingKind
    {
        None,
        FullToFull,
        ShortToShort,
        FullToShort,
        ShortToFull,
    }

    private sealed record PresentationPlay(DateTimeOffset PlayedAtUtc, SongPresentation Presentation);

    private sealed record ContentGroupPacing(
        bool Preferred,
        bool FloorAllowed,
        bool RescueAllowed,
        GroupPacingKind Kind)
    {
        public static ContentGroupPacing Unrestricted { get; } = new(true, true, true, GroupPacingKind.None);
    }

    private sealed record CandidateEvaluation(
        PlaylistAsset Asset,
        bool CategoryPreferred,
        bool ExactAssetAllowed,
        bool ContentGroupPreferred,
        bool ContentGroupFloorAllowed,
        bool ContentGroupRescueAllowed,
        bool VlogPreferred,
        bool VlogWithinNormalLimit,
        bool CadenceOverdue,
        DateTimeOffset? LastAssetPlayedAt,
        bool MusicOriented,
        bool ShortPresentation,
        bool CategoryBelowTarget,
        double CategoryDeficitImprovement,
        DateTimeOffset? LastContentGroupPlayedAt,
        bool ProjectedVlogOvershoot,
        double RotationWeight);

    private sealed record CandidatePreference(
        IReadOnlyList<CandidateEvaluation> Candidates,
        bool MusicFirstCategorySubstitutionAvailable,
        bool FullPresentationPrioritySubstitutionAvailable,
        bool ProjectedVlogOvershootSubstitutionAvailable,
        IReadOnlySet<CandidateEvaluation> CooldownAgePreferredCandidates,
        IReadOnlySet<CandidateEvaluation> AirtimeEfficientFullCandidates,
        bool LongMusicAirtimeEfficiencySubstitutionAvailable);

    private sealed record CandidateSelection(
        CandidateEvaluation Candidate,
        bool HotPreferenceBypassed,
        bool MusicFirstRescueUsed,
        bool MusicFirstCategorySubstitutionUsed = false,
        bool FullPresentationPrioritySubstitutionUsed = false,
        bool VlogAboveTargetFallbackUsed = false,
        bool CooldownAgePreferenceSubstitutionUsed = false,
        bool ProjectedVlogOvershootSubstitutionUsed = false,
        bool LongMusicAirtimeEfficiencySubstitutionUsed = false);

    private sealed class RelaxationCounts
    {
        public int CategoryTarget { get; set; }

        public int ExactAsset { get; set; }

        public int HotPreference { get; set; }

        public int ContentGroup { get; set; }

        public int MusicFirstRescue { get; set; }

        public int EmergencyContentGroupFloor { get; set; }

        public int ShortToShortPreferred { get; set; }

        public int FullToShortPreferred { get; set; }

        public int ShortToFullPreferred { get; set; }

        public int EmergencyShortToShortFloor { get; set; }

        public int EmergencyFullToShortFloor { get; set; }

        public int EmergencyShortToFullFloor { get; set; }

        public int MusicFirstCategorySubstitution { get; set; }

        public int FullPresentationPrioritySubstitution { get; set; }

        public int VlogAboveTargetFallback { get; set; }

        public int CooldownAgePreferenceSubstitution { get; set; }

        public int ProjectedVlogOvershootSubstitution { get; set; }

        public int LongMusicAirtimeEfficiencySubstitution { get; set; }

        public void CountPreferred(GroupPacingKind kind)
        {
            switch (kind)
            {
                case GroupPacingKind.FullToFull: ContentGroup++; break;
                case GroupPacingKind.ShortToShort: ShortToShortPreferred++; break;
                case GroupPacingKind.FullToShort: FullToShortPreferred++; break;
                case GroupPacingKind.ShortToFull: ShortToFullPreferred++; break;
            }
        }

        public void CountEmergency(GroupPacingKind kind)
        {
            switch (kind)
            {
                case GroupPacingKind.FullToFull: EmergencyContentGroupFloor++; break;
                case GroupPacingKind.ShortToShort: EmergencyShortToShortFloor++; break;
                case GroupPacingKind.FullToShort: EmergencyFullToShortFloor++; break;
                case GroupPacingKind.ShortToFull: EmergencyShortToFullFloor++; break;
            }
        }

        public int ConsecutiveVlog { get; set; }

        public int EmergencyVlogRun { get; set; }

        public int BumperCadence { get; set; }

        public int PromoCadence { get; set; }

        public int InterstitialCadence { get; set; }
    }

    private sealed class SchedulerState
    {
        private readonly Dictionary<string, double> _airtime = new(StringComparer.Ordinal);

        public SchedulerState(PlaylistHistoryDocument history, DateTimeOffset scheduleStart, PlaylistPolicy policy)
        {
            PlaylistHistoryEntry[] prior = history.Plays
                .Where(play => play.PlayedAtUtc <= scheduleStart)
                .OrderBy(play => play.PlayedAtUtc)
                .ToArray();
            foreach (PlaylistHistoryEntry play in prior)
            {
                LastAssetPlay[play.AssetId] = play.PlayedAtUtc;
                if (!string.IsNullOrWhiteSpace(play.ContentGroupId))
                {
                    LastContentGroupPresentation[play.ContentGroupId] = new PresentationPlay(
                        play.PlayedAtUtc,
                        GetPresentation(play.Type, play.DurationSeconds, policy));
                }
            }

            ConsecutiveVlogCount = prior.Reverse()
                .TakeWhile(play => play.Type == AssetTypes.Vlog)
                .Count();
            LastPromoAt = prior.LastOrDefault(play => play.Type == AssetTypes.Promo)?.PlayedAtUtc
                ?? scheduleStart;
            LastInterstitialAt = prior.LastOrDefault(play => play.Type == AssetTypes.Interstitial)?.PlayedAtUtc
                ?? scheduleStart;
            int lastBumper = Array.FindLastIndex(prior, play => play.Type == AssetTypes.Bumper);
            NormalProgramsSinceBumper = prior.Skip(lastBumper + 1).Count(play => IsNormalProgram(play.Type));
        }

        public Dictionary<string, DateTimeOffset> LastAssetPlay { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, PresentationPlay> LastContentGroupPresentation { get; } = new(StringComparer.Ordinal);

        public int ConsecutiveVlogCount { get; private set; }

        public int NormalProgramsSinceBumper { get; private set; }

        public DateTimeOffset LastPromoAt { get; private set; }

        public DateTimeOffset LastInterstitialAt { get; private set; }

        public double GetAirtimeSeconds(string type) => _airtime.GetValueOrDefault(type);

        public DateTimeOffset? GetLastAssetPlay(string assetId) =>
            LastAssetPlay.TryGetValue(assetId, out DateTimeOffset playedAt) ? playedAt : null;

        public DateTimeOffset? GetLastContentGroupPlay(string? contentGroupId) =>
            !string.IsNullOrWhiteSpace(contentGroupId)
            && LastContentGroupPresentation.TryGetValue(contentGroupId, out PresentationPlay? play)
                ? play.PlayedAtUtc
                : null;

        public void Record(PlaylistAsset asset, DateTimeOffset playTime, PlaylistPolicy policy)
        {
            LastAssetPlay[asset.AssetId] = playTime;
            if (!string.IsNullOrWhiteSpace(asset.ContentGroupId))
            {
                LastContentGroupPresentation[asset.ContentGroupId] = new PresentationPlay(
                    playTime,
                    GetPresentation(asset.Type, asset.DurationSeconds, policy));
            }

            _airtime[asset.Type] = GetAirtimeSeconds(asset.Type) + asset.DurationSeconds;
            ConsecutiveVlogCount = asset.Type == AssetTypes.Vlog ? ConsecutiveVlogCount + 1 : 0;
            if (asset.Type == AssetTypes.Bumper)
            {
                NormalProgramsSinceBumper = 0;
            }
            else if (IsNormalProgram(asset.Type))
            {
                NormalProgramsSinceBumper++;
            }

            if (asset.Type == AssetTypes.Promo)
            {
                LastPromoAt = playTime;
            }

            if (asset.Type == AssetTypes.Interstitial)
            {
                LastInterstitialAt = playTime;
            }
        }

        private static bool IsNormalProgram(string type) =>
            type is not (AssetTypes.Bumper or AssetTypes.Promo or AssetTypes.Interstitial);
    }

    private sealed class StableRandom
    {
        private ulong _state;

        public StableRandom(int seed)
        {
            _state = unchecked((uint)seed) + 0x9E3779B97F4A7C15UL;
        }

        public double NextDouble()
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return (value >> 11) * (1.0 / (1UL << 53));
        }
    }
}
