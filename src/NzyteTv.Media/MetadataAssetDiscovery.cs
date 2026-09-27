namespace NzyteTv.Media;

public interface IMetadataAssetDiscovery
{
    IReadOnlyList<LibraryMediaFile> Discover(string sourceRoot, string libraryRoot);

    IReadOnlyList<string> DiscoverOrphanedMetadata(string sourceRoot, IReadOnlyCollection<string> sourcePaths);
}

public sealed class MetadataAssetDiscovery : IMetadataAssetDiscovery
{
    private readonly IMediaLibraryDiscovery _mediaDiscovery;

    public MetadataAssetDiscovery(IMediaLibraryDiscovery? mediaDiscovery = null)
    {
        _mediaDiscovery = mediaDiscovery ?? new MediaLibraryDiscovery();
    }

    public IReadOnlyList<LibraryMediaFile> Discover(string sourceRoot, string libraryRoot) =>
        _mediaDiscovery.Discover(sourceRoot, libraryRoot);

    public IReadOnlyList<string> DiscoverOrphanedMetadata(
        string sourceRoot,
        IReadOnlyCollection<string> sourcePaths)
    {
        string root = Path.GetFullPath(sourceRoot);
        var sources = new HashSet<string>(sourcePaths.Select(Path.GetFullPath), GetPathComparer());
        var orphans = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        while (pendingDirectories.TryPop(out string? directory))
        {
            foreach (string metadataPath in Directory.EnumerateFiles(
                directory,
                $"*{AssetMetadataStore.MetadataSuffix}",
                options))
            {
                string sourcePath = metadataPath[..^AssetMetadataStore.MetadataSuffix.Length];
                if (!sources.Contains(Path.GetFullPath(sourcePath)))
                {
                    orphans.Add(Path.GetFullPath(metadataPath));
                }
            }

            foreach (string child in Directory.EnumerateDirectories(directory, "*", options)
                .Where(path => !string.Equals(
                    Path.GetFileName(path),
                    "System Volume Information",
                    StringComparison.OrdinalIgnoreCase)))
            {
                pendingDirectories.Push(child);
            }
        }

        return orphans.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
