namespace NzyteTv.Core;

public enum AssetEncodingStatus
{
    Ready,
    Missing,
}

public enum AssetMetadataStatus
{
    Resolved,
    Unresolved,
    Invalid,
    Missing,
}

public sealed record AssetEligibilityResult(
    AssetEncodingStatus EncodingStatus,
    AssetMetadataStatus MetadataStatus,
    bool IsPlaylistEligible,
    IReadOnlyList<string> Reasons);

public static class AssetEligibilityEvaluator
{
    public static AssetEligibilityResult Evaluate(
        AssetMetadata? metadata,
        SongCatalog catalog,
        bool normalizedLibraryFileExists,
        bool technicalManifestExists,
        bool programmingMetadataExists)
    {
        var reasons = new List<string>();
        AssetEncodingStatus encodingStatus = normalizedLibraryFileExists && technicalManifestExists
            ? AssetEncodingStatus.Ready
            : AssetEncodingStatus.Missing;
        if (!normalizedLibraryFileExists)
        {
            reasons.Add("Normalized library file is missing.");
        }

        if (!technicalManifestExists)
        {
            reasons.Add("Technical normalization manifest is missing.");
        }

        AssetMetadataStatus metadataStatus;
        if (metadata is null || !programmingMetadataExists)
        {
            metadataStatus = AssetMetadataStatus.Missing;
            reasons.Add("Programming metadata is missing from the library.");
        }
        else if (AssetMetadataValidator.GetStructuralErrors(metadata).Count > 0)
        {
            metadataStatus = AssetMetadataStatus.Invalid;
            reasons.Add("Programming metadata is invalid.");
        }
        else if (AssetTypes.IsSongBased(metadata.Type)
            && string.IsNullOrWhiteSpace(metadata.ContentGroupId))
        {
            metadataStatus = AssetMetadataStatus.Unresolved;
            reasons.Add("Song relationship is unresolved.");
        }
        else if (!AssetMetadataValidator.HasValidSongRelationship(metadata, catalog))
        {
            metadataStatus = AssetMetadataStatus.Invalid;
            reasons.Add("Referenced contentGroupId does not exist in the song catalog.");
        }
        else
        {
            metadataStatus = AssetMetadataStatus.Resolved;
        }

        if (metadata is not null && !metadata.Enabled)
        {
            reasons.Add("Asset is disabled.");
        }

        bool eligible = encodingStatus == AssetEncodingStatus.Ready
            && metadataStatus == AssetMetadataStatus.Resolved
            && metadata?.Enabled == true;
        return new AssetEligibilityResult(encodingStatus, metadataStatus, eligible, reasons);
    }
}
