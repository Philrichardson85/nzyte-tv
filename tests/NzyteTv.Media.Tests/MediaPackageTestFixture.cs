using System.Text.Json;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

internal sealed class MediaPackageTestFixture : IDisposable
{
    private int _packageId;

    public MediaPackageTestFixture()
    {
        Root = Directory.CreateTempSubdirectory("nzytetv-b2b-").FullName;
        MediaRoot = Path.Combine(Root, "media");
        SourceRoot = Path.Combine(MediaRoot, "source");
        LibraryRoot = Path.Combine(MediaRoot, "library");
        InboxRoot = Path.Combine(MediaRoot, "inbox");
        CatalogPath = Path.Combine(MediaRoot, "catalog", "song-catalog.json");
        MetadataRoot = Path.Combine(Root, "external-metadata");
        Directory.CreateDirectory(SourceRoot);
        Directory.CreateDirectory(LibraryRoot);
        Directory.CreateDirectory(InboxRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
        Directory.CreateDirectory(MetadataRoot);
        WriteCatalog([
            new SongCatalogEntry
            {
                ContentGroupId = "test-song",
                Title = "Test Song",
                Artist = "NZYTE",
                Aliases = ["The Test Song"],
            },
            new SongCatalogEntry
            {
                ContentGroupId = "other-song",
                Title = "Other Song",
                Artist = "NZYTE",
                Aliases = [],
            },
        ]);
    }

    public string Root { get; }
    public string MediaRoot { get; }
    public string SourceRoot { get; }
    public string LibraryRoot { get; }
    public string InboxRoot { get; }
    public string CatalogPath { get; }
    public string MetadataRoot { get; }

    public MediaPackageStorageOptions PackageOptions => new(MediaRoot, InboxRoot);

    public async Task<(string SourcePath, string LibraryPath)> AddMembersAsync(
        string sourceRelative = "Music Videos/Test Song.mov",
        AssetMetadata? sourceMetadata = null,
        AssetMetadata? libraryMetadata = null)
    {
        string sourcePath = Path.Combine(SourceRoot, sourceRelative.Replace('/', Path.DirectorySeparatorChar));
        string libraryPath = LibraryPathPolicy.GetDestinationPath(SourceRoot, LibraryRoot, sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
        await File.WriteAllTextAsync(sourcePath, $"source::{sourceRelative}");
        await File.WriteAllTextAsync(libraryPath, $"library::{sourceRelative}");
        var manifestStore = new SourceManifestStore();
        await manifestStore.WriteAsync(
            libraryPath,
            manifestStore.CreateFingerprint(SourceRoot, sourcePath),
            CancellationToken.None);
        var metadataStore = new AssetMetadataStore();
        if (sourceMetadata is not null)
        {
            await metadataStore.WriteAsync(sourcePath, sourceMetadata, CancellationToken.None);
        }

        if (libraryMetadata is not null)
        {
            await metadataStore.WriteAsync(libraryPath, libraryMetadata, CancellationToken.None);
        }

        return (sourcePath, libraryPath);
    }

    public Task<ReadyPackagePreparationResult> PrepareAsync(
        string sourceRelative = "Music Videos/Test Song.mov",
        string? packageId = null)
    {
        string id = packageId ?? NextPackageId();
        return new MediaPackagePreparer(packageIdFactory: () => id).PrepareAsync(
            PackageOptions,
            sourceRelative,
            CancellationToken.None);
    }

    public async Task<ExternalAssetMetadataGenerationStore> InitializeEmptyExternalAsync()
    {
        var store = new ExternalAssetMetadataGenerationStore(MetadataRoot);
        var inventory = new AssetMetadataGenerationInventory
        {
            Revision = 1,
            GenerationId = "000000000001",
            Packages = [],
            Assets = [],
        };
        await store.CreateGenerationAsync(
            "000000000001",
            1,
            [],
            inventory,
            CancellationToken.None);
        await store.BootstrapCurrentAsync("000000000001", CancellationToken.None);
        return store;
    }

    public void WriteCatalog(IReadOnlyCollection<SongCatalogEntry> songs)
    {
        var catalog = new SongCatalog
        {
            SchemaVersion = SongCatalog.CurrentSchemaVersion,
            Songs = songs.ToList(),
        };
        File.WriteAllText(CatalogPath, JsonSerializer.Serialize(catalog, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }) + Environment.NewLine);
    }

    public static AssetMetadata Metadata(
        string assetId = "test-song-music-video",
        string? contentGroupId = "test-song",
        string title = "Test Song",
        string type = AssetTypes.MusicVideo,
        bool enabled = true) => new()
        {
            AssetId = assetId,
            ContentGroupId = contentGroupId,
            Title = title,
            Artist = contentGroupId is null ? null : "NZYTE",
            Type = type,
            Enabled = enabled,
            Tags = [],
        };

    public void Dispose() => Directory.Delete(Root, recursive: true);

    private string NextPackageId() => (++_packageId).ToString("x32");
}
