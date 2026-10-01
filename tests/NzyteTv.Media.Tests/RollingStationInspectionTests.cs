using System.Text.Json;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class RollingStationInspectionTests
{
    private const string Secret = "rtmps://example.invalid/live2/FAKE-SECRET";

    [Fact]
    public async Task ValidateAndStatus_AreReadOnlyAndDoNotRequireCurrentProgrammingConfiguration()
    {
        using InspectionFixture fixture = await InspectionFixture.CreateAsync();
        Dictionary<string, (byte[] Content, DateTime Mtime)> before = fixture.CaptureFiles();
        File.WriteAllText(fixture.LibraryFixture.ProgrammingPath, "{ invalid future programming");
        before = fixture.CaptureFiles();
        var service = fixture.CreateService();

        RollingStationValidationResult validation = service.Validate(
            fixture.RollingConfigurationPath,
            configuredDestination: null);
        RollingStationStatusSnapshot status = service.GetStatus(
            fixture.RollingConfigurationPath,
            configuredDestination: Secret);

        Assert.True(validation.IsReady, string.Join("; ", validation.Errors));
        Assert.Equal(BroadcastDestinationStatus.NotConfigured, validation.DestinationStatus);
        Assert.Contains(validation.Warnings, warning =>
            warning.Contains("UNINITIALIZED", StringComparison.Ordinal));
        Assert.Equal(BroadcastDestinationStatus.Valid, status.Validation.DestinationStatus);
        Assert.Equal(1, status.NextRequiredSequence);
        Assert.True(status.NextBlockCommitted);
        Assert.Equal(before.Keys.Order(), fixture.CaptureFiles().Keys.Order());
        foreach ((string path, (byte[] content, DateTime mtime)) in before)
        {
            Assert.Equal(content, File.ReadAllBytes(path));
            Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));
        }
    }

    [Theory]
    [InlineData(null, BroadcastDestinationStatus.NotConfigured, true)]
    [InlineData("", BroadcastDestinationStatus.NotConfigured, true)]
    [InlineData("rtmp://example.invalid/live/FAKE", BroadcastDestinationStatus.Valid, true)]
    [InlineData("rtmps://example.invalid/live2/FAKE", BroadcastDestinationStatus.Valid, true)]
    [InlineData("https://example.invalid/live/FAKE", BroadcastDestinationStatus.Invalid, false)]
    [InlineData("not-a-url", BroadcastDestinationStatus.Invalid, false)]
    public async Task Validate_ClassifiesDestinationWithoutDisplayingOrSerializingIt(
        string? destination,
        BroadcastDestinationStatus expected,
        bool expectedReady)
    {
        using InspectionFixture fixture = await InspectionFixture.CreateAsync();

        RollingStationValidationResult result = fixture.CreateService().Validate(
            fixture.RollingConfigurationPath,
            destination);

        Assert.Equal(expected, result.DestinationStatus);
        Assert.Equal(expectedReady, result.IsReady);
        if (!string.IsNullOrEmpty(destination))
        {
            Assert.DoesNotContain(
                result.Errors,
                error => error.Contains(destination, StringComparison.Ordinal));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(fixture.LibraryFixture.Root, "*.json", SearchOption.AllDirectories)
                    .Select(File.ReadAllText),
                text => text.Contains(destination, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Validate_DetectsPlannerLineageAndCp2Contradictions()
    {
        using InspectionFixture fixture = await InspectionFixture.CreateAsync();
        RollingStationConfiguration wrongLineage = fixture.Configuration with
        {
            PlannerId = "ffeeddccbbaa99887766554433221100",
        };
        fixture.WriteRollingConfiguration(wrongLineage);

        RollingStationValidationResult lineage = fixture.CreateService().Validate(
            fixture.RollingConfigurationPath,
            null);

        Assert.False(lineage.IsReady);
        Assert.Contains(lineage.Errors, error =>
            error.Contains("lineage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_UsesExistingBlockResolverAndBroadcastPlannerReadiness()
    {
        using InspectionFixture fixture = await InspectionFixture.CreateAsync();
        RollingCommittedBlock block = fixture.Manifest.Blocks![0];
        string playlist = RollingPathSafety.ResolveExistingFile(
            fixture.Paths.RollingRoot,
            block.PlaylistPath!);
        File.AppendAllText(playlist, Environment.NewLine);

        RollingStationValidationResult result = fixture.CreateService().Validate(
            fixture.RollingConfigurationPath,
            null);

        Assert.False(result.IsReady);
        Assert.Contains(result.Errors, error =>
            error.Contains("hash mismatch", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class InspectionFixture : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        private InspectionFixture(
            RollingLibraryFixture libraryFixture,
            RollingMaintainResult maintained)
        {
            LibraryFixture = libraryFixture;
            Manifest = maintained.Manifest;
            Paths = maintained.Paths;
            StatePath = Path.Combine(libraryFixture.Root, "runtime", "state.json");
            RollingStatePath = Path.Combine(libraryFixture.Root, "runtime", "rolling-state.json");
            StationConfigurationPath = Path.Combine(libraryFixture.Root, "station.json");
            RollingConfigurationPath = Path.Combine(libraryFixture.Root, "rolling-station.json");
            string playlist = RollingPathSafety.ResolveExistingFile(
                Paths.RollingRoot,
                Manifest.Blocks![0].PlaylistPath!);
            var station = new StationConfiguration
            {
                MediaRoot = libraryFixture.Root,
                LibraryRoot = libraryFixture.LibraryRoot,
                StatePath = StatePath,
                Playlists = [playlist],
            };
            Configuration = new RollingStationConfiguration
            {
                StationConfigPath = StationConfigurationPath,
                PlannerId = Manifest.PlannerId!,
                RollingStatePath = RollingStatePath,
            };
            File.WriteAllText(StationConfigurationPath, Serialize(station));
            WriteRollingConfiguration(Configuration);
        }

        public RollingLibraryFixture LibraryFixture { get; }

        public RollingProgrammingManifest Manifest { get; }

        public RollingProgrammingPaths Paths { get; }

        public string StatePath { get; }

        public string RollingStatePath { get; }

        public string StationConfigurationPath { get; }

        public string RollingConfigurationPath { get; }

        public RollingStationConfiguration Configuration { get; }

        public static async Task<InspectionFixture> CreateAsync()
        {
            RollingLibraryFixture library = await RollingLibraryFixture.CreateAsync(programming: true);
            try
            {
                RollingProgrammingPlanner planner = library.CreatePlanner();
                await library.InitializeAsync(planner);
                RollingMaintainResult maintained = await planner.MaintainAsync(
                    library.Root,
                    CancellationToken.None,
                    committedBlockTarget: 1);
                return new InspectionFixture(library, maintained);
            }
            catch
            {
                library.Dispose();
                throw;
            }
        }

        public RollingStationInspectionService CreateService() => new(
            locateFfmpeg: () => "ffmpeg",
            processExistence: new NoProcesses());

        public void WriteRollingConfiguration(RollingStationConfiguration value) =>
            File.WriteAllText(RollingConfigurationPath, Serialize(value));

        public Dictionary<string, (byte[] Content, DateTime Mtime)> CaptureFiles() => Directory
            .EnumerateFiles(LibraryFixture.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => path,
                path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        public void Dispose() => LibraryFixture.Dispose();

        private static string Serialize<T>(T value) =>
            JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine;
    }

    private sealed class NoProcesses : IProcessExistence
    {
        public bool Exists(int processId) => false;
    }
}
