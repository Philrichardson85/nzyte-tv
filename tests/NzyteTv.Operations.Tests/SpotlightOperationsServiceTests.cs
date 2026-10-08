using NzyteTv.Core;
using NzyteTv.Media;
using NzyteTv.Operations.Configuration;
using NzyteTv.Operations.Contracts;
using NzyteTv.Operations.Spotlight;

namespace NzyteTv.Operations.Tests;

public sealed class SpotlightOperationsServiceTests
{
    [Fact]
    public async Task DisabledStateAndValidatedCatalogAreReturned()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();

        SpotlightStateResponse state = fixture.Service.Get();

        Assert.False(state.Enabled);
        Assert.Null(state.ContentGroupId);
        Assert.Equal(ProgrammingConfiguration.DefaultCampaignMultiplier, state.WeightMultiplier);
        Assert.Equal(["free-fallin", "purple-rain"],
            state.CatalogOptions.Select(option => option.ContentGroupId).Order().ToArray());
    }

    [Fact]
    public async Task SetAndDisableReturnCurrentSafeStateAndRevision()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();

        SpotlightStateResponse set = await fixture.Service.SetAsync(
            new SetSpotlightRequest
            {
                ExpectedRevision = 1,
                ContentGroupId = "purple-rain",
                WeightMultiplier = OperationsProtocol.DefaultSpotlightMultiplier,
            },
            CancellationToken.None);
        SpotlightStateResponse disabled = await fixture.Service.DisableAsync(
            new DisableSpotlightRequest { ExpectedRevision = 2 },
            CancellationToken.None);

        Assert.True(set.Enabled);
        Assert.Equal("Purple Rain", set.Title);
        Assert.Equal("Prince", set.Artist);
        Assert.Equal(2, set.Revision);
        Assert.False(disabled.Enabled);
        Assert.Equal(3, disabled.Revision);
    }

    [Fact]
    public async Task UnknownCatalogContentGroupIsRejectedWithoutRevisionChange()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();

        await Assert.ThrowsAsync<OperationsValidationException>(() => fixture.Service.SetAsync(
            new SetSpotlightRequest
            {
                ExpectedRevision = 1,
                ContentGroupId = "unknown",
                WeightMultiplier = 2.0,
            },
            CancellationToken.None));

        Assert.Equal(1, fixture.Service.Get().Revision);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("nzyte-ops-").FullName;

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(_root, "catalog"));
            Directory.CreateDirectory(Path.Combine(_root, "library"));
            var options = new OperationsOptions
            {
                MediaRoot = _root,
                SocketPath = Path.Combine(_root, "operations.sock"),
            };
            var configurationStore = new ProgrammingConfigurationStore();
            var catalogStore = new SongCatalogStore();
            var programmingService = new ProgrammingService(configurationStore, catalogStore);
            Service = new SpotlightOperationsService(
                options, configurationStore, catalogStore, programmingService);
        }

        public SpotlightOperationsService Service { get; }

        public async Task InitializeAsync()
        {
            var catalog = new SongCatalog
            {
                SchemaVersion = SongCatalog.CurrentSchemaVersion,
                Songs =
                [
                    new SongCatalogEntry { ContentGroupId = "purple-rain", Title = "Purple Rain", Artist = "Prince" },
                    new SongCatalogEntry { ContentGroupId = "free-fallin", Title = "Free Fallin'", Artist = "Tom Petty" },
                ],
            };
            await new SongCatalogStore().WriteNewAsync(
                Path.Combine(_root, "catalog", "song-catalog.json"), catalog, CancellationToken.None);
            await AddAssetAsync("purple-rain", "purple-rain");
            await AddAssetAsync("free-fallin", "free-fallin");
            await new ProgrammingService().InitializeAsync(_root, CancellationToken.None);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);

        private async Task AddAssetAsync(string assetId, string contentGroupId)
        {
            string path = Path.Combine(_root, "library", "Music Videos", $"{assetId}.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "normalized");
            File.WriteAllText(SourceManifestStore.GetManifestPath(path), "manifest");
            await new AssetMetadataStore().WriteAsync(path, new AssetMetadata
            {
                AssetId = assetId,
                ContentGroupId = contentGroupId,
                Title = assetId,
                Artist = "NZYTE",
                Type = AssetTypes.MusicVideo,
                Enabled = true,
                Tags = [],
            }, CancellationToken.None);
        }
    }
}
