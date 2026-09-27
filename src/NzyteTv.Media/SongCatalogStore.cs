using System.Text;
using System.Text.Json;
using NzyteTv.Core;

namespace NzyteTv.Media;

public interface ISongCatalogStore
{
    SongCatalog Load(string catalogPath);
}

public interface ISongCatalogWriter
{
    Task WriteNewAsync(string catalogPath, SongCatalog catalog, CancellationToken cancellationToken);
}

public sealed class SongCatalogStore : ISongCatalogStore, ISongCatalogWriter
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
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
            SongCatalog? catalog = JsonSerializer.Deserialize<SongCatalog>(File.ReadAllText(path), ReadOptions);
            SongCatalogValidator.Validate(catalog);
            return catalog!;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Malformed song catalog JSON in '{path}': {exception.Message}", exception);
        }
    }

    public async Task WriteNewAsync(
        string catalogPath,
        SongCatalog catalog,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        SongCatalogValidator.Validate(catalog);
        string path = Path.GetFullPath(catalogPath);
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"Song catalog already exists and will not be overwritten: {path}");
        }

        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The song catalog path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.partial");
        try
        {
            string json = JsonSerializer.Serialize(catalog, WriteOptions) + Environment.NewLine;
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
