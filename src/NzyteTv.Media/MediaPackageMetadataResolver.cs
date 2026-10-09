using System.Text;
using System.Text.Json;
using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record StableSongCatalogSnapshot(
    SongCatalog Catalog,
    string Sha256,
    long Length,
    DateTime LastWriteTimeUtc)
{
    public static StableSongCatalogSnapshot Load(string catalogPath)
    {
        string path = Path.GetFullPath(catalogPath);
        var before = new FileInfo(path);
        if (!before.Exists) throw new FileNotFoundException("The song catalog is missing.", path);
        byte[] content = File.ReadAllBytes(path);
        var after = new FileInfo(path);
        if (!after.Exists || before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
        {
            throw new IOException("The song catalog changed while it was being read.");
        }

        SongCatalog catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<SongCatalog>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? throw new InvalidDataException("The song catalog is empty.");
            SongCatalogValidator.Validate(catalog);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The song catalog is malformed.", exception);
        }

        return new StableSongCatalogSnapshot(
            catalog,
            MediaPackageHash.Sha256(content),
            after.Length,
            after.LastWriteTimeUtc);
    }

    public void VerifyUnchanged(string catalogPath)
    {
        string path = Path.GetFullPath(catalogPath);
        var file = new FileInfo(path);
        if (!file.Exists
            || file.Length != Length
            || file.LastWriteTimeUtc != LastWriteTimeUtc
            || !string.Equals(MediaPackageHash.Sha256(File.ReadAllBytes(path)), Sha256, StringComparison.Ordinal))
        {
            throw new CatalogChangedDuringRefreshException();
        }
    }
}

public sealed class CatalogChangedDuringRefreshException : InvalidOperationException
{
    public CatalogChangedDuringRefreshException()
        : base("The song catalog changed during media metadata refresh.")
    {
    }
}

public sealed record ResolvedPackageMetadata(
    AssetMetadata Metadata,
    byte[] SourceJsonBytes,
    byte[] LibraryJsonBytes,
    AssetMetadataInventoryClassification Classification);

public sealed class MediaPackageMetadataResolutionException(
    MediaLibraryRefreshIssueCode code) : InvalidOperationException("Package programming metadata could not be resolved.")
{
    public MediaLibraryRefreshIssueCode Code { get; } = code;
}

public sealed class MediaPackageMetadataResolver
{
    public ResolvedPackageMetadata Resolve(
        VerifiedReadyMediaPackage package,
        string sourceRoot,
        SongCatalog catalog,
        ISet<string> reservedAssetIds)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(reservedAssetIds);
        string sourceRelative = package.Manifest.SourceRelativePath!;
        string sourcePath = MetadataPathSafety.ResolveRelativeMediaPath(sourceRoot, sourceRelative);
        if (!AssetCategoryMap.TryDetect(sourceRoot, sourcePath, out string? detectedType))
        {
            throw new MediaPackageMetadataResolutionException(
                MediaLibraryRefreshIssueCode.InvalidProgrammingMetadata);
        }

        AssetMetadata metadata;
        byte[] preferredBytes;
        bool assetIdReservedByGenerator = false;
        if (package.SourceMetadata is not null)
        {
            metadata = ResolveExisting(package.SourceMetadata.Metadata, sourcePath, catalog);
            preferredBytes = SemanticallyEqual(metadata, package.SourceMetadata.Metadata)
                ? package.SourceMetadata.JsonBytes
                : Encoding.UTF8.GetBytes(AssetMetadataStore.Serialize(metadata));
        }
        else
        {
            ShortFormDescriptor? descriptor = ShortFormDescriptorDetector.Detect(sourcePath);
            SongMatchResult? match = AssetTypes.IsSongBased(detectedType)
                ? SongMatcher.Match(sourcePath, catalog)
                : null;
            if (match?.Status == SongMatchStatus.Ambiguous)
            {
                throw new MediaPackageMetadataResolutionException(
                    MediaLibraryRefreshIssueCode.AmbiguousCatalogMatch);
            }

            if (match?.Status == SongMatchStatus.Unresolved)
            {
                throw new MediaPackageMetadataResolutionException(
                    MediaLibraryRefreshIssueCode.CatalogMatchRequired);
            }

            SongCatalogEntry? song = match?.Match;
            string assetId = AssetIdGenerator.Generate(
                sourcePath,
                sourceRelative,
                detectedType!,
                song,
                reservedAssetIds,
                descriptor);
            metadata = new AssetMetadata
            {
                AssetId = assetId,
                ContentGroupId = song?.ContentGroupId,
                Title = song?.Title ?? Path.GetFileNameWithoutExtension(sourcePath),
                Artist = song?.Artist,
                Type = detectedType,
                Subtype = descriptor?.Subtype,
                Enabled = true,
                Tags = [],
            };
            assetIdReservedByGenerator = true;
            preferredBytes = Encoding.UTF8.GetBytes(AssetMetadataStore.Serialize(metadata));
        }

        AssetMetadataValidator.ValidateStructure(metadata);
        if (!string.Equals(metadata.Type, detectedType, StringComparison.Ordinal))
        {
            throw new MediaPackageMetadataResolutionException(
                MediaLibraryRefreshIssueCode.InvalidProgrammingMetadata);
        }

        if (!assetIdReservedByGenerator && !reservedAssetIds.Add(metadata.AssetId!))
        {
            throw new MediaPackageMetadataResolutionException(MediaLibraryRefreshIssueCode.DuplicateAssetId);
        }

        AssetMetadata? resolvedLibrary = package.LibraryMetadata is null
            ? null
            : ResolveExisting(package.LibraryMetadata.Metadata, sourcePath, catalog);
        if (resolvedLibrary is not null && !SemanticallyEqual(metadata, resolvedLibrary))
        {
            reservedAssetIds.Remove(metadata.AssetId!);
            throw new MediaPackageMetadataResolutionException(
                MediaLibraryRefreshIssueCode.ConflictingProgrammingMetadata);
        }

        return new ResolvedPackageMetadata(
            metadata,
            preferredBytes,
            preferredBytes,
            metadata.Enabled
                ? AssetMetadataInventoryClassification.PlaylistEligible
                : AssetMetadataInventoryClassification.Disabled);
    }

    private static AssetMetadata ResolveExisting(
        AssetMetadata metadata,
        string sourcePath,
        SongCatalog catalog)
    {
        AssetMetadataValidator.ValidateStructure(metadata);
        if (!AssetTypes.IsSongBased(metadata.Type)) return metadata;
        if (!string.IsNullOrWhiteSpace(metadata.ContentGroupId))
        {
            SongCatalogEntry? song = catalog.FindByContentGroupId(metadata.ContentGroupId);
            if (song is null)
            {
                throw new MediaPackageMetadataResolutionException(
                    MediaLibraryRefreshIssueCode.CatalogMatchRequired);
            }

            return metadata.WithContentGroup(song);
        }

        SongMatchResult match = SongMatcher.Match(sourcePath, catalog);
        return match.Status switch
        {
            SongMatchStatus.Matched => metadata.WithContentGroup(match.Match!),
            SongMatchStatus.Ambiguous => throw new MediaPackageMetadataResolutionException(
                MediaLibraryRefreshIssueCode.AmbiguousCatalogMatch),
            _ => throw new MediaPackageMetadataResolutionException(
                MediaLibraryRefreshIssueCode.CatalogMatchRequired),
        };
    }

    private static bool SemanticallyEqual(AssetMetadata left, AssetMetadata right) =>
        string.Equals(
            AssetMetadataStore.Serialize(left),
            AssetMetadataStore.Serialize(right),
            StringComparison.Ordinal);
}
