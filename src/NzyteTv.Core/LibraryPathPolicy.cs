namespace NzyteTv.Core;

public static class LibraryPathPolicy
{
    public static void EnsureRootsDoNotOverlap(string sourceRoot, string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        string source = NormalizeRoot(sourceRoot);
        string destination = NormalizeRoot(destinationRoot);

        if (IsSameOrDescendant(source, destination) || IsSameOrDescendant(destination, source))
        {
            throw new InvalidOperationException(
                "Source and destination library roots must not be the same or nested inside one another.");
        }
    }

    public static string GetDestinationPath(string sourceRoot, string destinationRoot, string sourceFile)
    {
        string source = NormalizeRoot(sourceRoot);
        string destination = NormalizeRoot(destinationRoot);
        string file = Path.GetFullPath(sourceFile);
        string relativePath = Path.GetRelativePath(source, file);

        if (Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", GetPathComparison())
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", GetPathComparison()))
        {
            throw new InvalidOperationException($"Source file is outside the source root: {file}");
        }

        return Path.GetFullPath(Path.Combine(destination, Path.ChangeExtension(relativePath, ".mp4")));
    }

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        string relativePath = Path.GetRelativePath(root, candidate);
        if (relativePath == ".")
        {
            return true;
        }

        return !Path.IsPathRooted(relativePath)
            && relativePath != ".."
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", GetPathComparison())
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", GetPathComparison());
    }

    private static string NormalizeRoot(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
