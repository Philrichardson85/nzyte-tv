namespace NzyteTv.Dashboard.Configuration;

public sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";

    public int Port { get; set; } = 5080;

    public int BrowserRefreshSeconds { get; set; } = 5;

    public int StatusRefreshSeconds { get; set; } = 5;

    public string StationStatePath { get; set; } = DefaultStatePath("state.json");

    public string RollingStatePath { get; set; } = DefaultStatePath("rolling-state.json");

    public string ReplenishmentStatePath { get; set; } =
        DefaultStatePath("rolling-state.json.replenishment.json");

    public static DashboardOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new DashboardOptions();
        configuration.GetSection(SectionName).Bind(options);
        options.ValidateAndNormalize();
        return options;
    }

    private void ValidateAndNormalize()
    {
        if (Port is < 1024 or > 65535)
        {
            throw new InvalidOperationException("Dashboard port must be between 1024 and 65535.");
        }

        if (BrowserRefreshSeconds is < 1 or > 60)
        {
            throw new InvalidOperationException(
                "Dashboard browser refresh interval must be between 1 and 60 seconds.");
        }

        if (StatusRefreshSeconds is < 1 or > 60)
        {
            throw new InvalidOperationException(
                "Dashboard status refresh interval must be between 1 and 60 seconds.");
        }

        StationStatePath = NormalizePath(StationStatePath, nameof(StationStatePath));
        RollingStatePath = NormalizePath(RollingStatePath, nameof(RollingStatePath));
        ReplenishmentStatePath = NormalizePath(
            ReplenishmentStatePath,
            nameof(ReplenishmentStatePath));

        var paths = new HashSet<string>(GetPathComparer())
        {
            StationStatePath,
            RollingStatePath,
            ReplenishmentStatePath,
        };
        if (paths.Count != 3)
        {
            throw new InvalidOperationException("Dashboard state paths must be distinct.");
        }
    }

    private static string NormalizePath(string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new InvalidOperationException(
                $"Dashboard option {propertyName} must be an absolute path.");
        }

        return Path.GetFullPath(value);
    }

    private static string DefaultStatePath(string fileName) => OperatingSystem.IsWindows()
        ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "dashboard-state", fileName))
        : Path.Combine("/var/lib/nzyte-tv", fileName);

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
