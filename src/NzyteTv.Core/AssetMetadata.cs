using System.Text.Json.Serialization;

namespace NzyteTv.Core;

public static class AssetTypes
{
    public const string MusicVideo = "music-video";
    public const string LyricVideo = "lyric-video";
    public const string Performance = "performance";
    public const string Visualizer = "visualizer";
    public const string AnimatedVisual = "animated-visual";
    public const string ShortForm = "short-form";
    public const string Vlog = "vlog";
    public const string Bumper = "bumper";
    public const string Promo = "promo";
    public const string Interstitial = "interstitial";
    public const string Advertisement = "advertisement";
    public const string Special = "special";

    public static IReadOnlySet<string> Supported { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        MusicVideo,
        LyricVideo,
        Performance,
        Visualizer,
        AnimatedVisual,
        ShortForm,
        Vlog,
        Bumper,
        Promo,
        Interstitial,
        Advertisement,
        Special,
    };

    public static bool IsSongBased(string? type) => type is
        MusicVideo or
        LyricVideo or
        Performance or
        Visualizer or
        AnimatedVisual or
        ShortForm;
}

public sealed class AssetMetadata
{
    public const int CurrentSchemaVersion = 1;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? AssetId { get; init; }

    public string? ContentGroupId { get; init; }

    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? Type { get; init; }

    public string? Subtype { get; init; }

    public DateOnly? RotationStartDate { get; init; }

    public bool Enabled { get; init; } = true;

    public string? SeriesId { get; init; }

    public int? EpisodeNumber { get; init; }

    public List<string>? Tags { get; init; } = [];

    public AssetMetadata WithContentGroup(SongCatalogEntry song) => new()
    {
        SchemaVersion = SchemaVersion,
        AssetId = AssetId,
        ContentGroupId = song.ContentGroupId,
        Title = song.Title,
        Artist = song.Artist,
        Type = Type,
        Subtype = Subtype,
        RotationStartDate = RotationStartDate,
        Enabled = Enabled,
        SeriesId = SeriesId,
        EpisodeNumber = EpisodeNumber,
        Tags = Tags is null ? null : [.. Tags],
    };
}

public sealed class AssetMetadataValidationException : InvalidOperationException
{
    public AssetMetadataValidationException(IEnumerable<string> errors)
        : base($"Asset metadata validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}")
    {
    }
}

public static class AssetMetadataValidator
{
    public static IReadOnlyList<string> GetStructuralErrors(AssetMetadata? metadata)
    {
        if (metadata is null)
        {
            return ["The metadata document is empty."];
        }

        var errors = new List<string>();
        if (metadata.SchemaVersion != AssetMetadata.CurrentSchemaVersion)
        {
            errors.Add(
                $"Unsupported schemaVersion {metadata.SchemaVersion}; expected {AssetMetadata.CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(metadata.AssetId))
        {
            errors.Add("assetId is required.");
        }

        if (string.IsNullOrWhiteSpace(metadata.Title))
        {
            errors.Add("title is required.");
        }

        if (string.IsNullOrWhiteSpace(metadata.Type) || !AssetTypes.Supported.Contains(metadata.Type))
        {
            errors.Add($"type must be one of: {string.Join(", ", AssetTypes.Supported)}.");
        }

        if (metadata.Subtype is not null && string.IsNullOrWhiteSpace(metadata.Subtype))
        {
            errors.Add("subtype must be null or a non-empty extensible value.");
        }

        if (metadata.Tags is null)
        {
            errors.Add("tags must be an array.");
        }
        else if (metadata.Tags.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("tags must not contain empty values.");
        }

        if (!AssetTypes.IsSongBased(metadata.Type) && !string.IsNullOrWhiteSpace(metadata.ContentGroupId))
        {
            errors.Add("Non-song programming must not define contentGroupId.");
        }

        return errors;
    }

    public static void ValidateStructure(AssetMetadata? metadata)
    {
        IReadOnlyList<string> errors = GetStructuralErrors(metadata);
        if (errors.Count > 0)
        {
            throw new AssetMetadataValidationException(errors);
        }
    }

    public static bool HasValidSongRelationship(AssetMetadata metadata, SongCatalog catalog) =>
        !AssetTypes.IsSongBased(metadata.Type)
        || (!string.IsNullOrWhiteSpace(metadata.ContentGroupId)
            && catalog.FindByContentGroupId(metadata.ContentGroupId) is not null);
}
