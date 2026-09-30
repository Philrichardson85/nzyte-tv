using System.Text.Json;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class ProgrammingWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Store_RoundTripsSchemaAndWritesAtomically()
    {
        using var fixture = new ProgrammingFixture();
        string path = fixture.ConfigurationPath;
        var store = new ProgrammingConfigurationStore();
        ProgrammingConfiguration configuration = ProgrammingConfiguration.CreateDefault() with
        {
            Revision = 7,
            AssetOverrides = new Dictionary<string, AssetEditorialOverride>
            {
                ["asset-one"] = new() { WeightMultiplier = 1.5 },
            },
        };

        await store.WriteAsync(path, configuration, CancellationToken.None);
        ProgrammingConfiguration roundTrip = store.Load(path);

        Assert.Equal(7, roundTrip.Revision);
        Assert.Equal(1.5, roundTrip.AssetOverrides!["asset-one"].WeightMultiplier);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.partial"));
    }

    [Fact]
    public async Task Store_InitIsIdempotentAndNeverOverwritesExistingConfiguration()
    {
        using var fixture = new ProgrammingFixture();
        var store = new ProgrammingConfigurationStore();

        ProgrammingConfigurationInitializationResult first = await store.InitializeAsync(
            fixture.ConfigurationPath,
            CancellationToken.None);
        string original = File.ReadAllText(fixture.ConfigurationPath);
        ProgrammingConfigurationInitializationResult second = await store.InitializeAsync(
            fixture.ConfigurationPath,
            CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(original, File.ReadAllText(fixture.ConfigurationPath));
    }

    [Theory]
    [InlineData("{ not-json")]
    [InlineData("{\"schemaVersion\":99,\"revision\":1}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    public void Store_RejectsMalformedUnsupportedOrDuplicateJson(string json)
    {
        using var fixture = new ProgrammingFixture();
        File.WriteAllText(fixture.ConfigurationPath, json);

        Exception exception = Assert.ThrowsAny<Exception>(() =>
            new ProgrammingConfigurationStore().Load(fixture.ConfigurationPath));

        Assert.True(exception is InvalidDataException or ProgrammingConfigurationValidationException);
    }

    [Fact]
    public async Task FailedAtomicWriteDoesNotCorruptPriorConfiguration()
    {
        using var fixture = new ProgrammingFixture();
        var goodStore = new ProgrammingConfigurationStore();
        await goodStore.WriteAsync(
            fixture.ConfigurationPath,
            ProgrammingConfiguration.CreateDefault(),
            CancellationToken.None);
        string original = File.ReadAllText(fixture.ConfigurationPath);
        var failingStore = new ProgrammingConfigurationStore(new ThrowingWriter());

        await Assert.ThrowsAsync<IOException>(() => failingStore.WriteAsync(
            fixture.ConfigurationPath,
            ProgrammingConfiguration.CreateDefault() with { Revision = 2 },
            CancellationToken.None));

        Assert.Equal(original, File.ReadAllText(fixture.ConfigurationPath));
        _ = goodStore.Load(fixture.ConfigurationPath);
    }

    [Fact]
    public async Task Service_ValidatesAndMutatesCampaignWithDeterministicRevision()
    {
        using var fixture = new ProgrammingFixture();
        await fixture.PrepareInventoryAsync();
        var service = new ProgrammingService();
        await service.InitializeAsync(fixture.Root, CancellationToken.None);

        ProgrammingValidationResult initial = service.Validate(fixture.Root);
        ProgrammingMutationResult set = await service.SetCampaignAsync(
            fixture.Root,
            "song-a",
            weightMultiplier: null,
            CancellationToken.None);
        ProgrammingMutationResult unchanged = await service.SetCampaignAsync(
            fixture.Root,
            "song-a",
            weightMultiplier: null,
            CancellationToken.None);
        ProgrammingMutationResult cleared = await service.ClearCampaignAsync(
            fixture.Root,
            CancellationToken.None);

        Assert.True(initial.IsValid);
        Assert.True(set.Changed);
        Assert.Equal(2, set.Configuration.Revision);
        Assert.Equal(ProgrammingConfiguration.DefaultCampaignMultiplier, set.Configuration.ActiveCampaign!.WeightMultiplier);
        Assert.False(unchanged.Changed);
        Assert.Equal(2, unchanged.Configuration.Revision);
        Assert.True(cleared.Changed);
        Assert.Equal(3, cleared.Configuration.Revision);
        Assert.False(cleared.Configuration.ActiveCampaign!.Enabled);
    }

    [Fact]
    public async Task Service_AssetOverrideCanBeSetClearedAndResetWithoutChangingMetadata()
    {
        using var fixture = new ProgrammingFixture();
        await fixture.PrepareInventoryAsync();
        string metadataPath = AssetMetadataStore.GetMetadataPath(fixture.AssetAPath);
        string originalMetadata = File.ReadAllText(metadataPath);
        var service = new ProgrammingService();
        await service.InitializeAsync(fixture.Root, CancellationToken.None);

        ProgrammingMutationResult blocked = await service.SetAssetOverrideAsync(
            fixture.Root,
            "asset-a",
            doNotAir: true,
            weightMultiplier: 1.5,
            CancellationToken.None);
        ProgrammingMutationResult cleared = await service.SetAssetOverrideAsync(
            fixture.Root,
            "asset-a",
            doNotAir: false,
            weightMultiplier: null,
            CancellationToken.None);
        ProgrammingMutationResult reset = await service.ResetAssetOverrideAsync(
            fixture.Root,
            "asset-a",
            CancellationToken.None);

        Assert.True(blocked.Configuration.AssetOverrides!["asset-a"].DoNotAir);
        Assert.False(cleared.Configuration.AssetOverrides!["asset-a"].DoNotAir);
        Assert.Equal(1.5, cleared.Configuration.AssetOverrides["asset-a"].WeightMultiplier);
        Assert.DoesNotContain("asset-a", reset.Configuration.AssetOverrides!.Keys);
        Assert.Equal(originalMetadata, File.ReadAllText(metadataPath));
    }

    [Fact]
    public async Task Service_RejectsUnknownCampaignAndAssetIdentity()
    {
        using var fixture = new ProgrammingFixture();
        await fixture.PrepareInventoryAsync();
        var service = new ProgrammingService();
        await service.InitializeAsync(fixture.Root, CancellationToken.None);

        await Assert.ThrowsAsync<ProgrammingConfigurationValidationException>(() =>
            service.SetCampaignAsync(fixture.Root, "unknown-song", null, CancellationToken.None));
        await Assert.ThrowsAsync<ProgrammingConfigurationValidationException>(() =>
            service.SetAssetOverrideAsync(
                fixture.Root,
                "unknown-asset",
                true,
                null,
                CancellationToken.None));
    }

    [Fact]
    public async Task Service_ClearAndResetCanRepairStaleReferencesWithoutHandEditingJson()
    {
        using var fixture = new ProgrammingFixture();
        await fixture.PrepareInventoryAsync();
        var store = new ProgrammingConfigurationStore();
        var service = new ProgrammingService();
        ProgrammingConfiguration staleCampaign = ProgrammingConfiguration.CreateDefault() with
        {
            ActiveCampaign = new ActiveCampaign
            {
                Enabled = true,
                ContentGroupId = "retired-song",
                WeightMultiplier = 2,
            },
        };
        await store.WriteAsync(fixture.ConfigurationPath, staleCampaign, CancellationToken.None);

        ProgrammingMutationResult cleared = await service.ClearCampaignAsync(
            fixture.Root,
            CancellationToken.None);

        Assert.False(cleared.Configuration.ActiveCampaign!.Enabled);
        Assert.True(service.Validate(fixture.Root).IsValid);

        ProgrammingConfiguration staleOverride = ProgrammingConfiguration.CreateDefault() with
        {
            AssetOverrides = new Dictionary<string, AssetEditorialOverride>
            {
                ["retired-asset"] = new() { DoNotAir = true },
            },
        };
        await store.WriteAsync(fixture.ConfigurationPath, staleOverride, CancellationToken.None);

        ProgrammingMutationResult reset = await service.ResetAssetOverrideAsync(
            fixture.Root,
            "retired-asset",
            CancellationToken.None);

        Assert.Empty(reset.Configuration.AssetOverrides!);
        Assert.True(service.Validate(fixture.Root).IsValid);
    }

    [Fact]
    public async Task PlaylistBuilder_AbsentConfigurationPreservesLegacyAndPresentConfigurationActivatesPolicy()
    {
        PlaylistAsset assetA = Asset("asset-a", "song-a");
        PlaylistAsset assetB = Asset("asset-b", "song-b");
        var loader = new StubLibraryLoader([assetA, assetB]);
        var legacyBuilder = CreateBuilder(loader, new StaticProgrammingStore(null));
        ProgrammingConfiguration configuration = ProgrammingConfiguration.CreateDefault() with
        {
            AssetOverrides = new Dictionary<string, AssetEditorialOverride>
            {
                [assetA.AssetId] = new() { DoNotAir = true },
            },
        };
        var activeBuilder = CreateBuilder(loader, new StaticProgrammingStore(configuration));
        var request = new PlaylistBuildRequest(
            "library",
            "catalog/song-catalog.json",
            "playlist.json",
            TimeSpan.FromMinutes(3),
            44,
            "history.json",
            DryRun: true);

        PlaylistBuildResult legacy = await legacyBuilder.BuildAsync(request, CancellationToken.None);
        PlaylistBuildResult active = await activeBuilder.BuildAsync(request, CancellationToken.None);

        Assert.False(legacy.ProgrammingPolicyActive);
        Assert.True(active.ProgrammingPolicyActive);
        Assert.Contains(legacy.Generation.Playlist.Items, item => item.AssetId == assetA.AssetId);
        Assert.DoesNotContain(active.Generation.Playlist.Items, item => item.AssetId == assetA.AssetId);
        Assert.Equal(PlaylistDocument.CurrentSchemaVersion, active.Generation.Playlist.SchemaVersion);
        Assert.Equal(PlaylistHistoryDocument.CurrentSchemaVersion, active.Generation.UpdatedHistory.SchemaVersion);
    }

    [Fact]
    public async Task ProgrammingJsonNeverSerializesDestinationOrRuntimeState()
    {
        using var fixture = new ProgrammingFixture();
        const string secret = "rtmps://example.invalid/live2/FAKE-SECRET";
        var store = new ProgrammingConfigurationStore();
        await store.WriteAsync(
            fixture.ConfigurationPath,
            ProgrammingConfiguration.CreateDefault(),
            CancellationToken.None);

        string json = File.ReadAllText(fixture.ConfigurationPath);

        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("rtmp", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stationPid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resumeGlobalIndex", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, StationRuntimeState.CurrentSchemaVersion);
    }

    [Fact]
    public async Task BroadcasterAcceptsPlaylistGeneratedUnderProgrammingPolicy()
    {
        using var fixture = new ProgrammingFixture();
        string library = Path.Combine(fixture.Root, "library");
        PlaylistAsset[] assets =
        [
            Asset("asset-a", "song-a"),
            Asset("asset-b", "song-b"),
        ];
        var policy = new PlaylistPolicy
        {
            TargetDuration = TimeSpan.FromMinutes(2),
            CategoryAirtimeTargets = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [AssetTypes.MusicVideo] = 1,
            },
            InterstitialCadence = null,
        };
        PlaylistDocument playlist = new PlaylistGenerator().Generate(
            assets,
            [],
            PlaylistHistoryDocument.Empty,
            policy,
            92,
            Now,
            ProgrammingConfiguration.CreateDefault()).Playlist;
        foreach (PlaylistAsset asset in assets)
        {
            string mediaPath = Path.Combine(
                library,
                asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
            File.WriteAllText(mediaPath, "normalized");
            File.WriteAllText(SourceManifestStore.GetManifestPath(mediaPath), "manifest");
        }

        string playlistPath = Path.Combine(fixture.Root, "playlists", "generated.json");
        await new PlaylistStore().WriteAsync(playlistPath, playlist, CancellationToken.None);

        BroadcastPlan plan = new BroadcastPlanner().CreatePlan([playlistPath], library);

        Assert.True(plan.IsReady);
        Assert.Equal(playlist.Items.Count, plan.Items.Count);
        Assert.All(plan.Items, item => Assert.True(File.Exists(item.MediaPath)));
    }

    private static PlaylistBuilder CreateBuilder(
        IPlaylistLibraryLoader loader,
        IProgrammingConfigurationStore programmingStore) => new(
        new StubCatalogStore(),
        loader,
        new RecordingPlaylistStore(),
        new RecordingHistoryStore(),
        new PlaylistGenerator(),
        timeProvider: new FixedTimeProvider(Now),
        programmingStore: programmingStore);

    private static PlaylistAsset Asset(string assetId, string group) => new(
        assetId,
        group,
        assetId,
        "Nzyte",
        AssetTypes.MusicVideo,
        null,
        $"Music Videos/{assetId}.mp4",
        60,
        null);

    private sealed class ProgrammingFixture : IDisposable
    {
        public ProgrammingFixture()
        {
            Root = Directory.CreateTempSubdirectory("nzytetv-programming-").FullName;
            Directory.CreateDirectory(Path.Combine(Root, "catalog"));
            Directory.CreateDirectory(Path.Combine(Root, "library"));
        }

        public string Root { get; }

        public string ConfigurationPath => Path.Combine(Root, "catalog", "programming.json");

        public string AssetAPath { get; private set; } = string.Empty;

        public async Task PrepareInventoryAsync()
        {
            var catalog = new SongCatalog
            {
                SchemaVersion = SongCatalog.CurrentSchemaVersion,
                Songs =
                [
                    new SongCatalogEntry { ContentGroupId = "song-a", Title = "Song A", Artist = "Nzyte" },
                    new SongCatalogEntry { ContentGroupId = "song-b", Title = "Song B", Artist = "Nzyte" },
                ],
            };
            await new SongCatalogStore().WriteNewAsync(
                Path.Combine(Root, "catalog", "song-catalog.json"),
                catalog,
                CancellationToken.None);
            AssetAPath = await AddAssetAsync("asset-a", "song-a");
            _ = await AddAssetAsync("asset-b", "song-b");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private async Task<string> AddAssetAsync(string assetId, string group)
        {
            string path = Path.Combine(Root, "library", "Music Videos", $"{assetId}.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "normalized");
            File.WriteAllText(SourceManifestStore.GetManifestPath(path), "manifest");
            await new AssetMetadataStore().WriteAsync(
                path,
                new AssetMetadata
                {
                    AssetId = assetId,
                    ContentGroupId = group,
                    Title = assetId,
                    Artist = "Nzyte",
                    Type = AssetTypes.MusicVideo,
                    Enabled = true,
                    Tags = [],
                },
                CancellationToken.None);
            return path;
        }
    }

    private sealed class ThrowingWriter : IAtomicTextFileWriter
    {
        public Task WriteAsync(string path, string content, CancellationToken cancellationToken) =>
            throw new IOException("simulated interrupted write");
    }

    private sealed class StaticProgrammingStore(ProgrammingConfiguration? configuration)
        : IProgrammingConfigurationStore
    {
        public ProgrammingConfiguration Load(string path) =>
            configuration ?? throw new FileNotFoundException();

        public ProgrammingConfiguration? LoadIfExists(string path) => configuration;

        public Task<ProgrammingConfigurationInitializationResult> InitializeAsync(
            string path,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task WriteAsync(
            string path,
            ProgrammingConfiguration value,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubCatalogStore : ISongCatalogStore
    {
        public SongCatalog Load(string catalogPath) => new()
        {
            SchemaVersion = SongCatalog.CurrentSchemaVersion,
            Songs =
            [
                new SongCatalogEntry { ContentGroupId = "song-a", Title = "Song A", Artist = "Nzyte" },
                new SongCatalogEntry { ContentGroupId = "song-b", Title = "Song B", Artist = "Nzyte" },
            ],
        };
    }

    private sealed class StubLibraryLoader(IReadOnlyList<PlaylistAsset> assets) : IPlaylistLibraryLoader
    {
        public Task<PlaylistLibrarySnapshot> LoadAsync(
            string libraryRoot,
            SongCatalog catalog,
            CancellationToken cancellationToken) => Task.FromResult(
                new PlaylistLibrarySnapshot(assets, [])
                {
                    KnownAssetIds = assets.Select(asset => asset.AssetId).ToHashSet(StringComparer.Ordinal),
                });
    }

    private sealed class RecordingPlaylistStore : IPlaylistStore
    {
        public Task WriteAsync(string path, PlaylistDocument playlist, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RecordingHistoryStore : IPlaylistHistoryStore
    {
        public PlaylistHistoryDocument Load(string path) => PlaylistHistoryDocument.Empty;

        public Task WriteAsync(
            string path,
            PlaylistHistoryDocument history,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
