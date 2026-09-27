using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed class MetadataEditor(IAssetMetadataStore metadataStore)
{
    public async Task<AssetMetadata> UpdateTypeAsync(
        string mediaPath,
        string type,
        string? subtype,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (!AssetTypes.Supported.Contains(type))
        {
            throw new InvalidOperationException(
                $"type must be one of: {string.Join(", ", AssetTypes.Supported)}.");
        }

        string fullMediaPath = Path.GetFullPath(mediaPath);
        if (!File.Exists(fullMediaPath))
        {
            throw new FileNotFoundException($"Source media file not found: {fullMediaPath}", fullMediaPath);
        }

        AssetMetadata existing = metadataStore.Read(fullMediaPath);
        string? effectiveSubtype = type == AssetTypes.ShortForm
            ? subtype ?? existing.Subtype
            : null;
        var updated = new AssetMetadata
        {
            SchemaVersion = existing.SchemaVersion,
            AssetId = existing.AssetId,
            ContentGroupId = AssetTypes.IsSongBased(type) ? existing.ContentGroupId : null,
            Title = existing.Title,
            Artist = existing.Artist,
            Type = type,
            Subtype = effectiveSubtype,
            RotationStartDate = existing.RotationStartDate,
            Enabled = existing.Enabled,
            SeriesId = existing.SeriesId,
            EpisodeNumber = existing.EpisodeNumber,
            Tags = existing.Tags is null ? null : [.. existing.Tags],
        };
        await metadataStore.WriteAsync(fullMediaPath, updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
