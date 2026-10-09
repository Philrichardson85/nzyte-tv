using System.Text.Json;
using System.Text.Json.Serialization;

namespace NzyteTv.Media;

[JsonConverter(typeof(JsonStringEnumConverter<AssetMetadataInventoryClassification>))]
public enum AssetMetadataInventoryClassification
{
    PlaylistEligible,
    Disabled,
}

public sealed record AssetMetadataInventoryPackage
{
    public string? PackageId { get; init; }

    public string? ManifestDigest { get; init; }

    public string? SourceRelativePath { get; init; }

    public string? LibraryRelativePath { get; init; }

    public string? SourceSha256 { get; init; }

    public string? LibrarySha256 { get; init; }

    public string? TechnicalManifestSha256 { get; init; }

    public string? SourceProgrammingMetadataSha256 { get; init; }

    public string? LibraryProgrammingMetadataSha256 { get; init; }
}

public sealed record AssetMetadataInventoryAsset
{
    public string? PackageId { get; init; }

    public string? SourceRelativePath { get; init; }

    public string? LibraryRelativePath { get; init; }

    public string? AssetId { get; init; }

    public string? ContentGroupId { get; init; }

    public string? TechnicalManifestSha256 { get; init; }

    public string? SourceMetadataSha256 { get; init; }

    public string? LibraryMetadataSha256 { get; init; }

    [JsonRequired]
    public AssetMetadataInventoryClassification Classification { get; init; }
}

public sealed record AssetMetadataGenerationInventory
{
    public const int CurrentSchemaVersion = 1;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonRequired]
    public long Revision { get; init; }

    public string? GenerationId { get; init; }

    public IReadOnlyList<AssetMetadataInventoryPackage>? Packages { get; init; } = [];

    public IReadOnlyList<AssetMetadataInventoryAsset>? Assets { get; init; } = [];
}

public static class AssetMetadataGenerationInventorySerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static string Serialize(AssetMetadataGenerationInventory inventory)
    {
        AssetMetadataGenerationInventory normalized = NormalizeAndValidate(inventory);
        return JsonSerializer.Serialize(normalized, JsonOptions) + Environment.NewLine;
    }

    public static AssetMetadataGenerationInventory Deserialize(ReadOnlySpan<byte> content)
    {
        AssetMetadataGenerationInventory inventory = StrictJson.Deserialize<AssetMetadataGenerationInventory>(
            content,
            JsonOptions,
            "metadata generation inventory");
        return NormalizeAndValidate(inventory);
    }

    public static AssetMetadataGenerationInventory NormalizeAndValidate(
        AssetMetadataGenerationInventory? inventory)
    {
        if (inventory is null
            || inventory.SchemaVersion != AssetMetadataGenerationInventory.CurrentSchemaVersion
            || inventory.Revision <= 0
            || !IsGenerationId(inventory.GenerationId)
            || inventory.Packages is null
            || inventory.Assets is null)
        {
            throw new InvalidDataException("The metadata generation inventory is invalid.");
        }

        AssetMetadataInventoryPackage[] packages = inventory.Packages
            .OrderBy(value => value.PackageId, StringComparer.Ordinal)
            .ToArray();
        AssetMetadataInventoryAsset[] assets = inventory.Assets
            .OrderBy(value => value.LibraryRelativePath, StringComparer.Ordinal)
            .ThenBy(value => value.SourceRelativePath, StringComparer.Ordinal)
            .ToArray();

        var packageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (AssetMetadataInventoryPackage package in packages)
        {
            if (!ReadyMediaPackageValidator.IsPackageId(package.PackageId)
                || !packageIds.Add(package.PackageId!)
                || !ReadyMediaPackageValidator.IsSha256(package.ManifestDigest)
                || !IsSafeRelative(package.SourceRelativePath)
                || !IsSafeRelative(package.LibraryRelativePath)
                || !ReadyMediaPackageValidator.IsSha256(package.SourceSha256)
                || !ReadyMediaPackageValidator.IsSha256(package.LibrarySha256)
                || !ReadyMediaPackageValidator.IsSha256(package.TechnicalManifestSha256)
                || !IsOptionalSha256(package.SourceProgrammingMetadataSha256)
                || !IsOptionalSha256(package.LibraryProgrammingMetadataSha256)
                || !PathsMatch(package.SourceRelativePath!, package.LibraryRelativePath!))
            {
                throw new InvalidDataException("The metadata generation package inventory is invalid.");
            }
        }

        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var libraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (AssetMetadataInventoryAsset asset in assets)
        {
            if (!IsSafeRelative(asset.SourceRelativePath)
                || !sourcePaths.Add(asset.SourceRelativePath!)
                || !IsSafeRelative(asset.LibraryRelativePath)
                || !libraryPaths.Add(asset.LibraryRelativePath!)
                || string.IsNullOrWhiteSpace(asset.AssetId)
                || !assetIds.Add(asset.AssetId)
                || !PathsMatch(asset.SourceRelativePath!, asset.LibraryRelativePath!)
                || !ReadyMediaPackageValidator.IsSha256(asset.TechnicalManifestSha256)
                || !ReadyMediaPackageValidator.IsSha256(asset.SourceMetadataSha256)
                || !ReadyMediaPackageValidator.IsSha256(asset.LibraryMetadataSha256)
                || !Enum.IsDefined(asset.Classification)
                || (asset.PackageId is not null && !packageIds.Contains(asset.PackageId)))
            {
                throw new InvalidDataException("The metadata generation asset inventory is invalid.");
            }
        }

        foreach (AssetMetadataInventoryPackage package in packages)
        {
            AssetMetadataInventoryAsset[] matches = assets.Where(value =>
                    string.Equals(value.PackageId, package.PackageId, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length != 1
                || !string.Equals(
                    matches[0].SourceRelativePath,
                    package.SourceRelativePath,
                    StringComparison.Ordinal)
                || !string.Equals(
                    matches[0].LibraryRelativePath,
                    package.LibraryRelativePath,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A metadata generation package has no matching asset inventory record.");
            }
        }

        return inventory with
        {
            Packages = packages,
            Assets = assets,
        };
    }

    private static bool IsSafeRelative(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            return string.Equals(
                MetadataPathSafety.NormalizeRelativeMediaPath(value),
                value,
                StringComparison.Ordinal);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsOptionalSha256(string? value) =>
        value is null || ReadyMediaPackageValidator.IsSha256(value);

    private static bool PathsMatch(string source, string library) => string.Equals(
        Path.ChangeExtension(source, ".mp4").Replace('\\', '/'),
        library,
        StringComparison.OrdinalIgnoreCase);

    private static bool IsGenerationId(string? value) => value is { Length: 12 }
        && value.All(character => character is >= '0' and <= '9')
        && value.Any(character => character != '0');
}
