using System.Text.Json;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class MetadataWorkflowTests
{
    [Theory]
    [InlineData("Visualizers", AssetTypes.Visualizer)]
    [InlineData("Animated Visuals", AssetTypes.AnimatedVisual)]
    public void CategoryMap_DetectsNewDirectoryBackedSongTypes(string directory, string expectedType)
    {
        string sourceRoot = Path.GetFullPath(Path.Combine("source", "root"));
        string sourcePath = Path.Combine(sourceRoot, directory, "Song.mp4");

        Assert.True(AssetCategoryMap.TryDetect(sourceRoot, sourcePath, out string? type));
        Assert.Equal(expectedType, type);
    }

    [Fact]
    public async Task Initialize_MusicLyricAndPerformanceResolveToOneGroupWithDistinctAssetIds()
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song("free-fallin", "Free Fallin"));
        fixture.AddSource("Music Videos", "Nzyte - FREE FALLIN (Official Music Video).mp4");
        fixture.AddSource("Lyric Videos", "Free Fallin - Lyric Video.mp4");
        fixture.AddSource("Performance Videos", "Free Fallin - Krog Street Performance.mp4");

        MetadataInitializationResult result = await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);

        Assert.Equal(3, result.AutomaticallyResolved);
        Assert.All(result.Assets, asset => Assert.Equal("free-fallin", asset.ContentGroupId));
        Assert.Equal(3, result.Assets.Select(asset => asset.AssetId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(result.Assets, asset => asset.AssetId == "free-fallin-music-video");
        Assert.Contains(result.Assets, asset => asset.AssetId == "free-fallin-lyric-video");
        Assert.Contains(result.Assets, asset => asset.AssetId == "free-fallin-performance-krog-street");
    }

    [Fact]
    public async Task Initialize_AliasMatchWritesCanonicalCatalogTitleAndArtist()
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song(
            "i-did-it-for-you",
            "I Did It For You",
            artist: "Canonical Artist",
            aliases: ["I Did It 4 U"]));
        string source = fixture.AddSource(
            "Music Videos",
            "Nzyte - I Did It 4 U (Official Music Video).mp4");

        await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);

        AssetMetadata metadata = fixture.MetadataStore.Read(source);
        Assert.Equal("i-did-it-for-you", metadata.ContentGroupId);
        Assert.Equal("I Did It For You", metadata.Title);
        Assert.Equal("Canonical Artist", metadata.Artist);
    }

    [Fact]
    public async Task Initialize_AcronymAliasWritesFullCanonicalCatalogTitle()
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song(
            "full-official-song",
            "Some Full Official Song Title",
            aliases: ["WALTW"]));
        string source = fixture.AddSource("Music Videos", "WALTW Content 15.mp4");

        await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);

        AssetMetadata metadata = fixture.MetadataStore.Read(source);
        Assert.Equal("full-official-song", metadata.ContentGroupId);
        Assert.Equal("Some Full Official Song Title", metadata.Title);
        Assert.Equal("Nzyte", metadata.Artist);
    }

    [Theory]
    [InlineData("Visualizers", "Pray MVV.mp4", "pray", "Pray", AssetTypes.Visualizer, "pray-visualizer")]
    [InlineData(
        "Animated Visuals",
        "Beauty Sold Separately Content 11.mp4",
        "beauty-sold-separately",
        "Beauty Sold Separately",
        AssetTypes.AnimatedVisual,
        "beauty-sold-separately-animated-visual")]
    public async Task Initialize_NewSongCategoriesUseCatalogMatchingAndCanonicalIdentity(
        string category,
        string fileName,
        string contentGroupId,
        string title,
        string type,
        string assetId)
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song(contentGroupId, title, artist: "Catalog Artist"));
        string source = fixture.AddSource(category, fileName);

        MetadataInitializationResult result = await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);

        MetadataAssetResult asset = Assert.Single(result.Assets);
        Assert.Equal(MetadataInitializationStatus.Resolved, asset.Status);
        Assert.Equal(type, asset.Type);
        Assert.Equal(contentGroupId, asset.ContentGroupId);
        Assert.Equal(assetId, asset.AssetId);
        AssetMetadata metadata = fixture.MetadataStore.Read(source);
        Assert.Equal(title, metadata.Title);
        Assert.Equal("Catalog Artist", metadata.Artist);
    }

    [Theory]
    [InlineData("Content Template Lipsync1 - I Did It For You.mp4", "lipsync", "i-did-it-for-you")]
    [InlineData("Content Template Mic Drop1 - Cash Rules.mp4", "mic-drop", "cash-rules")]
    public async Task Initialize_PerformanceDescriptorKeepsPerformanceType(
        string fileName,
        string expectedSubtype,
        string expectedGroup)
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(
            Song("i-did-it-for-you", "I Did It For You"),
            Song("cash-rules", "Cash Rules"));
        string source = fixture.AddSource("Performance Videos", fileName);

        MetadataInitializationResult result = await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot, fixture.LibraryRoot, fixture.CatalogPath, dryRun: false, CancellationToken.None);

        MetadataAssetResult asset = Assert.Single(result.Assets);
        Assert.Equal(AssetTypes.Performance, asset.Type);
        Assert.Equal(expectedSubtype, asset.Subtype);
        Assert.Equal(expectedGroup, asset.ContentGroupId);
        Assert.Equal(AssetTypes.Performance, fixture.MetadataStore.Read(source).Type);
    }

    [Theory]
    [InlineData("Visualizers", AssetTypes.Visualizer)]
    [InlineData("Animated Visuals", AssetTypes.AnimatedVisual)]
    public async Task Initialize_UnresolvedNewSongCategoryRequiresReviewWithoutGuessing(
        string category,
        string type)
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song("known-song", "Known Song"));
        string source = fixture.AddSource(category, "Completely Unknown Presentation.mp4");

        MetadataInitializationResult result = await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);

        MetadataAssetResult asset = Assert.Single(result.Assets);
        Assert.Equal(MetadataInitializationStatus.Unresolved, asset.Status);
        Assert.Equal(type, asset.Type);
        Assert.Null(asset.ContentGroupId);
        Assert.Null(fixture.MetadataStore.Read(source).ContentGroupId);
    }

    [Theory]
    [InlineData("Visualizers", AssetTypes.Visualizer)]
    [InlineData("Animated Visuals", AssetTypes.AnimatedVisual)]
    public async Task Review_UnresolvedNewSongCategoryOffersExistingCatalogChoices(
        string category,
        string type)
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song("known-song", "Known Song", artist: "Catalog Artist"));
        string source = fixture.AddSource(category, "Completely Unknown Presentation.mp4");
        await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);
        var reviewer = new MetadataReviewer(
            new SongCatalogStore(),
            fixture.Discovery,
            fixture.MetadataStore,
            fixture.Synchronizer,
            new SelectingPrompt("known-song"));

        MetadataReviewResult result = await reviewer.ReviewAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            CancellationToken.None);

        MetadataReviewFileResult reviewed = Assert.Single(result.Files);
        Assert.True(reviewed.Resolved);
        AssetMetadata metadata = fixture.MetadataStore.Read(source);
        Assert.Equal(type, metadata.Type);
        Assert.Equal("known-song", metadata.ContentGroupId);
        Assert.Equal("Known Song", metadata.Title);
        Assert.Equal("Catalog Artist", metadata.Artist);
    }

    [Fact]
    public async Task Initialize_DuplicateTitlesRequireReviewAndOverlappingSpecificTitleResolves()
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(
            Song("cold-american-dreams-2", "Cold", "American Dreams 2"),
            Song("cold-future-project", "Cold", "Future Project"),
            Song("cold-heart", "Cold Heart"));
        fixture.AddSource("Performance Videos", "Cold - Performance.mp4");
        fixture.AddSource("Performance Videos", "Cold Heart - Performance.mp4");

        MetadataInitializationResult result = await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);

        Assert.Equal(1, result.ReviewRequired);
        Assert.Equal("cold-heart", result.Assets.Single(asset =>
            Path.GetFileName(asset.SourcePath) == "Cold Heart - Performance.mp4").ContentGroupId);
        Assert.Null(result.Assets.Single(asset =>
            Path.GetFileName(asset.SourcePath) == "Cold - Performance.mp4").ContentGroupId);
    }

    [Theory]
    [InlineData("Content Template POV1-Cash Rules.mp4", "pov")]
    [InlineData("Content Template Stock10 - Cash Rules.mp4", "stock")]
    [InlineData("Content Template MicDrop2 - Cash Rules.mp4", "mic-drop")]
    [InlineData("BTS Cash Rules.mp4", "behind-the-scenes")]
    public async Task Initialize_DescriptorInVlogFolderKeepsDirectoryBackedType(
        string fileName,
        string expectedSubtype)
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song("cash-rules", "Cash Rules"));
        string source = fixture.AddSource("Vlog Episodes", fileName);

        MetadataInitializationResult result = await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);

        MetadataAssetResult asset = Assert.Single(result.Assets);
        Assert.Equal(MetadataInitializationStatus.NonSong, asset.Status);
        Assert.Equal(AssetTypes.Vlog, asset.Type);
        Assert.Equal(expectedSubtype, asset.Subtype);
        Assert.Null(asset.ContentGroupId);
        AssetMetadata metadata = fixture.MetadataStore.Read(source);
        Assert.Equal(AssetTypes.Vlog, metadata.Type);
        Assert.Equal(expectedSubtype, metadata.Subtype);
    }

    [Fact]
    public async Task Initialize_UnknownSongRemainsUnresolved()
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song("free-fallin", "Free Fallin"));
        string source = fixture.AddSource("Performance Videos", "Unknown Performance.mp4");

        MetadataInitializationResult result = await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);

        MetadataAssetResult asset = Assert.Single(result.Assets);
        Assert.Equal(MetadataInitializationStatus.Unresolved, asset.Status);
        Assert.Null(asset.ContentGroupId);
        Assert.True(File.Exists(AssetMetadataStore.GetMetadataPath(source)));
    }

    [Fact]
    public async Task Initialize_NonSongProgrammingDoesNotInventContentGroup()
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song("episode-one", "Episode One"));
        string source = fixture.AddSource("Vlog Episodes", "Episode One.mp4");

        MetadataInitializationResult result = await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);

        MetadataAssetResult asset = Assert.Single(result.Assets);
        Assert.Equal(MetadataInitializationStatus.NonSong, asset.Status);
        AssetMetadata metadata = fixture.MetadataStore.Read(source);
        Assert.Equal(AssetTypes.Vlog, metadata.Type);
        Assert.Null(metadata.ContentGroupId);
    }

    [Theory]
    [InlineData("Promos", AssetTypes.Promo)]
    [InlineData("Specials", AssetTypes.Special)]
    public async Task Initialize_MultiSongNonSongAssetMayRemainUngrouped(string category, string type)
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song("cash-rules", "Cash Rules"));
        string source = fixture.AddSource(category, "Album Medley Teaser.mp4");

        await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);

        AssetMetadata metadata = fixture.MetadataStore.Read(source);
        Assert.Equal(type, metadata.Type);
        Assert.Null(metadata.ContentGroupId);
        Assert.Equal("Album Medley Teaser", metadata.Title);
    }

    [Fact]
    public async Task Edit_WrongFolderTypePreservesAssetIdAndDoesNotTouchMediaOrEncodingState()
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog();
        string source = fixture.AddSource("Vlog Episodes", "Season 2 promo.mp4", libraryExists: true);
        string library = fixture.GetLibraryPath(source);
        string technicalManifest = SourceManifestStore.GetManifestPath(library);
        File.WriteAllText(technicalManifest, "technical normalization state");
        await fixture.MetadataStore.WriteAsync(
            source,
            Metadata("stable-season-2-promo", null, AssetTypes.Vlog),
            CancellationToken.None);
        string sourceMediaBefore = File.ReadAllText(source);
        string libraryMediaBefore = File.ReadAllText(library);
        string technicalManifestBefore = File.ReadAllText(technicalManifest);

        AssetMetadata updated = await new MetadataEditor(fixture.MetadataStore).UpdateTypeAsync(
            source,
            AssetTypes.Promo,
            subtype: null,
            CancellationToken.None);

        Assert.Equal("stable-season-2-promo", updated.AssetId);
        Assert.Equal(AssetTypes.Promo, updated.Type);
        Assert.Null(updated.ContentGroupId);
        Assert.Equal(sourceMediaBefore, File.ReadAllText(source));
        Assert.Equal(libraryMediaBefore, File.ReadAllText(library));
        Assert.Equal(technicalManifestBefore, File.ReadAllText(technicalManifest));
        Assert.False(File.Exists(AssetMetadataStore.GetMetadataPath(library)));
    }

    [Theory]
    [InlineData(AssetTypes.Visualizer)]
    [InlineData(AssetTypes.AnimatedVisual)]
    public async Task Edit_AcceptsNewSongBasedTypesAndPreservesRelationship(string type)
    {
        using var fixture = new MetadataFixture();
        string source = fixture.AddSource("Music Videos", "Cash Rules.mp4");
        await fixture.MetadataStore.WriteAsync(
            source,
            Metadata("cash-rules-presentation", "cash-rules", AssetTypes.MusicVideo),
            CancellationToken.None);

        AssetMetadata updated = await new MetadataEditor(fixture.MetadataStore).UpdateTypeAsync(
            source,
            type,
            subtype: null,
            CancellationToken.None);

        Assert.Equal(type, updated.Type);
        Assert.Equal("cash-rules-presentation", updated.AssetId);
        Assert.Equal("cash-rules", updated.ContentGroupId);
    }

    [Fact]
    public async Task Initialize_ExistingResolvedRelationshipUsesCanonicalIdentityWithoutTouchingEncoding()
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(
            Song("human-choice", "Human Choice", artist: "Catalog Artist"),
            Song("filename-choice", "Filename Choice"));
        string source = fixture.AddSource(
            "Music Videos",
            "Filename Choice Official Video.mp4",
            libraryExists: true);
        string library = fixture.GetLibraryPath(source);
        string technicalManifest = SourceManifestStore.GetManifestPath(library);
        File.WriteAllText(technicalManifest, "technical normalization state");
        var metadata = new AssetMetadata
        {
            AssetId = "stable-manual-id",
            ContentGroupId = "human-choice",
            Title = "Custom Presentation Title",
            Artist = "Custom Artist",
            Type = AssetTypes.MusicVideo,
            RotationStartDate = new DateOnly(2026, 9, 27),
            Enabled = false,
            Tags = ["manual"],
        };
        await fixture.MetadataStore.WriteAsync(source, metadata, CancellationToken.None);
        string sourceMediaBefore = File.ReadAllText(source);
        string libraryMediaBefore = File.ReadAllText(library);
        string technicalManifestBefore = File.ReadAllText(technicalManifest);

        MetadataInitializationResult result = await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);

        MetadataAssetResult asset = Assert.Single(result.Assets);
        Assert.Equal(MetadataInitializationStatus.Preserved, asset.Status);
        AssetMetadata actual = fixture.MetadataStore.Read(source);
        Assert.Equal("stable-manual-id", actual.AssetId);
        Assert.Equal("human-choice", actual.ContentGroupId);
        Assert.Equal("Human Choice", actual.Title);
        Assert.Equal("Catalog Artist", actual.Artist);
        Assert.Equal(new DateOnly(2026, 9, 27), actual.RotationStartDate);
        Assert.False(actual.Enabled);
        Assert.Equal(["manual"], actual.Tags);
        Assert.Equal("Human Choice", fixture.MetadataStore.Read(library).Title);
        Assert.Equal(sourceMediaBefore, File.ReadAllText(source));
        Assert.Equal(libraryMediaBefore, File.ReadAllText(library));
        Assert.Equal(technicalManifestBefore, File.ReadAllText(technicalManifest));
    }

    [Fact]
    public async Task Initialize_NewCatalogMatchResolvesExistingUnresolvedMetadataWithoutChangingAssetId()
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song("other-song", "Other Song"));
        string source = fixture.AddSource("Performance Videos", "Free Fallin Performance.mp4");
        await fixture.MetadataStore.WriteAsync(
            source,
            Metadata("stable-unresolved-id", null, AssetTypes.Performance),
            CancellationToken.None);
        fixture.WriteCatalog(Song("free-fallin", "Free Fallin"));

        MetadataInitializationResult result = await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);

        MetadataAssetResult asset = Assert.Single(result.Assets);
        Assert.Equal(MetadataInitializationStatus.Resolved, asset.Status);
        AssetMetadata actual = fixture.MetadataStore.Read(source);
        Assert.Equal("stable-unresolved-id", actual.AssetId);
        Assert.Equal("free-fallin", actual.ContentGroupId);
        Assert.Equal("Free Fallin", actual.Title);
        Assert.Equal("Nzyte", actual.Artist);
    }

    [Fact]
    public async Task Initialize_Twice_IsIdempotent()
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song("cash-rules", "Cash Rules"));
        string source = fixture.AddSource("Lyric Videos", "Cash Rules Lyric Video.mp4", libraryExists: true);

        MetadataInitializer initializer = fixture.CreateInitializer();
        MetadataInitializationResult first = await initializer.InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);
        string sourceMetadata = File.ReadAllText(AssetMetadataStore.GetMetadataPath(source));
        string libraryMetadata = File.ReadAllText(AssetMetadataStore.GetMetadataPath(
            fixture.GetLibraryPath(source)));

        MetadataInitializationResult second = await initializer.InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);

        Assert.Equal(1, first.MetadataCreated);
        Assert.Equal(0, second.MetadataCreated);
        Assert.Equal(1, second.ExistingMetadataPreserved);
        Assert.Equal(sourceMetadata, File.ReadAllText(AssetMetadataStore.GetMetadataPath(source)));
        Assert.Equal(libraryMetadata, File.ReadAllText(AssetMetadataStore.GetMetadataPath(
            fixture.GetLibraryPath(source))));
    }

    [Fact]
    public async Task Initialize_DryRunWritesNothing()
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song("cash-rules", "Cash Rules"));
        string source = fixture.AddSource("Music Videos", "Cash Rules Official Video.mp4", libraryExists: true);
        string library = fixture.GetLibraryPath(source);

        MetadataInitializationResult result = await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: true,
            CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.Equal(1, result.MetadataCreated);
        Assert.False(File.Exists(AssetMetadataStore.GetMetadataPath(source)));
        Assert.False(File.Exists(AssetMetadataStore.GetMetadataPath(library)));
    }

    [Fact]
    public async Task Initialize_DryRunDoesNotResolveExistingMetadataOnDisk()
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song("free-fallin", "Free Fallin"));
        string source = fixture.AddSource("Performance Videos", "Free Fallin Performance.mp4", libraryExists: true);
        AssetMetadata unresolved = Metadata("stable-unresolved", null, AssetTypes.Performance);
        await fixture.MetadataStore.WriteAsync(source, unresolved, CancellationToken.None);
        string metadataBefore = File.ReadAllText(AssetMetadataStore.GetMetadataPath(source));

        MetadataInitializationResult result = await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: true,
            CancellationToken.None);

        MetadataAssetResult planned = Assert.Single(result.Assets);
        Assert.Equal(MetadataInitializationStatus.Resolved, planned.Status);
        Assert.Equal("free-fallin", planned.ContentGroupId);
        Assert.Equal(metadataBefore, File.ReadAllText(AssetMetadataStore.GetMetadataPath(source)));
        Assert.False(File.Exists(AssetMetadataStore.GetMetadataPath(fixture.GetLibraryPath(source))));
    }

    [Fact]
    public async Task Sync_UpdatesLibraryMetadataWithoutChangingMediaOrTechnicalManifest()
    {
        using var fixture = new MetadataFixture();
        string source = fixture.AddSource("Music Videos", "Cash Rules.mp4", libraryExists: true);
        string library = fixture.GetLibraryPath(source);
        string manifest = SourceManifestStore.GetManifestPath(library);
        File.WriteAllText(manifest, "technical-state");
        var original = Metadata("asset-1", "cash-rules", AssetTypes.MusicVideo, enabled: true);
        await fixture.MetadataStore.WriteAsync(source, original, CancellationToken.None);
        string sourceMediaBefore = File.ReadAllText(source);
        string libraryMediaBefore = File.ReadAllText(library);

        MetadataSyncResult first = await fixture.Synchronizer.SynchronizeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            dryRun: false,
            CancellationToken.None);
        var changed = new AssetMetadata
        {
            AssetId = original.AssetId,
            ContentGroupId = original.ContentGroupId,
            Title = "Updated Presentation",
            Artist = original.Artist,
            Type = original.Type,
            RotationStartDate = new DateOnly(2026, 9, 27),
            Enabled = false,
            SeriesId = original.SeriesId,
            EpisodeNumber = original.EpisodeNumber,
            Tags = ["updated"],
        };
        await fixture.MetadataStore.WriteAsync(source, changed, CancellationToken.None);
        MetadataSyncResult second = await fixture.Synchronizer.SynchronizeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            dryRun: false,
            CancellationToken.None);

        Assert.Equal(MetadataSyncStatus.Synchronized, Assert.Single(first.Files).Status);
        Assert.Equal(MetadataSyncStatus.Synchronized, Assert.Single(second.Files).Status);
        AssetMetadata libraryMetadata = fixture.MetadataStore.Read(library);
        Assert.Equal("Updated Presentation", libraryMetadata.Title);
        Assert.False(libraryMetadata.Enabled);
        Assert.Equal(sourceMediaBefore, File.ReadAllText(source));
        Assert.Equal(libraryMediaBefore, File.ReadAllText(library));
        Assert.Equal("technical-state", File.ReadAllText(manifest));
    }

    [Fact]
    public async Task Sync_DestinationCollisionDoesNotOverwriteLibraryMetadata()
    {
        using var fixture = new MetadataFixture();
        string firstSource = fixture.AddSource("Specials", "Shared.mov");
        string secondSource = fixture.AddSource("Specials", "Shared.mkv", libraryExists: true);
        string library = fixture.GetLibraryPath(firstSource);
        Assert.Equal(library, fixture.GetLibraryPath(secondSource));
        await fixture.MetadataStore.WriteAsync(
            firstSource,
            Metadata("first-asset", null, AssetTypes.Special),
            CancellationToken.None);
        await fixture.MetadataStore.WriteAsync(
            secondSource,
            Metadata("second-asset", null, AssetTypes.Special),
            CancellationToken.None);

        MetadataSyncResult result = await fixture.Synchronizer.SynchronizeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            dryRun: false,
            CancellationToken.None);

        Assert.Equal(2, result.Files.Count);
        Assert.All(result.Files, file => Assert.Equal(MetadataSyncStatus.Error, file.Status));
        Assert.Equal(1, result.ExitCode);
        Assert.False(File.Exists(AssetMetadataStore.GetMetadataPath(library)));
    }

    [Fact]
    public async Task Review_ExplicitSelectionPreservesAssetIdAndSynchronizesLibrary()
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(
            Song("cold-project-a", "Cold", "Project A", artist: "Project A Artist"),
            Song("cold-project-b", "Cold", "Project B", artist: "Project B Artist"));
        string source = fixture.AddSource("Performance Videos", "Cold - Performance.mp4", libraryExists: true);
        AssetMetadata metadata = Metadata("stable-cold-performance", null, AssetTypes.Performance);
        await fixture.MetadataStore.WriteAsync(source, metadata, CancellationToken.None);
        var prompt = new SelectingPrompt("cold-project-a");
        var reviewer = new MetadataReviewer(
            new SongCatalogStore(),
            fixture.Discovery,
            fixture.MetadataStore,
            fixture.Synchronizer,
            prompt);

        MetadataReviewResult result = await reviewer.ReviewAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            CancellationToken.None);

        MetadataReviewFileResult reviewed = Assert.Single(result.Files);
        Assert.True(reviewed.Resolved);
        Assert.Equal("stable-cold-performance", reviewed.AssetId);
        Assert.Equal("cold-project-a", reviewed.ContentGroupId);
        AssetMetadata sourceMetadata = fixture.MetadataStore.Read(source);
        Assert.Equal("cold-project-a", sourceMetadata.ContentGroupId);
        Assert.Equal("Cold", sourceMetadata.Title);
        Assert.Equal("Project A Artist", sourceMetadata.Artist);
        Assert.Equal("stable-cold-performance", sourceMetadata.AssetId);
        AssetMetadata libraryMetadata = fixture.MetadataStore.Read(fixture.GetLibraryPath(source));
        Assert.Equal("cold-project-a", libraryMetadata.ContentGroupId);
        Assert.Equal("Cold", libraryMetadata.Title);
        Assert.Equal("Project A Artist", libraryMetadata.Artist);
    }

    [Fact]
    public async Task Rebind_PreservesAssetIdentityAndRelationshipAfterRename()
    {
        using var fixture = new MetadataFixture();
        string oldSource = fixture.AddSource("Music Videos", "Old Name.mp4");
        string newSource = Path.Combine(fixture.SourceRoot, "Music Videos", "New Name.mp4");
        File.WriteAllText(newSource, "renamed source");
        await fixture.MetadataStore.WriteAsync(
            oldSource,
            Metadata("stable-id", "free-fallin", AssetTypes.MusicVideo),
            CancellationToken.None);

        new MetadataRebinder(fixture.MetadataStore).Rebind(oldSource, newSource);

        Assert.False(File.Exists(AssetMetadataStore.GetMetadataPath(oldSource)));
        AssetMetadata rebound = fixture.MetadataStore.Read(newSource);
        Assert.Equal("stable-id", rebound.AssetId);
        Assert.Equal("free-fallin", rebound.ContentGroupId);
    }

    [Fact]
    public async Task Initialize_ReportsOrphanedMetadataWithoutDeletingIt()
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song("free-fallin", "Free Fallin"));
        string missingSource = Path.Combine(fixture.SourceRoot, "Music Videos", "Missing.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(missingSource)!);
        await fixture.MetadataStore.WriteAsync(
            missingSource,
            Metadata("missing-asset", "free-fallin", AssetTypes.MusicVideo),
            CancellationToken.None);

        MetadataInitializationResult result = await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);

        MetadataAssetResult orphan = Assert.Single(result.Assets);
        Assert.Equal(MetadataInitializationStatus.Orphaned, orphan.Status);
        Assert.True(File.Exists(AssetMetadataStore.GetMetadataPath(missingSource)));
    }

    [Fact]
    public async Task Initialize_DuplicateAssetIdSharedWithOrphanIsReportedWithoutDeletion()
    {
        using var fixture = new MetadataFixture();
        fixture.WriteCatalog(Song("free-fallin", "Free Fallin"));
        string source = fixture.AddSource("Music Videos", "Free Fallin.mp4");
        string missingSource = Path.Combine(fixture.SourceRoot, "Music Videos", "Missing.mp4");
        await fixture.MetadataStore.WriteAsync(
            source,
            Metadata("duplicate-id", "free-fallin", AssetTypes.MusicVideo),
            CancellationToken.None);
        await fixture.MetadataStore.WriteAsync(
            missingSource,
            Metadata("duplicate-id", "free-fallin", AssetTypes.MusicVideo),
            CancellationToken.None);

        MetadataInitializationResult result = await fixture.CreateInitializer().InitializeAsync(
            fixture.SourceRoot,
            fixture.LibraryRoot,
            fixture.CatalogPath,
            dryRun: false,
            CancellationToken.None);

        Assert.Equal(2, result.Errors);
        Assert.All(result.Assets, asset => Assert.Equal(MetadataInitializationStatus.Error, asset.Status));
        Assert.True(File.Exists(AssetMetadataStore.GetMetadataPath(source)));
        Assert.True(File.Exists(AssetMetadataStore.GetMetadataPath(missingSource)));
    }

    private static SongCatalogEntry Song(
        string id,
        string title,
        string? project = null,
        string artist = "Nzyte",
        List<string>? aliases = null) => new()
        {
            ContentGroupId = id,
            Title = title,
            Artist = artist,
            Project = project,
            Aliases = aliases ?? [],
        };

    private static AssetMetadata Metadata(string assetId, string? groupId, string type, bool enabled = true) => new()
    {
        AssetId = assetId,
        ContentGroupId = groupId,
        Title = "Presentation Title",
        Artist = "Nzyte",
        Type = type,
        Enabled = enabled,
        Tags = [],
    };

    private sealed class SelectingPrompt(string selection) : IMetadataReviewPrompt
    {
        public Task<string?> SelectContentGroupAsync(
            MetadataReviewRequest request,
            CancellationToken cancellationToken)
        {
            Assert.Contains(request.Candidates, candidate => candidate.ContentGroupId == selection);
            return Task.FromResult<string?>(selection);
        }
    }

    private sealed class MetadataFixture : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        private readonly string _root = Directory.CreateTempSubdirectory("nzytetv-metadata-").FullName;

        public MetadataFixture()
        {
            Directory.CreateDirectory(SourceRoot);
            Discovery = new MetadataAssetDiscovery();
            MetadataStore = new AssetMetadataStore();
            Synchronizer = new MetadataSynchronizer(Discovery, MetadataStore);
        }

        public string SourceRoot => Path.Combine(_root, "source");

        public string LibraryRoot => Path.Combine(_root, "library");

        public string CatalogPath => Path.Combine(_root, "catalog", "songs.json");

        public MetadataAssetDiscovery Discovery { get; }

        public AssetMetadataStore MetadataStore { get; }

        public MetadataSynchronizer Synchronizer { get; }

        public void WriteCatalog(params SongCatalogEntry[] songs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
            var catalog = new SongCatalog
            {
                SchemaVersion = SongCatalog.CurrentSchemaVersion,
                Songs = [.. songs],
            };
            File.WriteAllText(CatalogPath, JsonSerializer.Serialize(catalog, JsonOptions));
        }

        public string AddSource(string category, string fileName, bool libraryExists = false)
        {
            string source = Path.Combine(SourceRoot, category, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "source media");
            if (libraryExists)
            {
                string library = GetLibraryPath(source);
                Directory.CreateDirectory(Path.GetDirectoryName(library)!);
                File.WriteAllText(library, "normalized media");
            }

            return source;
        }

        public string GetLibraryPath(string source) =>
            LibraryPathPolicy.GetDestinationPath(SourceRoot, LibraryRoot, source);

        public MetadataInitializer CreateInitializer() => new(
            new SongCatalogStore(),
            Discovery,
            MetadataStore,
            Synchronizer);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
