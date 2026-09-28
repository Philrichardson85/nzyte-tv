using NzyteTv.Core;

namespace NzyteTv.Core.Tests;

public sealed class AssetEligibilityTests
{
    [Fact]
    public void Evaluate_UnresolvedSongWithReadyEncoding_IsNotEligible()
    {
        AssetEligibilityResult result = AssetEligibilityEvaluator.Evaluate(
            Metadata(contentGroupId: null),
            SongMatcherTests.Catalog(SongMatcherTests.Song("free-fallin", "Free Fallin")),
            normalizedLibraryFileExists: true,
            technicalManifestExists: true,
            programmingMetadataExists: true);

        Assert.Equal(AssetEncodingStatus.Ready, result.EncodingStatus);
        Assert.Equal(AssetMetadataStatus.Unresolved, result.MetadataStatus);
        Assert.False(result.IsPlaylistEligible);
    }

    [Fact]
    public void Evaluate_ResolvedEnabledSongWithAllFiles_IsEligible()
    {
        AssetEligibilityResult result = AssetEligibilityEvaluator.Evaluate(
            Metadata("free-fallin"),
            SongMatcherTests.Catalog(SongMatcherTests.Song("free-fallin", "Free Fallin")),
            normalizedLibraryFileExists: true,
            technicalManifestExists: true,
            programmingMetadataExists: true);

        Assert.Equal(AssetMetadataStatus.Resolved, result.MetadataStatus);
        Assert.True(result.IsPlaylistEligible);
    }

    [Fact]
    public void Evaluate_DisabledAsset_IsNotEligible()
    {
        AssetEligibilityResult result = AssetEligibilityEvaluator.Evaluate(
            Metadata("free-fallin", enabled: false),
            SongMatcherTests.Catalog(SongMatcherTests.Song("free-fallin", "Free Fallin")),
            normalizedLibraryFileExists: true,
            technicalManifestExists: true,
            programmingMetadataExists: true);

        Assert.False(result.IsPlaylistEligible);
        Assert.Contains(result.Reasons, reason => reason.Contains("disabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_MissingTechnicalManifest_IsNotEligible()
    {
        AssetEligibilityResult result = AssetEligibilityEvaluator.Evaluate(
            Metadata("free-fallin"),
            SongMatcherTests.Catalog(SongMatcherTests.Song("free-fallin", "Free Fallin")),
            normalizedLibraryFileExists: true,
            technicalManifestExists: false,
            programmingMetadataExists: true);

        Assert.Equal(AssetEncodingStatus.Missing, result.EncodingStatus);
        Assert.False(result.IsPlaylistEligible);
    }

    [Theory]
    [InlineData(AssetTypes.Visualizer)]
    [InlineData(AssetTypes.AnimatedVisual)]
    public void Evaluate_NewSongTypesRequireValidContentGroup(string type)
    {
        SongCatalog catalog = SongMatcherTests.Catalog(SongMatcherTests.Song("free-fallin", "Free Fallin"));

        AssetEligibilityResult unresolved = AssetEligibilityEvaluator.Evaluate(
            Metadata(contentGroupId: null, type: type),
            catalog,
            normalizedLibraryFileExists: true,
            technicalManifestExists: true,
            programmingMetadataExists: true);
        AssetEligibilityResult resolved = AssetEligibilityEvaluator.Evaluate(
            Metadata("free-fallin", type: type),
            catalog,
            normalizedLibraryFileExists: true,
            technicalManifestExists: true,
            programmingMetadataExists: true);

        Assert.Equal(AssetMetadataStatus.Unresolved, unresolved.MetadataStatus);
        Assert.False(unresolved.IsPlaylistEligible);
        Assert.Equal(AssetMetadataStatus.Resolved, resolved.MetadataStatus);
        Assert.True(resolved.IsPlaylistEligible);
    }

    private static AssetMetadata Metadata(
        string? contentGroupId,
        bool enabled = true,
        string type = AssetTypes.Performance) =>
        new()
        {
            AssetId = "free-fallin-performance",
            ContentGroupId = contentGroupId,
            Title = "Free Fallin",
            Artist = "Nzyte",
            Type = type,
            Enabled = enabled,
            Tags = [],
        };
}
