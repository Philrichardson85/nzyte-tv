using System.Text.Json;
using NzyteTv.Core;

namespace NzyteTv.Media;

public interface ISongCatalogStore
{
    SongCatalog Load(string catalogPath);
}

public sealed class SongCatalogStore : ISongCatalogStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public SongCatalog Load(string catalogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        string path = Path.GetFullPath(catalogPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Song catalog not found: {path}", path);
        }

        try
        {
            SongCatalog? catalog = JsonSerializer.Deserialize<SongCatalog>(File.ReadAllText(path), SerializerOptions);
            SongCatalogValidator.Validate(catalog);
            return catalog!;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Malformed song catalog JSON in '{path}': {exception.Message}", exception);
        }
    }
}
