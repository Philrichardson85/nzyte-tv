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

    public MediaLibraryOperationsOptions MediaLibrary { get; set; } = new();

    public static OperationsOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new OperationsOptions();
        configuration.GetSection(SectionName).Bind(options);
        options.MediaRoot = NormalizeAbsolute(options.MediaRoot, nameof(MediaRoot));
        options.SocketPath = NormalizeAbsolute(options.SocketPath, nameof(SocketPath));
        options.MediaLibrary ??= new MediaLibraryOperationsOptions();
        options.MediaLibrary.NormalizeConfiguredPaths();
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

public sealed class MediaLibraryOperationsOptions
{
    public bool Enabled { get; set; }

    public string? MediaRoot { get; set; }

    public string? MetadataRoot { get; set; }

    public string? InboxRoot { get; set; }

    internal void NormalizeConfiguredPaths()
    {
        MediaRoot = NormalizeOptional(MediaRoot);
        MetadataRoot = NormalizeOptional(MetadataRoot);
        InboxRoot = NormalizeOptional(InboxRoot);
    }

    public bool HasCompleteConfiguration =>
        Path.IsPathFullyQualified(MediaRoot ?? string.Empty)
        && Path.IsPathFullyQualified(MetadataRoot ?? string.Empty)
        && (InboxRoot is null || Path.IsPathFullyQualified(InboxRoot));

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : value;
}
