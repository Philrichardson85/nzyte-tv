using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record LibraryMediaFile(string SourcePath, string DestinationPath);

public interface IMediaLibraryDiscovery
{
    IReadOnlyList<LibraryMediaFile> Discover(string sourceRoot, string destinationRoot);
}

public sealed class MediaLibraryDiscovery : IMediaLibraryDiscovery
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".mkv",
    };

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information",
    };

    public IReadOnlyList<LibraryMediaFile> Discover(string sourceRoot, string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        string source = Path.GetFullPath(sourceRoot);
        string destination = Path.GetFullPath(destinationRoot);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Source library root not found: {source}");
        }

        LibraryPathPolicy.EnsureRootsDoNotOverlap(source, destination);

        var discovered = new List<LibraryMediaFile>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(source);

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        while (pendingDirectories.TryPop(out string? directory))
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", options))
            {
                if (!SupportedExtensions.Contains(Path.GetExtension(file)))
                {
                    continue;
                }

                discovered.Add(new LibraryMediaFile(
                    Path.GetFullPath(file),
                    LibraryPathPolicy.GetDestinationPath(source, destination, file)));
            }

            foreach (string child in Directory.EnumerateDirectories(directory, "*", options)
                .Where(path => !IgnoredDirectories.Contains(Path.GetFileName(path))))
            {
                pendingDirectories.Push(child);
            }
        }

        return discovered
            .OrderBy(file => file.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
