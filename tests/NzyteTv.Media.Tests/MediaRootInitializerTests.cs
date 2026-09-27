using System.Text.Json;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class MediaRootInitializerTests
{
    [Fact]
    public async Task InitializeAsync_BlankRootCreatesCompletePortableStructure()
    {
        string parent = Directory.CreateTempSubdirectory("nzytetv-media-init-").FullName;
        string root = Path.Combine(parent, "Portable Media");
        try
        {
            MediaRootInitializationResult result = await new MediaRootInitializer().InitializeAsync(
                root,
                CancellationToken.None);

            Assert.Equal(Path.GetFullPath(root), result.MediaRoot);
            Assert.Equal(0, result.FilesOverwritten);
            Assert.True(Directory.Exists(Path.Combine(root, "source")));
            foreach (string category in AssetCategoryMap.DirectoryBackedSourceCategories)
            {
                Assert.True(Directory.Exists(Path.Combine(root, "source", category)), category);
                Assert.Contains($"source/{category}/", result.Created);
            }

            Assert.Equal(
                [
                    "Music Videos",
                    "Lyric Videos",
                    "Performance Videos",
                    "Vlog Episodes",
                    "Bumpers",
                    "Promos",
                    "Interstitials",
                    "Advertisements",
                    "Specials",
                ],
                AssetCategoryMap.DirectoryBackedSourceCategories);
            Assert.DoesNotContain("Short Form", AssetCategoryMap.DirectoryBackedSourceCategories);
            Assert.False(Directory.Exists(Path.Combine(root, "source", "Short Form")));
            Assert.False(Directory.Exists(Path.Combine(root, "source", "Visualizers")));
            Assert.False(Directory.Exists(Path.Combine(root, "source", "Animated Visuals")));
            foreach (string directory in new[] { "library", "catalog", "playlists", "work" })
            {
                Assert.True(Directory.Exists(Path.Combine(root, directory)), directory);
            }

            string descriptorPath = Path.Combine(root, MediaRootInitializer.DescriptorFileName);
            using JsonDocument descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(descriptorPath));
            Assert.Equal(
                MediaRootDescriptor.CurrentSchemaVersion,
                descriptor.RootElement.GetProperty("schemaVersion").GetInt32());
            SongCatalog catalog = new SongCatalogStore().Load(
                Path.Combine(root, "catalog", "song-catalog.json"));
            Assert.Equal(SongCatalog.CurrentSchemaVersion, catalog.SchemaVersion);
            Assert.Empty(catalog.Songs!);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_SecondRunPreservesAllContentAndAddsOnlyMissingCategory()
    {
        string root = Directory.CreateTempSubdirectory("nzytetv-media-init-").FullName;
        try
        {
            var initializer = new MediaRootInitializer();
            await initializer.InitializeAsync(root, CancellationToken.None);
            string descriptorPath = Path.Combine(root, MediaRootInitializer.DescriptorFileName);
            string descriptor = "{\r\n  \"schemaVersion\": 1,\r\n  \"future\": \"preserve\"\r\n}\r\n";
            await File.WriteAllTextAsync(descriptorPath, descriptor);
            string catalogPath = Path.Combine(root, "catalog", "song-catalog.json");
            string catalog = """
                {
                  "schemaVersion": 1,
                  "songs": [
                    { "contentGroupId": "cash-rules", "title": "Cash Rules", "artist": "Nzyte" }
                  ]
                }
                """;
            await File.WriteAllTextAsync(catalogPath, catalog);
            string source = WriteFile(root, "source/Music Videos/master.mp4", "original master");
            string sourceMetadata = WriteFile(
                root,
                "source/Music Videos/master.mp4.nzytetv.meta.json",
                "programming metadata");
            string library = WriteFile(root, "library/Music Videos/master.mp4", "normalized asset");
            string technicalManifest = WriteFile(
                root,
                "library/Music Videos/master.mp4.nzytetv.json",
                "technical manifest");
            string libraryMetadata = WriteFile(
                root,
                "library/Music Videos/master.mp4.nzytetv.meta.json",
                "library metadata");
            string playlist = WriteFile(root, "playlists/current.json", "playlist");
            string history = WriteFile(root, "playlists/history.json", "history");
            string missingCategory = Path.Combine(root, "source", "Promos");
            Directory.Delete(missingCategory);
            Dictionary<string, byte[]> before = new[]
            {
                descriptorPath,
                catalogPath,
                source,
                sourceMetadata,
                library,
                technicalManifest,
                libraryMetadata,
                playlist,
                history,
            }.ToDictionary(path => path, File.ReadAllBytes, GetPathComparer());

            MediaRootInitializationResult result = await initializer.InitializeAsync(
                root,
                CancellationToken.None);

            Assert.Equal(["source/Promos/"], result.Created);
            Assert.True(Directory.Exists(missingCategory));
            Assert.Equal(0, result.FilesOverwritten);
            foreach ((string path, byte[] contents) in before)
            {
                Assert.Equal(contents, File.ReadAllBytes(path));
            }

            Assert.Equal(descriptor, await File.ReadAllTextAsync(descriptorPath));
            Assert.Equal(catalog, await File.ReadAllTextAsync(catalogPath));
            Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "source"), "*.mp4", SearchOption.AllDirectories));
            Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "library"), "*.mp4", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{ not json", "Malformed")]
    [InlineData("{ \"schemaVersion\": 99 }", "Unsupported")]
    public async Task InitializeAsync_InvalidExistingDescriptorFailsBeforeChangingRoot(
        string descriptor,
        string expectedMessage)
    {
        string root = Directory.CreateTempSubdirectory("nzytetv-media-init-").FullName;
        try
        {
            string descriptorPath = Path.Combine(root, MediaRootInitializer.DescriptorFileName);
            await File.WriteAllTextAsync(descriptorPath, descriptor);
            string sentinel = Path.Combine(root, "keep.txt");
            await File.WriteAllTextAsync(sentinel, "keep");

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new MediaRootInitializer().InitializeAsync(root, CancellationToken.None));

            Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(descriptor, await File.ReadAllTextAsync(descriptorPath));
            Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
            Assert.False(Directory.Exists(Path.Combine(root, "source")));
            Assert.False(Directory.Exists(Path.Combine(root, "library")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string WriteFile(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
