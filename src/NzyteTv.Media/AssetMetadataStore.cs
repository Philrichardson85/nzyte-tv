using System.Text;
using System.Text.Json;
using NzyteTv.Core;

namespace NzyteTv.Media;

/// <summary>
/// Mutable adjacent-sidecar storage used by workstation metadata commands.
/// External generations are intentionally exposed through the read-only
/// <see cref="IAssetMetadataRepository"/> boundary instead.
/// </summary>
public interface IAssetMetadataStore
{
    AssetMetadata Read(string mediaPath);

    Task<bool> WriteAsync(string mediaPath, AssetMetadata metadata, CancellationToken cancellationToken);

    void Rebind(string oldMediaPath, string newMediaPath);
}

public sealed class AssetMetadataStore : IAssetMetadataStore
{
    public const string MetadataSuffix = ".nzytetv.meta.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public AssetMetadata Read(string mediaPath)
    {
        string metadataPath = GetMetadataPath(mediaPath);
        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException($"Programming metadata not found: {metadataPath}", metadataPath);
        }

        return Deserialize(File.ReadAllBytes(metadataPath), metadataPath);
    }

    public async Task<bool> WriteAsync(
        string mediaPath,
        AssetMetadata metadata,
        CancellationToken cancellationToken)
    {
        AssetMetadataValidator.ValidateStructure(metadata);
        string metadataPath = GetMetadataPath(mediaPath);
        string directory = Path.GetDirectoryName(metadataPath)
            ?? throw new InvalidOperationException("The metadata path has no parent directory.");
        Directory.CreateDirectory(directory);

        string json = Serialize(metadata);
        if (File.Exists(metadataPath)
            && string.Equals(await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false), json, StringComparison.Ordinal))
        {
            return false;
        }

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(metadataPath)}.{Guid.NewGuid():N}.partial");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, metadataPath, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Rebind(string oldMediaPath, string newMediaPath)
    {
        string oldMetadataPath = GetMetadataPath(oldMediaPath);
        string newMetadataPath = GetMetadataPath(newMediaPath);
        if (!File.Exists(oldMetadataPath))
        {
            throw new FileNotFoundException($"Programming metadata not found: {oldMetadataPath}", oldMetadataPath);
        }

        if (!File.Exists(Path.GetFullPath(newMediaPath)))
        {
            throw new FileNotFoundException($"New source media file not found: {Path.GetFullPath(newMediaPath)}", newMediaPath);
        }

        if (File.Exists(newMetadataPath))
        {
            throw new IOException($"The new source already has programming metadata: {newMetadataPath}");
        }

        _ = Read(oldMediaPath);
        string? directory = Path.GetDirectoryName(newMetadataPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.Move(oldMetadataPath, newMetadataPath);
    }

    public static string GetMetadataPath(string mediaPath) => Path.GetFullPath(mediaPath) + MetadataSuffix;

    internal static AssetMetadata Deserialize(ReadOnlySpan<byte> content, string description)
    {
        try
        {
            AssetMetadata? metadata = JsonSerializer.Deserialize<AssetMetadata>(content, ReadOptions);
            AssetMetadataValidator.ValidateStructure(metadata);
            return metadata!;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Malformed asset metadata JSON in '{description}': {exception.Message}", exception);
        }
    }

    internal static string Serialize(AssetMetadata metadata)
    {
        AssetMetadataValidator.ValidateStructure(metadata);
        return JsonSerializer.Serialize(metadata, WriteOptions) + Environment.NewLine;
    }
}
