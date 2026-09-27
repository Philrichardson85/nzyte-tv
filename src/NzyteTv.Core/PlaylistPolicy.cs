namespace NzyteTv.Core;

public sealed record HotRotationBand(int MaximumAgeDays, double Weight);

public sealed record ProgramCountCadence(int MinimumPrograms, int MaximumPrograms);

public sealed record TimeCadence(TimeSpan MinimumInterval, TimeSpan MaximumInterval);

public sealed record PlaylistPolicy
{
    public TimeSpan TargetDuration { get; init; } = TimeSpan.FromHours(6);

    public IReadOnlyDictionary<string, double> CategoryAirtimeTargets { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [AssetTypes.MusicVideo] = 0.50,
            [AssetTypes.LyricVideo] = 0.25,
            [AssetTypes.Vlog] = 0.25,
        };

    public TimeSpan ExactAssetCooldown { get; init; } = TimeSpan.FromHours(2);

    public TimeSpan ContentGroupCooldown { get; init; } = TimeSpan.FromMinutes(90);

    public TimeSpan ContentGroupMinimumCooldown { get; init; } = TimeSpan.FromMinutes(60);

    public TimeSpan ContentGroupMusicFirstRescueCooldown { get; init; } = TimeSpan.FromMinutes(45);

    public bool AvoidConsecutiveVlogs { get; init; } = true;

    public int MaximumConsecutiveVlogs { get; init; } = 2;

    public IReadOnlyList<HotRotationBand> HotRotationBands { get; init; } =
    [
        new(7, 2.5),
        new(21, 2.0),
        new(45, 1.5),
    ];

    public double NormalRotationWeight { get; init; } = 1.0;

    public ProgramCountCadence? BumperCadence { get; init; } = new(4, 5);

    public TimeCadence? InterstitialCadence { get; init; } =
        new(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30));

    public TimeCadence? PromoCadence { get; init; } =
        new(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(45));

    public TimeSpan HistorySafetyMargin { get; init; } = TimeSpan.FromMinutes(15);

    public bool IsCategoryEnabled(string type) =>
        CategoryAirtimeTargets.TryGetValue(type, out double target) && target > 0
        || type == AssetTypes.Bumper && BumperCadence is not null
        || type == AssetTypes.Interstitial && InterstitialCadence is not null
        || type == AssetTypes.Promo && PromoCadence is not null;

    public double GetRotationWeight(DateOnly? rotationStartDate, DateOnly scheduleDate)
    {
        if (rotationStartDate is null)
        {
            return NormalRotationWeight;
        }

        int ageDays = scheduleDate.DayNumber - rotationStartDate.Value.DayNumber;
        if (ageDays < 0)
        {
            return NormalRotationWeight;
        }

        return HotRotationBands
            .OrderBy(band => band.MaximumAgeDays)
            .FirstOrDefault(band => ageDays <= band.MaximumAgeDays)?.Weight
            ?? NormalRotationWeight;
    }

    public void Validate()
    {
        var errors = new List<string>();
        if (TargetDuration <= TimeSpan.Zero)
        {
            errors.Add("Target duration must be positive.");
        }

        if (ExactAssetCooldown < TimeSpan.Zero
            || ContentGroupCooldown < TimeSpan.Zero
            || ContentGroupMinimumCooldown < TimeSpan.Zero
            || ContentGroupMusicFirstRescueCooldown < TimeSpan.Zero)
        {
            errors.Add("Cooldowns must not be negative.");
        }

        if (ContentGroupMinimumCooldown > ContentGroupCooldown)
        {
            errors.Add("The content-group minimum cooldown must not exceed the preferred cooldown.");
        }

        if (ContentGroupMusicFirstRescueCooldown > ContentGroupMinimumCooldown)
        {
            errors.Add("The content-group music-first rescue cooldown must not exceed the normal floor.");
        }

        if (MaximumConsecutiveVlogs < 1)
        {
            errors.Add("The maximum consecutive-vlog fallback must be at least one.");
        }

        if (CategoryAirtimeTargets.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value < 0))
        {
            errors.Add("Category airtime targets must contain non-empty types and non-negative weights.");
        }

        if (CategoryAirtimeTargets.Values.Sum() <= 0
            && BumperCadence is null
            && PromoCadence is null
            && InterstitialCadence is null)
        {
            errors.Add("At least one category airtime target or cadence must be configured.");
        }

        if (NormalRotationWeight <= 0
            || HotRotationBands.Any(band => band.MaximumAgeDays < 0 || band.Weight <= 0))
        {
            errors.Add("Rotation weights and age bands must be positive.");
        }

        ValidateCadence(BumperCadence, errors);
        ValidateCadence(PromoCadence, "Promo", errors);
        ValidateCadence(InterstitialCadence, "Interstitial", errors);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Playlist policy is invalid:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}");
        }
    }

    private static void ValidateCadence(ProgramCountCadence? cadence, ICollection<string> errors)
    {
        if (cadence is not null
            && (cadence.MinimumPrograms < 1 || cadence.MaximumPrograms < cadence.MinimumPrograms))
        {
            errors.Add("Bumper cadence program counts are invalid.");
        }
    }

    private static void ValidateCadence(TimeCadence? cadence, string name, ICollection<string> errors)
    {
        if (cadence is not null
            && (cadence.MinimumInterval <= TimeSpan.Zero
                || cadence.MaximumInterval < cadence.MinimumInterval))
        {
            errors.Add($"{name} cadence intervals are invalid.");
        }
    }
}
