namespace NzyteTv.Operations.Configuration;

public sealed class OperationsOptions
{
    public const string SectionName = "Operations";
    internal const string LinuxDefaultMediaRoot = "/srv/nzyte-tv/media";

    public string MediaRoot { get; set; } = OperatingSystem.IsWindows()
        ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "operations-media"))
        : LinuxDefaultMediaRoot;
    public string SocketPath { get; set; } = OperatingSystem.IsWindows()
        ? Path.GetFullPath(Path.Combine(Path.GetTempPath(), "nzyte-tv-operations.sock"))
        : "/run/nzyte-tv-operations/operations.sock";

    public static OperationsOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new OperationsOptions();
        configuration.GetSection(SectionName).Bind(options);
        options.MediaRoot = NormalizeAbsolute(options.MediaRoot, nameof(MediaRoot));
        options.SocketPath = NormalizeAbsolute(options.SocketPath, nameof(SocketPath));
        return options;
    }

    private static string NormalizeAbsolute(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new InvalidOperationException($"Operations option {name} must be an absolute path.");
        }
        return Path.GetFullPath(value);
    }
}
