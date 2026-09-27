namespace NzyteTv.Core;

public static class AssetCategoryMap
{
    private static readonly IReadOnlyDictionary<string, string> Mappings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Music Videos"] = AssetTypes.MusicVideo,
            ["Lyric Videos"] = AssetTypes.LyricVideo,
            ["Performance Videos"] = AssetTypes.Performance,
            ["Short Form"] = AssetTypes.ShortForm,
            ["Vlog Episodes"] = AssetTypes.Vlog,
            ["Bumpers"] = AssetTypes.Bumper,
            ["Promos"] = AssetTypes.Promo,
            ["Interstitials"] = AssetTypes.Interstitial,
            ["Advertisements"] = AssetTypes.Advertisement,
            ["Specials"] = AssetTypes.Special,
        };

    public static IReadOnlyDictionary<string, string> CategoryMappings => Mappings;

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
