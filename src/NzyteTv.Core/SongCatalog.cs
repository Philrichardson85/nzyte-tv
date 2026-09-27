using System.Text.Json.Serialization;

namespace NzyteTv.Core;

public sealed class SongCatalog
{
    public const int CurrentSchemaVersion = 1;

    [JsonRequired]
    public int SchemaVersion { get; init; }

    public List<SongCatalogEntry>? Songs { get; init; }

    public SongCatalogEntry? FindByContentGroupId(string? contentGroupId) => Songs?.FirstOrDefault(song =>
        string.Equals(song.ContentGroupId, contentGroupId, StringComparison.Ordinal));
}

public sealed class SongCatalogEntry
{
    public string? ContentGroupId { get; init; }

    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? Project { get; init; }

    public List<string>? Aliases { get; init; }
}

public sealed class CatalogValidationException : InvalidOperationException
{
    public CatalogValidationException(IEnumerable<string> errors)
        : base($"Song catalog validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}")
    {
    }
}

public static class SongCatalogValidator
{
    public static void Validate(SongCatalog? catalog)
    {
        if (catalog is null)
        {
            throw new CatalogValidationException(["The catalog document is empty."]);
        }

        var errors = new List<string>();
        if (catalog.SchemaVersion != SongCatalog.CurrentSchemaVersion)
        {
            errors.Add(
                $"Unsupported schemaVersion {catalog.SchemaVersion}; expected {SongCatalog.CurrentSchemaVersion}.");
        }

        if (catalog.Songs is null)
        {
            errors.Add("songs is required.");
        }
        else
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < catalog.Songs.Count; index++)
            {
                SongCatalogEntry? song = catalog.Songs[index];
                string location = $"songs[{index}]";
                if (song is null)
                {
                    errors.Add($"{location} must not be null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(song.ContentGroupId))
                {
                    errors.Add($"{location}.contentGroupId is required.");
                }
                else if (!ids.Add(song.ContentGroupId))
                {
                    errors.Add($"Duplicate contentGroupId '{song.ContentGroupId}'.");
                }

                if (string.IsNullOrWhiteSpace(song.Title))
                {
                    errors.Add($"{location}.title is required.");
                }

                if (string.IsNullOrWhiteSpace(song.Artist))
                {
                    errors.Add($"{location}.artist is required.");
                }

                if (song.Aliases?.Any(string.IsNullOrWhiteSpace) == true)
                {
                    errors.Add($"{location}.aliases must not contain empty values.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new CatalogValidationException(errors);
        }
    }
}
