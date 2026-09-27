using NzyteTv.Core;

namespace NzyteTv.Core.Tests;

public sealed class SongMatcherTests
{
    [Fact]
    public void Match_ExactTitleSequence_Resolves()
    {
        SongMatchResult result = SongMatcher.Match(
            "Nzyte - Free Fallin (Official Music Video).mp4",
            Catalog(Song("free-fallin", "Free Fallin")));

        Assert.Equal(SongMatchStatus.Matched, result.Status);
        Assert.Equal("free-fallin", result.Match!.ContentGroupId);
    }

    [Fact]
    public void Match_IsCaseAndPunctuationInsensitive()
    {
        SongMatchResult result = SongMatcher.Match(
            "Nzyte - CASH_RULES [Lyric Video].MP4",
            Catalog(Song("cash-rules", "Cash Rules")));

        Assert.Equal(SongMatchStatus.Matched, result.Status);
        Assert.Equal("cash-rules", result.Match!.ContentGroupId);
    }

    [Fact]
    public void Match_Alias_ResolvesToCanonicalSong()
    {
        SongMatchResult result = SongMatcher.Match(
            "Free Falling - Visualizer.mp4",
            Catalog(Song("free-fallin", "Free Fallin", aliases: ["Free Falling"])));

        Assert.Equal(SongMatchStatus.Matched, result.Status);
        Assert.Equal("free-fallin", result.Match!.ContentGroupId);
    }

    [Fact]
    public void Match_RealFilenameAlias_ResolvesToOfficialCanonicalTitle()
    {
        SongMatchResult result = SongMatcher.Match(
            "I Did It 4 U.mp4",
            Catalog(Song("i-did-it-for-you", "I Did It For You", aliases: ["I Did It 4 U"])));

        Assert.Equal(SongMatchStatus.Matched, result.Status);
        Assert.Equal("i-did-it-for-you", result.Match!.ContentGroupId);
        Assert.Equal("I Did It For You", result.Match.Title);
    }

    [Fact]
    public void Match_AcronymAliasWithKnownContentSuffix_Resolves()
    {
        SongMatchResult result = SongMatcher.Match(
            "WALTW Content 15.mp4",
            Catalog(Song("we-are-living-this-way", "We Are Living This Way", aliases: ["WALTW"])));

        Assert.Equal(SongMatchStatus.Matched, result.Status);
        Assert.Equal("we-are-living-this-way", result.Match!.ContentGroupId);
    }

    [Theory]
    [InlineData("Content Template POV1-Cash Rules.mp4")]
    [InlineData("Content Template Stock10 - Cash Rules.mp4")]
    [InlineData("Content Template MicDrop2 - Cash Rules.mp4")]
    [InlineData("BTS Cash Rules.mp4")]
    public void Match_KnownShortFormDescriptor_ResolvesSong(string fileName)
    {
        SongMatchResult result = SongMatcher.Match(
            fileName,
            Catalog(Song("cash-rules", "Cash Rules")));

        Assert.Equal(SongMatchStatus.Matched, result.Status);
        Assert.Equal("cash-rules", result.Match!.ContentGroupId);
    }

    [Theory]
    [InlineData("Content Template POV1-Cash Rules.mp4", "pov", 1)]
    [InlineData("Content Template Lipsync1 -Cash Rules.mp4", "lipsync", 1)]
    [InlineData("Content Template Stock10 - Cash Rules.mp4", "stock", 10)]
    [InlineData("Content Template MicDrop2 - Cash Rules.mp4", "mic-drop", 2)]
    [InlineData("BTS Cash Rules.mp4", "behind-the-scenes", null)]
    [InlineData("Content Template Thought1 - Cash Rules.mp4", "thought", 1)]
    [InlineData("Content Template Meme5 - Cash Rules.mp4", "meme", 5)]
    [InlineData("Content Template AI1 - Cash Rules.mp4", "ai-visual", 1)]
    public void ShortFormDescriptor_DetectsExtensibleSubtype(
        string fileName,
        string expectedSubtype,
        int? expectedSequence)
    {
        ShortFormDescriptor descriptor = Assert.IsType<ShortFormDescriptor>(
            ShortFormDescriptorDetector.Detect(fileName));

        Assert.Equal(expectedSubtype, descriptor.Subtype);
        Assert.Equal(expectedSequence, descriptor.SequenceNumber);
    }

    [Fact]
    public void Match_ShortAcronymEmbeddedInsideWord_DoesNotMatch()
    {
        SongMatchResult result = SongMatcher.Match(
            "Adventure Official Video.mp4",
            Catalog(Song("after-dark", "After Dark", aliases: ["AD"])));

        Assert.Equal(SongMatchStatus.Unresolved, result.Status);
    }

    [Fact]
    public void Match_DuplicateAcronymAlias_RequiresReview()
    {
        SongMatchResult result = SongMatcher.Match(
            "TM Content 3.mp4",
            Catalog(
                Song("this-moment", "This Moment", aliases: ["TM"]),
                Song("true-magic", "True Magic", aliases: ["TM"])));

        Assert.Equal(SongMatchStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains("alias", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Match_CanonicalTitleAndAnotherSongsAlias_RequiresReview()
    {
        SongCatalog catalog = Catalog(
            Song("free-fallin", "Free Fallin"),
            Song("different-recording", "Different Recording", aliases: ["Free Fallin"]));

        SongMatchResult result = SongMatcher.Match("Free Fallin Official Video.mp4", catalog);

        Assert.Equal(SongMatchStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Match_DuplicateTitles_RequiresReview()
    {
        SongCatalog catalog = Catalog(
            Song("cold-american-dreams-2", "Cold", "American Dreams 2"),
            Song("cold-future-project", "Cold", "Future Project"));

        SongMatchResult result = SongMatcher.Match("Cold - Performance.mp4", catalog);

        Assert.Equal(SongMatchStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Match_OverlappingTitles_PrefersSpecificExactTitle()
    {
        SongCatalog catalog = Catalog(
            Song("cold", "Cold"),
            Song("cold-heart", "Cold Heart"));

        SongMatchResult result = SongMatcher.Match("Cold Heart - Performance.mp4", catalog);

        Assert.Equal(SongMatchStatus.Matched, result.Status);
        Assert.Equal("cold-heart", result.Match!.ContentGroupId);
    }

    [Fact]
    public void Match_UnknownSong_RemainsUnresolved()
    {
        SongMatchResult result = SongMatcher.Match(
            "Unknown Performance.mp4",
            Catalog(Song("free-fallin", "Free Fallin")));

        Assert.Equal(SongMatchStatus.Unresolved, result.Status);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void CategoryMap_RecognizesAllSupportedProductionCategories()
    {
        string root = Path.GetFullPath(Path.Combine("root", "source"));

        foreach ((string category, string type) in AssetCategoryMap.CategoryMappings)
        {
            Assert.True(AssetCategoryMap.TryDetect(
                root,
                Path.Combine(root, category, "asset.mp4"),
                out string? detected));
            Assert.Equal(type, detected);
        }
    }

    [Fact]
    public void AssetIds_SameSongVariantsAreUniqueAndReadable()
    {
        SongCatalogEntry song = Song("free-fallin", "Free Fallin");
        var ids = new HashSet<string>(StringComparer.Ordinal);

        string musicVideo = AssetIdGenerator.Generate(
            "Nzyte - Free Fallin Official Video.mp4",
            "Music Videos/Nzyte - Free Fallin Official Video.mp4",
            AssetTypes.MusicVideo,
            song,
            ids);
        string lyricVideo = AssetIdGenerator.Generate(
            "Free Fallin Lyric Video.mp4",
            "Lyric Videos/Free Fallin Lyric Video.mp4",
            AssetTypes.LyricVideo,
            song,
            ids);
        string performance = AssetIdGenerator.Generate(
            "Free Fallin - Krog Street Performance.mp4",
            "Performance Videos/Free Fallin - Krog Street Performance.mp4",
            AssetTypes.Performance,
            song,
            ids);

        Assert.Equal("free-fallin-music-video", musicVideo);
        Assert.Equal("free-fallin-lyric-video", lyricVideo);
        Assert.Equal("free-fallin-performance-krog-street", performance);
        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public void AssetIds_CollisionsReceiveDeterministicPathBasedSuffix()
    {
        const string relativePath = "Specials/Archive/Station ID.mp4";
        var firstRun = new HashSet<string>(StringComparer.Ordinal) { "station-id-special" };
        var secondRun = new HashSet<string>(StringComparer.Ordinal) { "station-id-special" };

        string first = AssetIdGenerator.Generate(
            "Station ID.mp4",
            relativePath,
            AssetTypes.Special,
            song: null,
            firstRun);
        string second = AssetIdGenerator.Generate(
            "Station ID.mp4",
            relativePath,
            AssetTypes.Special,
            song: null,
            secondRun);

        Assert.Equal(first, second);
        Assert.StartsWith("station-id-special-", first, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetIds_ShortFormIncludesSubtypeAndSequence()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        ShortFormDescriptor descriptor = Assert.IsType<ShortFormDescriptor>(
            ShortFormDescriptorDetector.Detect("Content Template POV1-Cash Rules.mp4"));

        string assetId = AssetIdGenerator.Generate(
            "Content Template POV1-Cash Rules.mp4",
            "Vlog Episodes/Content Template POV1-Cash Rules.mp4",
            AssetTypes.ShortForm,
            Song("cash-rules", "Cash Rules"),
            ids,
            descriptor);

        Assert.Equal("cash-rules-pov-01", assetId);
    }

    internal static SongCatalog Catalog(params SongCatalogEntry[] songs) => new()
    {
        SchemaVersion = SongCatalog.CurrentSchemaVersion,
        Songs = [.. songs],
    };

    internal static SongCatalogEntry Song(
        string id,
        string title,
        string? project = null,
        List<string>? aliases = null) => new()
        {
            ContentGroupId = id,
            Title = title,
            Artist = "Nzyte",
            Project = project,
            Aliases = aliases ?? [],
        };
}
