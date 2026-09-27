using System.Text;
using System.Text.Json;
using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record MediaRootInitializationResult(
    string MediaRoot,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Existing)
{
    public int FilesOverwritten => 0;
}

public interface IMediaRootInitializer
{
    Task<MediaRootInitializationResult> InitializeAsync(
        string mediaRoot,
        CancellationToken cancellationToken);
}

public sealed class MediaRootInitializer(ISongCatalogWriter? catalogWriter = null) : IMediaRootInitializer
{
    public const string DescriptorFileName = ".nzytetv-media-root.json";
    public const string CatalogRelativePath = "catalog/song-catalog.json";

    private static readonly JsonSerializerOptions DescriptorReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions DescriptorWriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ISongCatalogWriter _catalogWriter = catalogWriter ?? new SongCatalogStore();

    public async Task<MediaRootInitializationResult> InitializeAsync(
        string mediaRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaRoot);
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(mediaRoot);
        if (File.Exists(root))
        {
            throw new InvalidOperationException($"Media root is a file, not a directory: {root}");
        }

        string descriptorPath = Path.Combine(root, DescriptorFileName);
        bool descriptorExists = File.Exists(descriptorPath);
        if (Directory.Exists(descriptorPath))
        {
            throw new InvalidDataException($"Media-root descriptor path is a directory: {descriptorPath}");
        }

        if (descriptorExists)
        {
            ValidateDescriptor(descriptorPath);
        }

        Directory.CreateDirectory(root);
        var created = new List<string>();
        var existing = new List<string>();
        foreach (string relativeDirectory in GetRequiredDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directoryPath = CombineRelativePath(root, relativeDirectory);
            if (Directory.Exists(directoryPath))
            {
                existing.Add(FormatDirectory(relativeDirectory));
                continue;
            }

            Directory.CreateDirectory(directoryPath);
            created.Add(FormatDirectory(relativeDirectory));
        }

        if (descriptorExists)
        {
            existing.Add(DescriptorFileName);
        }
        else
        {
            await WriteDescriptorNewAsync(descriptorPath, cancellationToken).ConfigureAwait(false);
            created.Add(DescriptorFileName);
        }

        string catalogPath = CombineRelativePath(root, CatalogRelativePath);
        if (Directory.Exists(catalogPath))
        {
            throw new InvalidDataException($"Song catalog path is a directory: {catalogPath}");
        }

        if (File.Exists(catalogPath))
        {
            existing.Add(CatalogRelativePath);
        }
        else
        {
            await _catalogWriter.WriteNewAsync(
                catalogPath,
                new SongCatalog
                {
                    SchemaVersion = SongCatalog.CurrentSchemaVersion,
                    Songs = [],
                },
                cancellationToken).ConfigureAwait(false);
            created.Add(CatalogRelativePath);
        }

        return new MediaRootInitializationResult(root, created, existing);
    }

    private static IReadOnlyList<string> GetRequiredDirectories() =>
    [
        "source",
        .. AssetCategoryMap.DirectoryBackedSourceCategories.Select(category => $"source/{category}"),
        "library",
        "catalog",
        "playlists",
        "work",
    ];

    private static string CombineRelativePath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string FormatDirectory(string relativePath) => $"{relativePath}/";

    private static void ValidateDescriptor(string descriptorPath)
    {
        try
        {
            MediaRootDescriptor? descriptor = JsonSerializer.Deserialize<MediaRootDescriptor>(
                File.ReadAllText(descriptorPath),
                DescriptorReadOptions);
            if (descriptor is null)
            {
                throw new InvalidDataException($"Media-root descriptor is empty: {descriptorPath}");
            }

            if (descriptor.SchemaVersion != MediaRootDescriptor.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported media-root schemaVersion {descriptor.SchemaVersion} in '{descriptorPath}'; " +
                    $"expected {MediaRootDescriptor.CurrentSchemaVersion}.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Malformed media-root descriptor JSON in '{descriptorPath}': {exception.Message}",
                exception);
        }
    }

    private static async Task WriteDescriptorNewAsync(
        string descriptorPath,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(descriptorPath)
            ?? throw new InvalidOperationException("The media-root descriptor path has no parent directory.");
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(descriptorPath)}.{Guid.NewGuid():N}.partial");
        try
        {
            string json = JsonSerializer.Serialize(
                new MediaRootDescriptor { SchemaVersion = MediaRootDescriptor.CurrentSchemaVersion },
                DescriptorWriteOptions) + Environment.NewLine;
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, descriptorPath, overwrite: false);
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
