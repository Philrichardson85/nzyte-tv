using System.Text.Json;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class PlaylistWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LibrarySnapshot_UsesV02AEligibilityAndReportsExclusionReasons()
    {
        using var fixture = new PlaylistFixture();
        string ready = await fixture.AddAssetAsync("Music Videos/Ready.mp4", "ready", "ready-group");
        string disabled = await fixture.AddAssetAsync(
            "Music Videos/Disabled.mp4", "disabled", "disabled-group", enabled: false);
        string unresolved = await fixture.AddAssetAsync(
            "Music Videos/Unresolved.mp4", "unresolved", contentGroupId: null);
        string noManifest = await fixture.AddAssetAsync(
            "Music Videos/No Manifest.mp4", "no-manifest", "ready-group", createManifest: false);
        string noMetadata = fixture.AddMedia("Music Videos/No Metadata.mp4");
        fixture.AddManifest(noMetadata);
        string noDuration = await fixture.AddAssetAsync(
            "Music Videos/No Duration.mp4", "no-duration", "ready-group", durationSeconds: null);
        string missingMedia = fixture.GetPath("Music Videos/Missing.mp4");
        await fixture.WriteMetadataAsync(missingMedia, "missing-media", "ready-group");
        fixture.AddManifest(missingMedia);
        string readyBefore = File.ReadAllText(ready);
        string readyMetadataBefore = File.ReadAllText(AssetMetadataStore.GetMetadataPath(ready));

        PlaylistLibrarySnapshot snapshot = await fixture.CreateLoader().LoadAsync(
            fixture.Root,
            fixture.Catalog,
            CancellationToken.None);

        PlaylistAsset eligible = Assert.Single(snapshot.EligibleAssets);
        Assert.Equal("ready", eligible.AssetId);
        Assert.Equal(120, eligible.DurationSeconds);
        Assert.Equal(6, snapshot.ExcludedAssets.Count);
        AssertReason(snapshot, disabled, "disabled");
        AssertReason(snapshot, unresolved, "unresolved");
        AssertReason(snapshot, noManifest, "manifest");
        AssertReason(snapshot, noMetadata, "metadata");
        AssertReason(snapshot, noDuration, "duration");
        AssertReason(snapshot, missingMedia, "file is missing");
        Assert.Equal(readyBefore, File.ReadAllText(ready));
        Assert.Equal(readyMetadataBefore, File.ReadAllText(AssetMetadataStore.GetMetadataPath(ready)));
    }

    [Fact]
    public async Task LibrarySnapshot_InvalidMetadataIsExcludedWithoutAbortingLaterAssets()
    {
        using var fixture = new PlaylistFixture();
        string invalid = fixture.AddMedia("Music Videos/Invalid.mp4");
        fixture.AddManifest(invalid);
        File.WriteAllText(AssetMetadataStore.GetMetadataPath(invalid), "{ not json");
        await fixture.AddAssetAsync("Music Videos/Ready.mp4", "ready", "ready-group");

        PlaylistLibrarySnapshot snapshot = await fixture.CreateLoader().LoadAsync(
            fixture.Root,
            fixture.Catalog,
            CancellationToken.None);

        Assert.Single(snapshot.EligibleAssets);
        AssertReason(snapshot, invalid, "invalid");
    }

    [Fact]
    public void HistoryStore_MalformedOrUnsupportedHistoryFailsClearly()
    {
        using var fixture = new PlaylistFixture();
        string malformed = Path.Combine(fixture.Root, "malformed-history.json");
        File.WriteAllText(malformed, "{ bad json");
        string unsupported = Path.Combine(fixture.Root, "unsupported-history.json");
        File.WriteAllText(unsupported, """
            { "schemaVersion": 99, "plays": [] }
            """);
        var store = new PlaylistHistoryStore();

        InvalidDataException malformedError = Assert.Throws<InvalidDataException>(() => store.Load(malformed));
        InvalidDataException schemaError = Assert.Throws<InvalidDataException>(() => store.Load(unsupported));

        Assert.Contains("Malformed playlist history", malformedError.Message, StringComparison.Ordinal);
        Assert.Contains("schemaVersion 99", schemaError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaylistAndHistoryStoresWriteVersionedInspectableJson()
    {
        using var fixture = new PlaylistFixture();
        string playlistPath = Path.Combine(fixture.Root, "out", "current.json");
        string historyPath = Path.Combine(fixture.Root, "out", "history.json");
        PlaylistGenerationResult generation = new PlaylistGenerator().Generate(
            [Asset("ready")],
            [],
            PlaylistHistoryDocument.Empty,
            new PlaylistPolicy
            {
                TargetDuration = TimeSpan.FromMinutes(1),
                BumperCadence = null,
                PromoCadence = null,
                InterstitialCadence = null,
            },
            123,
            Now);

        await new PlaylistStore().WriteAsync(playlistPath, generation.Playlist, CancellationToken.None);
        await new PlaylistHistoryStore().WriteAsync(
            historyPath, generation.UpdatedHistory, CancellationToken.None);

        using JsonDocument playlist = JsonDocument.Parse(File.ReadAllText(playlistPath));
        using JsonDocument history = JsonDocument.Parse(File.ReadAllText(historyPath));
        Assert.Equal(1, playlist.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(123, playlist.RootElement.GetProperty("seed").GetInt32());
        Assert.Equal("ready", playlist.RootElement.GetProperty("items")[0].GetProperty("assetId").GetString());
        JsonElement summary = playlist.RootElement.GetProperty("summary");
        Assert.Equal(0, summary.GetProperty("emergencyContentGroupFloorViolations").GetInt32());
        Assert.Equal(0, summary.GetProperty("emergencyVlogRunViolations").GetInt32());
        Assert.Equal(0, summary.GetProperty("bumperInsertions").GetInt32());
        Assert.Equal(0, summary.GetProperty("promoInsertions").GetInt32());
        Assert.Equal(0, summary.GetProperty("interstitialInsertions").GetInt32());
        Assert.Equal(1, history.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.NotEmpty(history.RootElement.GetProperty("plays").EnumerateArray());
    }

    [Fact]
    public async Task Builder_DryRunWritesNoPlaylistOrHistory()
    {
        var playlistStore = new RecordingPlaylistStore();
        var historyStore = new RecordingHistoryStore();
        var builder = new PlaylistBuilder(
            new StubCatalogStore(),
            new StubLibraryLoader([Asset("ready")]),
            playlistStore,
            historyStore,
            new PlaylistGenerator(),
            timeProvider: new FixedTimeProvider(Now));
        var request = new PlaylistBuildRequest(
            "library",
            "catalog.json",
            "playlist.json",
            TimeSpan.FromMinutes(1),
            321,
            "history.json",
            DryRun: true);

        PlaylistBuildResult result = await builder.BuildAsync(request, CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.NotEmpty(result.Generation.Playlist.Items);
        Assert.Equal(0, playlistStore.Writes);
        Assert.Equal(0, historyStore.Writes);
    }

    [Fact]
    public async Task Builder_NonDryRunWritesPlaylistAndBoundedHistory()
    {
        var playlistStore = new RecordingPlaylistStore();
        var historyStore = new RecordingHistoryStore
        {
            Loaded = new PlaylistHistoryDocument
            {
                ScheduleEndUtc = Now,
                Plays =
                [
                    new PlaylistHistoryEntry("ancient", "ancient", AssetTypes.MusicVideo, Now.AddHours(-10)),
                    new PlaylistHistoryEntry("recent", "recent", AssetTypes.MusicVideo, Now.AddMinutes(-10)),
                ],
            },
        };
        var builder = new PlaylistBuilder(
            new StubCatalogStore(),
            new StubLibraryLoader([Asset("ready")]),
            playlistStore,
            historyStore,
            new PlaylistGenerator(),
            timeProvider: new FixedTimeProvider(Now));

        PlaylistBuildResult result = await builder.BuildAsync(
            new PlaylistBuildRequest(
                "library", "catalog.json", "playlist.json", TimeSpan.FromMinutes(1), 5, "history.json", false),
            CancellationToken.None);

        Assert.Equal(1, playlistStore.Writes);
        Assert.Equal(1, historyStore.Writes);
        Assert.DoesNotContain(result.Generation.UpdatedHistory.Plays, play => play.AssetId == "ancient");
        Assert.Contains(result.Generation.UpdatedHistory.Plays, play => play.AssetId == "recent");
    }

    private static PlaylistAsset Asset(string id) => new(
        id,
        $"{id}-group",
        id,
        "Nzyte",
        AssetTypes.MusicVideo,
        null,
        $"Music Videos/{id}.mp4",
        60,
        null);

    private static void AssertReason(PlaylistLibrarySnapshot snapshot, string path, string text)
    {
        PlaylistExclusion exclusion = snapshot.ExcludedAssets.Single(candidate =>
            candidate.RelativePath.EndsWith(Path.GetFileName(path), StringComparison.Ordinal));
        Assert.Contains(exclusion.Reasons, reason => reason.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class PlaylistFixture : IDisposable
    {
        private readonly Dictionary<string, TimeSpan?> _durations = new(GetPathComparer());

        public PlaylistFixture()
        {
            Root = Directory.CreateTempSubdirectory("nzytetv-playlist-").FullName;
        }

        public string Root { get; }

        public SongCatalog Catalog { get; } = new()
        {
            SchemaVersion = SongCatalog.CurrentSchemaVersion,
            Songs =
            [
                new SongCatalogEntry
                {
                    ContentGroupId = "ready-group",
                    Title = "Ready",
                    Artist = "Nzyte",
                    Aliases = [],
                },
            ],
        };

        public string GetPath(string relativePath) => Path.Combine(
            Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        public string AddMedia(string relativePath, TimeSpan? duration = null)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "normalized media");
            _durations[Path.GetFullPath(path)] = duration ?? TimeSpan.FromMinutes(2);
            return path;
        }

        public void AddManifest(string mediaPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
            File.WriteAllText(SourceManifestStore.GetManifestPath(mediaPath), "technical state");
        }

        public async Task<string> AddAssetAsync(
            string relativePath,
            string assetId,
            string? contentGroupId,
            bool enabled = true,
            bool createManifest = true,
            double? durationSeconds = 120)
        {
            string path = AddMedia(
                relativePath,
                durationSeconds is double seconds ? TimeSpan.FromSeconds(seconds) : null);
            await WriteMetadataAsync(path, assetId, contentGroupId, enabled);
            if (createManifest)
            {
                AddManifest(path);
            }

            if (durationSeconds is null)
            {
                _durations[path] = null;
            }

            return path;
        }

        public Task WriteMetadataAsync(
            string mediaPath,
            string assetId,
            string? contentGroupId,
            bool enabled = true) =>
            new AssetMetadataStore().WriteAsync(
                mediaPath,
                new AssetMetadata
                {
                    AssetId = assetId,
                    ContentGroupId = contentGroupId,
                    Title = assetId,
                    Artist = "Nzyte",
                    Type = AssetTypes.MusicVideo,
                    Enabled = enabled,
                    Tags = [],
                },
                CancellationToken.None);

        public PlaylistLibraryLoader CreateLoader() => new(new StubAnalyzer(_durations));

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private sealed class StubAnalyzer(IReadOnlyDictionary<string, TimeSpan?> durations) : IMediaAnalyzer
    {
        public Task<MediaDescription> InspectAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaDescription(
                filePath,
                durations.GetValueOrDefault(Path.GetFullPath(filePath)),
                "mov,mp4",
                1,
                null,
                null,
                []));

        public Task<MediaDescription> AnalyzeForVerificationAsync(
            string filePath,
            CancellationToken cancellationToken) => InspectAsync(filePath, cancellationToken);
    }

    private sealed class StubCatalogStore : ISongCatalogStore
    {
        public SongCatalog Load(string catalogPath) => new()
        {
            SchemaVersion = SongCatalog.CurrentSchemaVersion,
            Songs =
            [
                new SongCatalogEntry
                {
                    ContentGroupId = "ready-group",
                    Title = "Ready",
                    Artist = "Nzyte",
                    Aliases = [],
                },
            ],
        };
    }

    private sealed class StubLibraryLoader(IReadOnlyList<PlaylistAsset> assets) : IPlaylistLibraryLoader
    {
        public Task<PlaylistLibrarySnapshot> LoadAsync(
            string libraryRoot,
            SongCatalog catalog,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PlaylistLibrarySnapshot(assets, []));
    }

    private sealed class RecordingPlaylistStore : IPlaylistStore
    {
        public int Writes { get; private set; }

        public Task WriteAsync(string path, PlaylistDocument playlist, CancellationToken cancellationToken)
        {
            Writes++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHistoryStore : IPlaylistHistoryStore
    {
        public int Writes { get; private set; }

        public PlaylistHistoryDocument Loaded { get; init; } = PlaylistHistoryDocument.Empty;

        public PlaylistHistoryDocument Load(string path) => Loaded;

        public Task WriteAsync(
            string path,
            PlaylistHistoryDocument history,
            CancellationToken cancellationToken)
        {
            Writes++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
