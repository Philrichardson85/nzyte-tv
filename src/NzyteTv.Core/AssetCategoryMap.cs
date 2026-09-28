namespace NzyteTv.Core;

public static class AssetCategoryMap
{
    private sealed record CategoryDefinition(
        string DirectoryName,
        string AssetType,
        bool ProvisionInPortableMediaRoot);

    private static readonly IReadOnlyList<CategoryDefinition> Definitions =
    [
        new("Music Videos", AssetTypes.MusicVideo, true),
        new("Lyric Videos", AssetTypes.LyricVideo, true),
        new("Performance Videos", AssetTypes.Performance, true),
        new("Visualizers", AssetTypes.Visualizer, true),
        new("Animated Visuals", AssetTypes.AnimatedVisual, true),
        new("Short Form", AssetTypes.ShortForm, false),
        new("Vlog Episodes", AssetTypes.Vlog, true),
        new("Bumpers", AssetTypes.Bumper, true),
        new("Promos", AssetTypes.Promo, true),
        new("Interstitials", AssetTypes.Interstitial, true),
        new("Advertisements", AssetTypes.Advertisement, true),
        new("Specials", AssetTypes.Special, true),
    ];

    private static readonly IReadOnlyDictionary<string, string> Mappings =
        Definitions.ToDictionary(
            definition => definition.DirectoryName,
            definition => definition.AssetType,
            StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<string> ProvisionedDirectories = Definitions
        .Where(definition => definition.ProvisionInPortableMediaRoot)
        .Select(definition => definition.DirectoryName)
        .ToArray();

    public static IReadOnlyDictionary<string, string> CategoryMappings => Mappings;

    public static IReadOnlyList<string> DirectoryBackedSourceCategories => ProvisionedDirectories;

    public static bool TryDetect(string sourceRoot, string sourcePath, out string? type)
    {
        type = null;
        string relative = Path.GetRelativePath(Path.GetFullPath(sourceRoot), Path.GetFullPath(sourcePath));
        string? category = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return category is not null && Mappings.TryGetValue(category, out type);
    }
}
