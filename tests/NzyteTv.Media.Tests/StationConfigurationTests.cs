using System.Text.Json;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class StationConfigurationTests
{
    [Fact]
    public void Load_ValidConfigurationParsesAndNormalizesPaths()
    {
        using var fixture = new StationConfigurationFixture();
        string configPath = fixture.WriteValidConfiguration();

        StationConfiguration configuration = new StationConfigurationLoader().Load(configPath);

        Assert.Equal(StationConfiguration.CurrentSchemaVersion, configuration.SchemaVersion);
        Assert.Equal(Path.GetFullPath(fixture.MediaRoot), configuration.MediaRoot);
        Assert.Equal(Path.GetFullPath(fixture.LibraryRoot), configuration.LibraryRoot);
        Assert.Equal(Path.GetFullPath(fixture.StatePath), configuration.StatePath);
        Assert.Equal([Path.GetFullPath(fixture.PlaylistPath)], configuration.Playlists);
    }

    [Fact]
    public void Load_MissingConfigurationIsRejected()
    {
        using var fixture = new StationConfigurationFixture();

        Assert.Throws<FileNotFoundException>(() => new StationConfigurationLoader().Load(
            Path.Combine(fixture.Root, "missing.json")));
    }

    [Fact]
    public void Load_MalformedConfigurationIsRejected()
    {
        using var fixture = new StationConfigurationFixture();
        string path = fixture.WriteRawConfiguration("{ invalid");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new StationConfigurationLoader().Load(path));

        Assert.Contains("Malformed station configuration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_UnsupportedSchemaIsRejected()
    {
        using var fixture = new StationConfigurationFixture();
        string path = fixture.WriteConfiguration(schemaVersion: 99);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new StationConfigurationLoader().Load(path));

        Assert.Contains("schemaVersion 99", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingSchemaIsRejected()
    {
        using var fixture = new StationConfigurationFixture();
        string path = fixture.WriteRawConfiguration(JsonSerializer.Serialize(new
        {
            mediaRoot = fixture.MediaRoot,
            libraryRoot = fixture.LibraryRoot,
            statePath = fixture.StatePath,
            playlists = new[] { fixture.PlaylistPath },
        }));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new StationConfigurationLoader().Load(path));

        Assert.Contains("Malformed station configuration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingMediaRootIsRejected()
    {
        using var fixture = new StationConfigurationFixture();
        string missing = Path.Combine(fixture.Root, "missing-media");
        string path = fixture.WriteConfiguration(mediaRoot: missing);

        DirectoryNotFoundException exception = Assert.Throws<DirectoryNotFoundException>(() =>
            new StationConfigurationLoader().Load(path));

        Assert.Contains("media root", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_MissingLibraryRootIsRejected()
    {
        using var fixture = new StationConfigurationFixture();
        string missing = Path.Combine(fixture.Root, "missing-library");
        string path = fixture.WriteConfiguration(libraryRoot: missing);

        DirectoryNotFoundException exception = Assert.Throws<DirectoryNotFoundException>(() =>
            new StationConfigurationLoader().Load(path));

        Assert.Contains("library root", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_EmptyPlaylistsAreRejected()
    {
        using var fixture = new StationConfigurationFixture();
        string path = fixture.WriteConfiguration(playlists: []);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new StationConfigurationLoader().Load(path));

        Assert.Contains("at least one playlist", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_MissingPlaylistIsRejected()
    {
        using var fixture = new StationConfigurationFixture();
        string missing = Path.Combine(fixture.Root, "missing-playlist.json");
        string path = fixture.WriteConfiguration(playlists: [missing]);

        Assert.Throws<FileNotFoundException>(() => new StationConfigurationLoader().Load(path));
    }

    [Fact]
    public void Load_InvalidStatePathIsRejected()
    {
        using var fixture = new StationConfigurationFixture();
        string path = fixture.WriteConfiguration(statePath: fixture.MediaRoot);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new StationConfigurationLoader().Load(path));

        Assert.Contains("statePath", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_DuplicatePlaylistIsRejected()
    {
        using var fixture = new StationConfigurationFixture();
        string path = fixture.WriteConfiguration(playlists: [fixture.PlaylistPath, fixture.PlaylistPath]);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new StationConfigurationLoader().Load(path));

        Assert.Contains("duplicate playlist", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("mediaRoot")]
    [InlineData("libraryRoot")]
    [InlineData("statePath")]
    [InlineData("playlist")]
    public void Load_RelativeProductionPathsAreRejected(string property)
    {
        using var fixture = new StationConfigurationFixture();
        string path = fixture.WriteConfiguration(
            mediaRoot: property == "mediaRoot" ? "media" : fixture.MediaRoot,
            libraryRoot: property == "libraryRoot" ? "library" : fixture.LibraryRoot,
            statePath: property == "statePath" ? "state.json" : fixture.StatePath,
            playlists: property == "playlist" ? ["playlist.json"] : null);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new StationConfigurationLoader().Load(path));

        Assert.Contains("absolute path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_SecretOrOtherUnknownConfigurationPropertiesAreRejectedAndModelDoesNotSerializeThem()
    {
        using var fixture = new StationConfigurationFixture();
        string secret = "rtmps://example.invalid/live2/SECRET-KEY";
        string path = fixture.WriteRawConfiguration(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            mediaRoot = fixture.MediaRoot,
            libraryRoot = fixture.LibraryRoot,
            statePath = fixture.StatePath,
            playlists = new[] { fixture.PlaylistPath },
            rtmpUrl = secret,
        }));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new StationConfigurationLoader().Load(path));
        string serializedModel = JsonSerializer.Serialize(new StationConfiguration
        {
            MediaRoot = fixture.MediaRoot,
            LibraryRoot = fixture.LibraryRoot,
            StatePath = fixture.StatePath,
            Playlists = [fixture.PlaylistPath],
        });

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("rtmp", serializedModel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", serializedModel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_DoesNotStartFfmpegAndReusesBroadcastReadinessChecks()
    {
        using var fixture = new StationConfigurationFixture();
        int locatorCalls = 0;
        var service = new StationValidationService(
            new StationConfigurationLoader(),
            new BroadcastPlanner(),
            () =>
            {
                locatorCalls++;
                return "ffmpeg";
            });

        StationValidationResult ready = service.Validate(
            fixture.WriteValidConfiguration(),
            configuredDestination: null);
        File.Delete(SourceManifestStore.GetManifestPath(fixture.MediaPath));
        StationValidationResult unready = service.Validate(
            fixture.WriteValidConfiguration(),
            configuredDestination: "rtmps://example.invalid/live2/EXAMPLE-KEY");

        Assert.True(ready.IsReady);
        Assert.Equal(BroadcastDestinationStatus.NotConfigured, ready.DestinationStatus);
        Assert.False(unready.IsReady);
        Assert.Equal(BroadcastDestinationStatus.Valid, unready.DestinationStatus);
        Assert.Equal(1, unready.BroadcastPlan.UnreadyAssetCount);
        Assert.Equal(2, locatorCalls);
    }

    [Theory]
    [InlineData(null, BroadcastDestinationStatus.NotConfigured, true)]
    [InlineData("", BroadcastDestinationStatus.NotConfigured, true)]
    [InlineData("   ", BroadcastDestinationStatus.NotConfigured, true)]
    [InlineData("rtmp://example.invalid/live/EXAMPLE-KEY", BroadcastDestinationStatus.Valid, true)]
    [InlineData("rtmps://example.invalid/live2/EXAMPLE-KEY", BroadcastDestinationStatus.Valid, true)]
    [InlineData("not-a-url", BroadcastDestinationStatus.Invalid, false)]
    [InlineData("https://example.invalid/live/EXAMPLE-KEY", BroadcastDestinationStatus.Invalid, false)]
    public void Validate_ClassifiesDestinationUsingLiveRunSemantics(
        string? configuredDestination,
        BroadcastDestinationStatus expectedStatus,
        bool expectedReady)
    {
        using var fixture = new StationConfigurationFixture();
        var service = new StationValidationService(
            new StationConfigurationLoader(),
            new BroadcastPlanner(),
            () => "ffmpeg");

        StationValidationResult result = service.Validate(
            fixture.WriteValidConfiguration(),
            configuredDestination);

        Assert.Equal(expectedStatus, result.DestinationStatus);
        Assert.Equal(expectedReady, result.IsReady);
    }

    private sealed class StationConfigurationFixture : IDisposable
    {
        public StationConfigurationFixture()
        {
            Root = Directory.CreateTempSubdirectory("nzytetv-station-config-").FullName;
            MediaRoot = Directory.CreateDirectory(Path.Combine(Root, "media")).FullName;
            LibraryRoot = Directory.CreateDirectory(Path.Combine(MediaRoot, "library")).FullName;
            StatePath = Path.Combine(Root, "state", "state.json");
            MediaPath = Path.Combine(LibraryRoot, "ready.mp4");
            File.WriteAllText(MediaPath, "media");
            File.WriteAllText(SourceManifestStore.GetManifestPath(MediaPath), "{}");
            PlaylistPath = Path.Combine(MediaRoot, "playlist.json");
            File.WriteAllText(PlaylistPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                items = new[]
                {
                    new
                    {
                        sequence = 1,
                        assetId = "asset-1",
                        relativePath = "ready.mp4",
                        durationSeconds = 60,
                        title = "Ready",
                        type = "music-video",
                    },
                },
            }));
        }

        public string Root { get; }

        public string MediaRoot { get; }

        public string LibraryRoot { get; }

        public string StatePath { get; }

        public string PlaylistPath { get; }

        public string MediaPath { get; }

        public string WriteValidConfiguration() => WriteConfiguration();

        public string WriteConfiguration(
            int schemaVersion = 1,
            string? mediaRoot = null,
            string? libraryRoot = null,
            string? statePath = null,
            IReadOnlyList<string>? playlists = null) => WriteRawConfiguration(JsonSerializer.Serialize(new
            {
                schemaVersion,
                mediaRoot = mediaRoot ?? MediaRoot,
                libraryRoot = libraryRoot ?? LibraryRoot,
                statePath = statePath ?? StatePath,
                playlists = playlists ?? [PlaylistPath],
            }));

        public string WriteRawConfiguration(string json)
        {
            string path = Path.Combine(Root, "station.json");
            File.WriteAllText(path, json);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
