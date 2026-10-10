using System.Text.Json;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class SourceManifestStoreTests
{
    [Theory]
    [InlineData(null, VerticalLayoutMode.None)]
    [InlineData("", VerticalLayoutMode.None)]
    [InlineData("   ", VerticalLayoutMode.None)]
    [InlineData("none", VerticalLayoutMode.None)]
    [InlineData("blurred-background", VerticalLayoutMode.BlurredBackground)]
    public async Task ReadVerticalLayout_AcceptsOnlySupportedCanonicalValues(
        string? manifestValue,
        VerticalLayoutMode expected)
    {
        string root = Directory.CreateTempSubdirectory("nzytetv-manifest-layout-").FullName;
        try
        {
            string destination = Path.Combine(root, "library", "video.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, "normalized");
            var store = new SourceManifestStore();
            await store.WriteAsync(
                destination,
                new SourceFingerprint(1, "video.mov", 1, DateTime.UnixEpoch, "test", manifestValue),
                CancellationToken.None);

            Assert.Equal(expected, store.ReadVerticalLayout(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadVerticalLayout_RejectsUnsupportedAndMalformedManifests()
    {
        string root = Directory.CreateTempSubdirectory("nzytetv-manifest-layout-").FullName;
        try
        {
            string destination = Path.Combine(root, "library", "video.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, "normalized");
            var store = new SourceManifestStore();
            await store.WriteAsync(
                destination,
                new SourceFingerprint(
                    1,
                    "video.mov",
                    1,
                    DateTime.UnixEpoch,
                    "test",
                    "future-layout"),
                CancellationToken.None);
            Assert.Throws<InvalidDataException>(() => store.ReadVerticalLayout(destination));

            File.WriteAllText(SourceManifestStore.GetManifestPath(destination), "{ malformed");
            Assert.Throws<InvalidDataException>(() => store.ReadVerticalLayout(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_LegacyManifestWithoutVerticalLayoutMatchesOnlyStandardMode()
    {
        string root = Directory.CreateTempSubdirectory("nzytetv-manifest-").FullName;
        try
        {
            string source = Path.Combine(root, "source", "portrait.mp4");
            string destination = Path.Combine(root, "library", "portrait.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(source, "source");
            File.WriteAllText(destination, "normalized");
            var store = new SourceManifestStore();
            SourceFingerprint standard = store.CreateFingerprint(Path.GetDirectoryName(source)!, source);
            string legacyJson = JsonSerializer.Serialize(new
            {
                standard.SchemaVersion,
                standard.SourceRelativePath,
                standard.SourceSize,
                standard.SourceLastModifiedUtc,
                standard.BroadcastProfileVersion,
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            File.WriteAllText(SourceManifestStore.GetManifestPath(destination), legacyJson);

            ManifestMatchResult standardMatch = store.Evaluate(destination, standard);
            SourceFingerprint blurred = store.CreateFingerprint(
                Path.GetDirectoryName(source)!,
                source,
                new NormalizationOptions { VerticalLayout = VerticalLayoutMode.BlurredBackground });
            ManifestMatchResult blurredMatch = store.Evaluate(destination, blurred);

            Assert.True(standardMatch.IsMatch);
            Assert.False(blurredMatch.IsMatch);
            Assert.Contains("Vertical layout changed", blurredMatch.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
