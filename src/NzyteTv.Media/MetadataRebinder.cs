namespace NzyteTv.Media;

public sealed class MetadataRebinder(IAssetMetadataStore metadataStore)
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".mkv",
    };

    public void Rebind(string oldSourcePath, string newSourcePath)
    {
        string oldSource = Path.GetFullPath(oldSourcePath);
        string newSource = Path.GetFullPath(newSourcePath);
        if (string.Equals(oldSource, newSource, GetPathComparison()))
        {
            throw new InvalidOperationException("Old and new source paths must be different.");
        }

        if (!SupportedExtensions.Contains(Path.GetExtension(newSource)))
        {
            throw new InvalidOperationException("The new source must use a supported media extension (.mp4, .mov, or .mkv).");
        }

        metadataStore.Rebind(oldSource, newSource);
    }

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
