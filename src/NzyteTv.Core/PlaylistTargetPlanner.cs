namespace NzyteTv.Core;

public sealed record PlaylistTargetPlan(
    IReadOnlyDictionary<string, double> ConfiguredTargets,
    IReadOnlyDictionary<string, double> EffectiveTargets,
    IReadOnlyDictionary<string, double> PracticalCapacitySeconds,
    IReadOnlyList<string> CapacityLimitedCategories,
    double RedistributedTargetAirtimeSeconds);

public static class PlaylistTargetPlanner
{
    private const double Epsilon = 0.000001;

    public static PlaylistTargetPlan Calculate(
        IReadOnlyCollection<PlaylistAsset> eligibleAssets,
        PlaylistPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(eligibleAssets);
        ArgumentNullException.ThrowIfNull(policy);

        double targetSeconds = policy.TargetDuration.TotalSeconds;
        double configuredWeight = policy.CategoryAirtimeTargets.Values
            .Where(value => value > 0)
            .Sum();
        var configured = policy.CategoryAirtimeTargets
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => item.Key,
                item => configuredWeight > 0 && item.Value > 0 ? item.Value / configuredWeight : 0,
                StringComparer.Ordinal);
        int practicalAppearances = GetPracticalAppearanceBudget(policy);
        var capacitySeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        var effectiveSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        var limited = new List<string>();

        foreach ((string type, double configuredShare) in configured)
        {
            double uniqueSeconds = eligibleAssets
                .Where(asset => asset.Type == type)
                .Sum(asset => asset.DurationSeconds);
            double capacity = policy.ExactAssetCooldown <= TimeSpan.Zero && uniqueSeconds > 0
                ? targetSeconds
                : Math.Min(targetSeconds, uniqueSeconds * practicalAppearances);
            double desired = configuredShare * targetSeconds;
            double attainable = Math.Min(desired, capacity);

            capacitySeconds[type] = capacity;
            effectiveSeconds[type] = attainable;
            if (desired > capacity + Epsilon)
            {
                limited.Add(type);
            }
        }

        double unallocated = Math.Max(0, targetSeconds - effectiveSeconds.Values.Sum());
        double initialUnallocated = unallocated;
        unallocated = Distribute(
            unallocated,
            configured.Keys.Where(policy.MusicOrientedNormalTypes.Contains),
            configured,
            capacitySeconds,
            effectiveSeconds);
        unallocated = Distribute(
            unallocated,
            configured.Keys.Where(type => !policy.MusicOrientedNormalTypes.Contains(type)),
            configured,
            capacitySeconds,
            effectiveSeconds);

        var effectiveTargets = effectiveSeconds.ToDictionary(
            item => item.Key,
            item => targetSeconds > 0 ? item.Value / targetSeconds : 0,
            StringComparer.Ordinal);
        return new PlaylistTargetPlan(
            configured,
            effectiveTargets,
            capacitySeconds,
            limited.OrderBy(type => type, StringComparer.Ordinal).ToArray(),
            initialUnallocated - unallocated);
    }

    private static int GetPracticalAppearanceBudget(PlaylistPolicy policy)
    {
        if (policy.ExactAssetCooldown <= TimeSpan.Zero)
        {
            return 1;
        }

        return Math.Max(
            1,
            (int)Math.Ceiling(policy.TargetDuration.TotalSeconds / policy.ExactAssetCooldown.TotalSeconds));
    }

    private static double Distribute(
        double unallocated,
        IEnumerable<string> candidateTypes,
        IReadOnlyDictionary<string, double> configured,
        IReadOnlyDictionary<string, double> capacitySeconds,
        IDictionary<string, double> effectiveSeconds)
    {
        string[] orderedTypes = candidateTypes.OrderBy(type => type, StringComparer.Ordinal).ToArray();
        while (unallocated > Epsilon)
        {
            string[] recipients = orderedTypes
                .Where(type => capacitySeconds[type] - effectiveSeconds[type] > Epsilon)
                .ToArray();
            if (recipients.Length == 0)
            {
                break;
            }

            double totalWeight = recipients.Sum(type => configured[type]);
            double distributed = 0;
            foreach (string type in recipients)
            {
                double weight = totalWeight > 0 ? configured[type] / totalWeight : 1.0 / recipients.Length;
                double allocation = Math.Min(
                    unallocated * weight,
                    capacitySeconds[type] - effectiveSeconds[type]);
                effectiveSeconds[type] += allocation;
                distributed += allocation;
            }

            if (distributed <= Epsilon)
            {
                break;
            }

            unallocated -= distributed;
        }

        return Math.Max(0, unallocated);
    }
}
