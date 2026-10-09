using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class AssetMetadataGenerationInventoryTests
{
    [Fact]
    public void Inventory_IsStrictDeterministicAndContainsNoAbsoluteIdentity()
    {
        AssetMetadataGenerationInventory inventory = ValidInventory();
        string json = AssetMetadataGenerationInventorySerializer.Serialize(inventory);
        AssetMetadataGenerationInventory roundTrip =
            AssetMetadataGenerationInventorySerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Assert.Single(roundTrip.Packages!).PackageId);
        Assert.DoesNotContain(Path.GetTempPath(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidDataException>(() => AssetMetadataGenerationInventorySerializer.Deserialize(
            System.Text.Encoding.UTF8.GetBytes(json.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1,\n  \"unexpected\": true,",
                StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => AssetMetadataGenerationInventorySerializer.Deserialize(
            System.Text.Encoding.UTF8.GetBytes(json.Replace(
                "\"revision\": 1,",
                "\"revision\": 1,\n  \"revision\": 1,",
                StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData("../escape.mov")]
    [InlineData("/rooted.mov")]
    [InlineData("C:\\rooted.mov")]
    [InlineData("Music Videos//empty.mov")]
    public void Inventory_RejectsUnsafeRelativeIdentity(string relative)
    {
        AssetMetadataGenerationInventory inventory = ValidInventory();
        AssetMetadataInventoryAsset asset = Assert.Single(inventory.Assets!);

        Assert.Throws<InvalidDataException>(() =>
            AssetMetadataGenerationInventorySerializer.NormalizeAndValidate(inventory with
            {
                Assets = [asset with { SourceRelativePath = relative }],
            }));
    }

    [Fact]
    public void Inventory_RejectsDuplicatePackagePathAndAssetIdentity()
    {
        AssetMetadataGenerationInventory inventory = ValidInventory();
        AssetMetadataInventoryPackage package = Assert.Single(inventory.Packages!);
        AssetMetadataInventoryAsset asset = Assert.Single(inventory.Assets!);

        Assert.Throws<InvalidDataException>(() =>
            AssetMetadataGenerationInventorySerializer.NormalizeAndValidate(inventory with
            {
                Packages = [package, package],
            }));
        Assert.Throws<InvalidDataException>(() =>
            AssetMetadataGenerationInventorySerializer.NormalizeAndValidate(inventory with
            {
                Assets = [asset, asset with { PackageId = null }],
            }));
    }

    private static AssetMetadataGenerationInventory ValidInventory()
    {
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        return new AssetMetadataGenerationInventory
        {
            Revision = 1,
            GenerationId = "000000000001",
            Packages =
            [
                new AssetMetadataInventoryPackage
                {
                    PackageId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    ManifestDigest = hash,
                    SourceRelativePath = "Music Videos/Song.mov",
                    LibraryRelativePath = "Music Videos/Song.mp4",
                    SourceSha256 = hash,
                    LibrarySha256 = hash,
                    TechnicalManifestSha256 = hash,
                },
            ],
            Assets =
            [
                new AssetMetadataInventoryAsset
                {
                    PackageId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    SourceRelativePath = "Music Videos/Song.mov",
                    LibraryRelativePath = "Music Videos/Song.mp4",
                    AssetId = "song-music-video",
                    ContentGroupId = "song",
                    TechnicalManifestSha256 = hash,
                    SourceMetadataSha256 = hash,
                    LibraryMetadataSha256 = hash,
                    Classification = AssetMetadataInventoryClassification.PlaylistEligible,
                },
            ],
        };
    }
}
