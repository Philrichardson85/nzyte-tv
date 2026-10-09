using System.Text;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class AssetMetadataRepositoryTests
{
    [Fact]
    public void StorageOptions_DefaultToAdjacentAndRequireExplicitExternalRoot()
    {
        Assert.Equal(AssetMetadataStorageMode.Adjacent, new AssetMetadataStorageOptions().Validate().Mode);
        Assert.IsType<AdjacentAssetMetadataRepository>(AssetMetadataRepository.Create());
        Assert.Throws<InvalidOperationException>(() => new AssetMetadataStorageOptions
        {
            Mode = AssetMetadataStorageMode.ExternalGeneration,
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AssetMetadataStorageOptions
        {
            ExternalRoot = Path.GetFullPath("metadata"),
        }.Validate());
    }

    [Fact]
    public async Task AdjacentRepository_PreservesExistingPathAndMalformedBehavior()
    {
        using var fixture = new RepositoryFixture();
        string mediaPath = fixture.AddMedia("Music Videos/Prince - Purple Rain.mp4");
        AssetMetadata metadata = CreateMetadata("purple-rain", "Purple Rain");
        await new AssetMetadataStore().WriteAsync(mediaPath, metadata, CancellationToken.None);
        IAssetMetadataSnapshot snapshot = new AdjacentAssetMetadataRepository().Pin(
            AssetMetadataTree.Library,
            fixture.LibraryRoot);

        Assert.Equal(["Music Videos/Prince - Purple Rain.mp4"], snapshot.DiscoverRelativeMediaPaths());
        Assert.True(snapshot.Exists("Music Videos/Prince - Purple Rain.mp4"));
        Assert.Equal(metadata.AssetId, snapshot.Read("Music Videos/Prince - Purple Rain.mp4").Metadata.AssetId);
        Assert.True(File.Exists(mediaPath + AssetMetadataStore.MetadataSuffix));

        await File.WriteAllTextAsync(AssetMetadataStore.GetMetadataPath(mediaPath), "{ not json");
        Assert.Throws<InvalidDataException>(() => snapshot.Read("Music Videos/Prince - Purple Rain.mp4"));
    }

    [Fact]
    public async Task ExternalGeneration_CreatesPublishesPinsAndMapsNestedUnicodePaths()
    {
        using var fixture = new RepositoryFixture();
        string relative = "Music Videos/Beyoncé - Déjà Vu.mp4";
        _ = fixture.AddMedia(relative);
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        await store.CreateGenerationAsync(
            "000000000001",
            1,
            [new AssetMetadataGenerationRecord(
                AssetMetadataTree.Library,
                relative.Replace('/', '\\'),
                CreateMetadata("deja-vu", "Déjà Vu"))],
            CancellationToken.None);

        AssetMetadataGenerationPointer pointer = await store.PublishCurrentAsync(
            "000000000001",
            0,
            CancellationToken.None);
        IAssetMetadataSnapshot snapshot = store.Pin(AssetMetadataTree.Library, fixture.LibraryRoot);

        Assert.Equal(1, pointer.Revision);
        Assert.Equal("000000000001", pointer.GenerationId);
        Assert.Equal(1, snapshot.Identity.Revision);
        Assert.Equal("000000000001", snapshot.Identity.GenerationId);
        Assert.Equal([relative], snapshot.DiscoverRelativeMediaPaths());
        Assert.Equal("Déjà Vu", snapshot.Read(relative).Metadata.Title);
        Assert.True(File.Exists(Path.Combine(
            fixture.MetadataRoot,
            "generations",
            "000000000001",
            "library",
            "Music Videos",
            "Beyoncé - Déjà Vu.mp4.nzytetv.meta.json")));
    }

    [Fact]
    public async Task ExternalGeneration_MapsSourceAndLibraryTreesIndependently()
    {
        using var fixture = new RepositoryFixture();
        string sourceRoot = Path.Combine(fixture.Root, "source");
        Directory.CreateDirectory(sourceRoot);
        string sourceRelative = "Music Videos/Source File.mov";
        string libraryRelative = "Music Videos/Source File.mp4";
        string sourcePath = Path.Combine(sourceRoot, sourceRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, "source");
        _ = fixture.AddMedia(libraryRelative);
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        await store.CreateGenerationAsync(
            "000000000001",
            1,
            [
                new AssetMetadataGenerationRecord(
                    AssetMetadataTree.Source,
                    sourceRelative,
                    CreateMetadata("source-asset", "Source")),
                new AssetMetadataGenerationRecord(
                    AssetMetadataTree.Library,
                    libraryRelative,
                    CreateMetadata("library-asset", "Library")),
            ],
            CancellationToken.None);
        await store.PublishCurrentAsync("000000000001", 0, CancellationToken.None);

        IAssetMetadataSnapshot source = store.Pin(AssetMetadataTree.Source, sourceRoot);
        IAssetMetadataSnapshot library = store.Pin(AssetMetadataTree.Library, fixture.LibraryRoot);

        Assert.Equal("source-asset", source.Read(sourceRelative).Metadata.AssetId);
        Assert.Equal("library-asset", library.Read(libraryRelative).Metadata.AssetId);
        Assert.False(source.Exists(libraryRelative));
        Assert.False(library.Exists(sourceRelative));
    }

    [Fact]
    public async Task PinnedGeneration_RemainsStableAfterCurrentPointerChanges()
    {
        using var fixture = new RepositoryFixture();
        const string relative = "Music Videos/song.mp4";
        _ = fixture.AddMedia(relative);
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        await CreateAndPublishAsync(store, "000000000001", 1, 0, relative, "First");
        IAssetMetadataSnapshot first = store.Pin(AssetMetadataTree.Library, fixture.LibraryRoot);
        await CreateAndPublishAsync(store, "000000000002", 2, 1, relative, "Second");
        IAssetMetadataSnapshot second = store.Pin(AssetMetadataTree.Library, fixture.LibraryRoot);

        Assert.Equal("First", first.Read(relative).Metadata.Title);
        Assert.Equal("Second", second.Read(relative).Metadata.Title);
        Assert.Equal("000000000001", first.Identity.GenerationId);
        Assert.Equal("000000000002", second.Identity.GenerationId);
    }

    [Fact]
    public async Task ExternalRepository_ExplicitLegacyIdentityUsesAdjacentAuthorityAfterCurrentAdvances()
    {
        using var fixture = new RepositoryFixture();
        const string relative = "Music Videos/song.mp4";
        string mediaPath = fixture.AddMedia(relative);
        await new AssetMetadataStore().WriteAsync(
            mediaPath,
            CreateMetadata("asset", "Adjacent Title"),
            CancellationToken.None);
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        await CreateAndPublishAsync(store, "000000000001", 1, 0, relative, "Generation One");
        await CreateAndPublishAsync(store, "000000000002", 2, 1, relative, "Generation Two");

        IAssetMetadataSnapshot legacy = store.Pin(
            AssetMetadataTree.Library,
            fixture.LibraryRoot,
            new AssetMetadataSnapshotIdentity(AssetMetadataStorageMode.Adjacent));
        IAssetMetadataSnapshot current = store.Pin(AssetMetadataTree.Library, fixture.LibraryRoot);

        Assert.Equal(AssetMetadataStorageMode.Adjacent, legacy.Identity.Mode);
        Assert.Equal("Adjacent Title", legacy.Read(relative).Metadata.Title);
        Assert.Equal("Generation Two", current.Read(relative).Metadata.Title);
    }

    [Fact]
    public async Task ExternalMode_MissingCurrentPointerFailsClosedWithoutAdjacentFallback()
    {
        using var fixture = new RepositoryFixture();
        string mediaPath = fixture.AddMedia("Music Videos/song.mp4");
        await new AssetMetadataStore().WriteAsync(
            mediaPath,
            CreateMetadata("asset", "Adjacent Title"),
            CancellationToken.None);
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);

        Assert.Throws<FileNotFoundException>(() =>
            store.Pin(AssetMetadataTree.Library, fixture.LibraryRoot));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"schemaVersion\":2,\"revision\":1,\"generationId\":\"000000000001\"}")]
    [InlineData("{\"schemaVersion\":1,\"revision\":0,\"generationId\":\"000000000001\"}")]
    [InlineData("{\"schemaVersion\":1,\"revision\":1,\"generationId\":\"../outside\"}")]
    [InlineData("{\"schemaVersion\":1,\"revision\":1,\"generationId\":\"000000000001\",\"unexpected\":true}")]
    [InlineData("{\"schemaVersion\":1,\"revision\":1,\"revision\":2,\"generationId\":\"000000000001\"}")]
    public async Task ExternalMode_MalformedOrUnsupportedCurrentPointerFailsClosed(string json)
    {
        using var fixture = new RepositoryFixture();
        string mediaPath = fixture.AddMedia("Music Videos/song.mp4");
        await new AssetMetadataStore().WriteAsync(
            mediaPath,
            CreateMetadata("asset", "Adjacent Title"),
            CancellationToken.None);
        Directory.CreateDirectory(fixture.MetadataRoot);
        await File.WriteAllTextAsync(Path.Combine(fixture.MetadataRoot, "current.json"), json);
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);

        Assert.Throws<InvalidDataException>(() =>
            store.Pin(AssetMetadataTree.Library, fixture.LibraryRoot));
    }

    [Fact]
    public async Task ExternalMode_MissingGenerationFailsClosed()
    {
        using var fixture = new RepositoryFixture();
        string mediaPath = fixture.AddMedia("Music Videos/song.mp4");
        await new AssetMetadataStore().WriteAsync(
            mediaPath,
            CreateMetadata("asset", "Adjacent Title"),
            CancellationToken.None);
        Directory.CreateDirectory(fixture.MetadataRoot);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.MetadataRoot, "current.json"),
            "{\"schemaVersion\":1,\"revision\":1,\"generationId\":\"000000000001\"}");
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);

        Assert.Throws<DirectoryNotFoundException>(() =>
            store.Pin(AssetMetadataTree.Library, fixture.LibraryRoot));
    }

    [Fact]
    public async Task ExternalMode_PointerAndGenerationManifestMismatchFailsClosed()
    {
        using var fixture = new RepositoryFixture();
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        await store.CreateGenerationAsync("000000000001", 1, [], CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.MetadataRoot, "current.json"),
            "{\"schemaVersion\":1,\"revision\":2,\"generationId\":\"000000000001\"}");

        Assert.Throws<InvalidDataException>(() => store.ReadCurrent());
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"revision\":1,\"generationId\":\"000000000001\"}")]
    [InlineData("{\"schemaVersion\":1,\"revision\":1,\"generationId\":\"000000000001\",\"unexpected\":true}")]
    public async Task ExternalMode_UnsupportedOrUnknownGenerationManifestFailsClosed(string json)
    {
        using var fixture = new RepositoryFixture();
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        await store.CreateGenerationAsync("000000000001", 1, [], CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(
                fixture.MetadataRoot,
                "generations",
                "000000000001",
                "generation.json"),
            json);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.MetadataRoot, "current.json"),
            "{\"schemaVersion\":1,\"revision\":1,\"generationId\":\"000000000001\"}");

        Assert.Throws<InvalidDataException>(() => store.ReadCurrent());
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\system.ini")]
    [InlineData("../outside.mp4")]
    [InlineData("Music Videos/../outside.mp4")]
    [InlineData("Music Videos//song.mp4")]
    [InlineData("Music Videos/./song.mp4")]
    public async Task GenerationCreation_RejectsUnsafeRelativeIdentities(string relativePath)
    {
        using var fixture = new RepositoryFixture();
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.CreateGenerationAsync(
            "000000000001",
            1,
            [new AssetMetadataGenerationRecord(
                AssetMetadataTree.Library,
                relativePath,
                CreateMetadata("asset", "Title"))],
            CancellationToken.None));
    }

    [Fact]
    public async Task GenerationCreation_RejectsDuplicateNormalizedIdentityAndPublishedRewrite()
    {
        using var fixture = new RepositoryFixture();
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CreateGenerationAsync(
            "000000000001",
            1,
            [
                new AssetMetadataGenerationRecord(
                    AssetMetadataTree.Library,
                    "Music Videos/Song.mp4",
                    CreateMetadata("asset-a", "A")),
                new AssetMetadataGenerationRecord(
                    AssetMetadataTree.Library,
                    "Music Videos\\Song.mp4",
                    CreateMetadata("asset-b", "B")),
            ],
            CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(
            fixture.MetadataRoot,
            "generations",
            "000000000001")));
        Assert.Empty(Directory.EnumerateDirectories(
            fixture.MetadataRoot,
            "*.staging",
            SearchOption.TopDirectoryOnly));

        await store.CreateGenerationAsync(
            "000000000001",
            1,
            [],
            CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => store.CreateGenerationAsync(
            "000000000001",
            1,
            [],
            CancellationToken.None));
    }

    [Fact]
    public async Task Publication_RejectsIncompleteFinalGenerationAndLeavesPointerMissing()
    {
        using var fixture = new RepositoryFixture();
        string generationRoot = Path.Combine(
            fixture.MetadataRoot,
            "generations",
            "000000000001");
        Directory.CreateDirectory(Path.Combine(generationRoot, "source"));
        await File.WriteAllTextAsync(
            Path.Combine(generationRoot, "generation.json"),
            "{\"schemaVersion\":1,\"revision\":1,\"generationId\":\"000000000001\"}");
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => store.PublishCurrentAsync(
            "000000000001",
            0,
            CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(fixture.MetadataRoot, "current.json")));
    }

    [Fact]
    public async Task ExternalMode_IncompleteReferencedGenerationFailsClosed()
    {
        using var fixture = new RepositoryFixture();
        string generationRoot = Path.Combine(
            fixture.MetadataRoot,
            "generations",
            "000000000001");
        Directory.CreateDirectory(Path.Combine(generationRoot, "source"));
        await File.WriteAllTextAsync(
            Path.Combine(generationRoot, "generation.json"),
            "{\"schemaVersion\":1,\"revision\":1,\"generationId\":\"000000000001\"}");
        await File.WriteAllTextAsync(
            Path.Combine(fixture.MetadataRoot, "current.json"),
            "{\"schemaVersion\":1,\"revision\":1,\"generationId\":\"000000000001\"}");
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);

        Assert.Throws<DirectoryNotFoundException>(() =>
            store.Pin(AssetMetadataTree.Library, fixture.LibraryRoot));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("000000000000")]
    [InlineData("00000000001a")]
    [InlineData("../000000001")]
    public async Task GenerationCreation_RejectsInvalidGenerationIds(string generationId)
    {
        using var fixture = new RepositoryFixture();
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.CreateGenerationAsync(
            generationId,
            1,
            [],
            CancellationToken.None));
    }

    [Fact]
    public async Task ExternalSnapshot_MissingAndMalformedMetadataHaveSafeReadSemantics()
    {
        using var fixture = new RepositoryFixture();
        const string relative = "Music Videos/song.mp4";
        _ = fixture.AddMedia(relative);
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        await store.CreateGenerationAsync("000000000001", 1, [], CancellationToken.None);
        await store.PublishCurrentAsync("000000000001", 0, CancellationToken.None);
        IAssetMetadataSnapshot missing = store.Pin(AssetMetadataTree.Library, fixture.LibraryRoot);
        Assert.False(missing.Exists(relative));
        Assert.Throws<FileNotFoundException>(() => missing.Read(relative));

        string metadataPath = Path.Combine(
            fixture.MetadataRoot,
            "generations",
            "000000000001",
            "library",
            relative.Replace('/', Path.DirectorySeparatorChar) + AssetMetadataStore.MetadataSuffix);
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        await File.WriteAllTextAsync(metadataPath, "{ malformed");
        Assert.Throws<InvalidDataException>(() => missing.Read(relative));
    }

    [Fact]
    public async Task ProgrammingInventory_UsesExternalGenerationAndIgnoresAdjacentSidecar()
    {
        using var fixture = new RepositoryFixture();
        const string relative = "Music Videos/song.mp4";
        string mediaPath = fixture.AddMedia(relative);
        await File.WriteAllTextAsync(SourceManifestStore.GetManifestPath(mediaPath), "technical");
        await new AssetMetadataStore().WriteAsync(
            mediaPath,
            CreateMetadata("adjacent-asset", "Adjacent"),
            CancellationToken.None);
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        await store.CreateGenerationAsync(
            "000000000001",
            1,
            [new AssetMetadataGenerationRecord(
                AssetMetadataTree.Library,
                relative,
                CreateMetadata("external-asset", "External"))],
            CancellationToken.None);
        await store.PublishCurrentAsync("000000000001", 0, CancellationToken.None);
        var catalog = new SongCatalog
        {
            SchemaVersion = SongCatalog.CurrentSchemaVersion,
            Songs =
            [
                new SongCatalogEntry
                {
                    ContentGroupId = "song",
                    Title = "Song",
                    Artist = "Artist",
                    Aliases = [],
                },
            ],
        };

        ProgrammingInventory inventory = new ProgrammingInventoryLoader(
            metadataRepository: store).Load(fixture.LibraryRoot, catalog);

        ProgrammingAssetInventoryEntry asset = Assert.Single(inventory.Assets);
        Assert.Equal("external-asset", asset.AssetId);
        Assert.True(asset.IsTechnicallyPlaylistEligible);
        Assert.DoesNotContain(inventory.Assets, value => value.AssetId == "adjacent-asset");
    }

    [Fact]
    public async Task ExternalLibraryLoader_TreatsMissingGenerationMetadataAsIneligible()
    {
        using var fixture = new RepositoryFixture();
        const string relative = "Music Videos/song.mp4";
        string mediaPath = fixture.AddMedia(relative);
        await File.WriteAllTextAsync(SourceManifestStore.GetManifestPath(mediaPath), "technical");
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        await store.CreateGenerationAsync("000000000001", 1, [], CancellationToken.None);
        await store.PublishCurrentAsync("000000000001", 0, CancellationToken.None);
        var catalog = new SongCatalog
        {
            SchemaVersion = SongCatalog.CurrentSchemaVersion,
            Songs = [],
        };
        var loader = new PlaylistLibraryLoader(new StubAnalyzer(), metadataRepository: store);

        PlaylistLibrarySnapshot inventory = await loader.LoadAsync(
            fixture.LibraryRoot,
            catalog,
            CancellationToken.None);

        Assert.Empty(inventory.EligibleAssets);
        PlaylistExclusion excluded = Assert.Single(inventory.ExcludedAssets);
        Assert.Equal(relative, excluded.RelativePath);
        Assert.Contains(excluded.Reasons, reason => reason.Contains("metadata", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExternalDiscovery_IgnoresAdjacentMetadataOnlyEvidence()
    {
        using var fixture = new RepositoryFixture();
        string missingMedia = Path.Combine(fixture.LibraryRoot, "Music Videos", "missing.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(missingMedia)!);
        await new AssetMetadataStore().WriteAsync(
            missingMedia,
            CreateMetadata("adjacent-only", "Adjacent Only"),
            CancellationToken.None);

        var catalog = new SongCatalog
        {
            SchemaVersion = SongCatalog.CurrentSchemaVersion,
            Songs = [],
        };
        PlaylistLibrarySnapshot adjacent = await new PlaylistLibraryLoader(new StubAnalyzer()).LoadAsync(
            fixture.LibraryRoot,
            catalog,
            CancellationToken.None);
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        await store.CreateGenerationAsync("000000000001", 1, [], CancellationToken.None);
        await store.PublishCurrentAsync("000000000001", 0, CancellationToken.None);
        PlaylistLibrarySnapshot external = await new PlaylistLibraryLoader(
            new StubAnalyzer(),
            metadataRepository: store).LoadAsync(
                fixture.LibraryRoot,
                catalog,
                CancellationToken.None);

        Assert.Single(adjacent.ExcludedAssets);
        Assert.Empty(external.ExcludedAssets);
    }

    [Fact]
    public async Task ExternalSnapshot_RejectsReparseEscapeWhenPlatformPermitsLinks()
    {
        using var fixture = new RepositoryFixture();
        const string relative = "linked/song.mp4";
        _ = fixture.AddMedia(relative);
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        await store.CreateGenerationAsync("000000000001", 1, [], CancellationToken.None);
        await store.PublishCurrentAsync("000000000001", 0, CancellationToken.None);
        string outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        string outsideMedia = Path.Combine(outside, "song.mp4");
        await File.WriteAllTextAsync(outsideMedia, "outside");
        await new AssetMetadataStore().WriteAsync(
            outsideMedia,
            CreateMetadata("outside", "Outside"),
            CancellationToken.None);
        string link = Path.Combine(
            fixture.MetadataRoot,
            "generations",
            "000000000001",
            "library",
            "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        IAssetMetadataSnapshot snapshot = store.Pin(AssetMetadataTree.Library, fixture.LibraryRoot);
        Assert.Throws<InvalidDataException>(() => snapshot.Read(relative));
    }

    [Fact]
    public void ReaderInterfacesExposeNoMutationMethods()
    {
        Assert.All(
            typeof(IAssetMetadataRepository).GetMethods(),
            method => Assert.Equal("Pin", method.Name));
        Assert.DoesNotContain(
            typeof(IAssetMetadataSnapshot).GetMethods(),
            method => method.Name.Contains("Write", StringComparison.Ordinal)
                || method.Name.Contains("Delete", StringComparison.Ordinal)
                || method.Name.Contains("Move", StringComparison.Ordinal));
        Assert.False(typeof(IAssetMetadataStore).IsAssignableFrom(typeof(ExternalAssetMetadataGenerationStore)));
    }

    [Fact]
    public async Task Publication_UsesExpectedRevisionAndExactlyOneConcurrentPublisherWins()
    {
        using var fixture = new RepositoryFixture();
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        await store.CreateGenerationAsync("000000000001", 1, [], CancellationToken.None);
        await store.PublishCurrentAsync("000000000001", 0, CancellationToken.None);
        await store.CreateGenerationAsync("000000000002", 2, [], CancellationToken.None);

        Task<AssetMetadataGenerationPointer>[] attempts =
        [
            store.PublishCurrentAsync("000000000002", 1, CancellationToken.None),
            store.PublishCurrentAsync("000000000002", 1, CancellationToken.None),
        ];
        await Task.WhenAll(attempts.Select(async attempt =>
        {
            try { _ = await attempt; }
            catch (AssetMetadataGenerationConflictException) { }
        }));

        _ = Assert.Single(attempts, task => task.Status == TaskStatus.RanToCompletion);
        _ = Assert.Single(attempts, task => task.IsFaulted
            && task.Exception!.Flatten().InnerExceptions.Single() is AssetMetadataGenerationConflictException);
        Assert.Equal(2, store.ReadCurrent().Revision);
        await Assert.ThrowsAsync<AssetMetadataGenerationConflictException>(() =>
            store.PublishCurrentAsync("000000000002", 1, CancellationToken.None));
    }

    [Fact]
    public async Task PublicationFailure_LeavesOldPointerAndGenerationContentsUnchanged()
    {
        using var fixture = new RepositoryFixture();
        var store = new ExternalAssetMetadataGenerationStore(fixture.MetadataRoot);
        await store.CreateGenerationAsync("000000000001", 1, [], CancellationToken.None);
        await store.PublishCurrentAsync("000000000001", 0, CancellationToken.None);
        await store.CreateGenerationAsync(
            "000000000002",
            2,
            [new AssetMetadataGenerationRecord(
                AssetMetadataTree.Library,
                "Music Videos/song.mp4",
                CreateMetadata("asset", "Title"))],
            CancellationToken.None);
        string generationFile = Path.Combine(
            fixture.MetadataRoot,
            "generations",
            "000000000002",
            "library",
            "Music Videos",
            "song.mp4.nzytetv.meta.json");
        byte[] before = await File.ReadAllBytesAsync(generationFile);
        var failingStore = new ExternalAssetMetadataGenerationStore(
            fixture.MetadataRoot,
            new FailingWriter());

        await Assert.ThrowsAsync<IOException>(() => failingStore.PublishCurrentAsync(
            "000000000002",
            1,
            CancellationToken.None));

        Assert.Equal("000000000001", store.ReadCurrent().GenerationId);
        Assert.Equal(before, await File.ReadAllBytesAsync(generationFile));
    }

    private static async Task CreateAndPublishAsync(
        ExternalAssetMetadataGenerationStore store,
        string generationId,
        long revision,
        long expectedRevision,
        string relativePath,
        string title)
    {
        await store.CreateGenerationAsync(
            generationId,
            revision,
            [new AssetMetadataGenerationRecord(
                AssetMetadataTree.Library,
                relativePath,
                CreateMetadata("asset", title))],
            CancellationToken.None);
        await store.PublishCurrentAsync(generationId, expectedRevision, CancellationToken.None);
    }

    private static AssetMetadata CreateMetadata(string assetId, string title) => new()
    {
        AssetId = assetId,
        ContentGroupId = "song",
        Title = title,
        Artist = "Artist",
        Type = AssetTypes.MusicVideo,
        Enabled = true,
        Tags = [],
    };

    private sealed class FailingWriter : IAtomicTextFileWriter
    {
        public Task WriteAsync(string path, string content, CancellationToken cancellationToken) =>
            throw new IOException("Simulated pointer publication failure.");
    }

    private sealed class StubAnalyzer : IMediaAnalyzer
    {
        public Task<MediaDescription> InspectAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaDescription(
                filePath,
                TimeSpan.FromMinutes(4),
                "mov,mp4",
                1,
                null,
                null,
                []));

        public Task<MediaDescription> AnalyzeForVerificationAsync(
            string filePath,
            CancellationToken cancellationToken) => InspectAsync(filePath, cancellationToken);
    }

    private sealed class RepositoryFixture : IDisposable
    {
        public RepositoryFixture()
        {
            Root = Directory.CreateTempSubdirectory("nzytetv-metadata-repository-").FullName;
            LibraryRoot = Path.Combine(Root, "library");
            MetadataRoot = Path.Combine(Root, "external-metadata");
            Directory.CreateDirectory(LibraryRoot);
        }

        public string Root { get; }

        public string LibraryRoot { get; }

        public string MetadataRoot { get; }

        public string AddMedia(string relativePath)
        {
            string path = Path.Combine(
                LibraryRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "media");
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
