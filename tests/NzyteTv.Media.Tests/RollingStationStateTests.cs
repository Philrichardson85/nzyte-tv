using System.Text.Json;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class RollingStationStateTests
{
    private const string PlannerId = "00112233445566778899aabbccddeeff";

    [Fact]
    public void Configuration_LoadsVersionedAbsoluteReferences()
    {
        using var fixture = new ConfigurationFixture();

        RollingStationConfiguration configuration =
            new RollingStationConfigurationLoader().Load(fixture.WriteConfiguration());

        Assert.Equal(RollingStationConfiguration.CurrentSchemaVersion, configuration.SchemaVersion);
        Assert.Equal(Path.GetFullPath(fixture.StationConfigPath), configuration.StationConfigPath);
        Assert.Equal(PlannerId, configuration.PlannerId);
        Assert.Equal(Path.GetFullPath(fixture.RollingStatePath), configuration.RollingStatePath);
    }

    [Fact]
    public void Configuration_RejectsMalformedUnsupportedRelativeAndUnknownSecretData()
    {
        using var fixture = new ConfigurationFixture();
        var loader = new RollingStationConfigurationLoader();

        Assert.Throws<InvalidDataException>(() => loader.Load(fixture.WriteRaw("{ invalid")));
        Assert.Throws<InvalidDataException>(() => loader.Load(fixture.WriteRaw(JsonSerializer.Serialize(new
        {
            schemaVersion = 99,
            stationConfigPath = fixture.StationConfigPath,
            plannerId = PlannerId,
            rollingStatePath = fixture.RollingStatePath,
        }))));
        Assert.Throws<InvalidDataException>(() => loader.Load(fixture.WriteRaw(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            stationConfigPath = "station.json",
            plannerId = PlannerId,
            rollingStatePath = fixture.RollingStatePath,
        }))));
        const string secret = "rtmps://example.invalid/live2/FAKE-SECRET";
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            loader.Load(fixture.WriteRaw(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                stationConfigPath = fixture.StationConfigPath,
                plannerId = PlannerId,
                rollingStatePath = fixture.RollingStatePath,
                rtmpUrl = secret,
            }))));

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_RoundTripsSchemaAndRedactsErrorsWithoutDuplicatingCp2RuntimeFields()
    {
        using var fixture = new ConfigurationFixture();
        const string secret = "rtmps://example.invalid/live2/FAKE-SECRET";
        RollingStationRuntimeState state = CreateClaimedState() with
        {
            Phase = RollingStationPhase.Failed,
            FailureDisposition = RollingStationFailureDisposition.Restartable,
            LastTransitionError = $"failed output {secret}",
            InitialCutoverSourceQueueId = new string('d', 64),
            InitialCutoverAcceptedAtUtc = DateTimeOffset.Parse("2026-10-01T11:00:00Z"),
        };
        var store = new RollingStationStateStore();

        await store.WriteAsync(fixture.RollingStatePath, state, CancellationToken.None);
        RollingStationRuntimeState roundTrip = store.Read(fixture.RollingStatePath);
        string json = File.ReadAllText(fixture.RollingStatePath);

        Assert.Equal(RollingStationRuntimeState.CurrentSchemaVersion, roundTrip.SchemaVersion);
        Assert.Equal("failed output [REDACTED]", roundTrip.LastTransitionError);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("ffmpegPid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stationPid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resumeGlobalIndex", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lastHeartbeatUtc", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task State_FailedAtomicWriteLeavesPriorDocumentValid()
    {
        using var fixture = new ConfigurationFixture();
        var normal = new RollingStationStateStore();
        RollingStationRuntimeState original = CreateClaimedState();
        await normal.WriteAsync(fixture.RollingStatePath, original, CancellationToken.None);

        var interrupted = new RollingStationStateStore(new InterruptedWriter());
        await Assert.ThrowsAsync<IOException>(() => interrupted.WriteAsync(
            fixture.RollingStatePath,
            original with { Phase = RollingStationPhase.Executing },
            CancellationToken.None));

        Assert.Equal(original, normal.Read(fixture.RollingStatePath));
    }

    [Fact]
    public void State_RejectsMalformedUnknownSchemaAndPartialIdentityGroups()
    {
        using var fixture = new ConfigurationFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.RollingStatePath)!);
        File.WriteAllText(fixture.RollingStatePath, "{ invalid");
        Assert.Throws<InvalidDataException>(() =>
            new RollingStationStateStore().Read(fixture.RollingStatePath));

        RollingStationRuntimeState unsupported = CreateClaimedState() with { SchemaVersion = 9 };
        Assert.Throws<InvalidDataException>(() => RollingStationStateStore.Validate(
            unsupported,
            fixture.RollingStatePath));
        Assert.Throws<InvalidDataException>(() => RollingStationStateStore.Validate(
            CreateClaimedState() with { ActiveQueueId = null },
            fixture.RollingStatePath));
        Assert.Throws<InvalidDataException>(() => RollingStationStateStore.Validate(
            CreateClaimedState() with
            {
                Phase = RollingStationPhase.Failed,
                FailureDisposition = null,
                LastTransitionError = null,
            },
            fixture.RollingStatePath));
    }

    [Fact]
    public void StateMachine_DefinesEveryLegalAndIllegalPhaseTransition()
    {
        var allowed = new HashSet<(RollingStationPhase?, RollingStationPhase)>
        {
            (null, RollingStationPhase.Claimed),
            (null, RollingStationPhase.WaitingForBlock),
            (RollingStationPhase.Claimed, RollingStationPhase.Executing),
            (RollingStationPhase.Claimed, RollingStationPhase.Stopped),
            (RollingStationPhase.Claimed, RollingStationPhase.Advancing),
            (RollingStationPhase.Claimed, RollingStationPhase.Failed),
            (RollingStationPhase.Executing, RollingStationPhase.Executing),
            (RollingStationPhase.Executing, RollingStationPhase.Stopped),
            (RollingStationPhase.Executing, RollingStationPhase.Advancing),
            (RollingStationPhase.Executing, RollingStationPhase.Failed),
            (RollingStationPhase.Stopped, RollingStationPhase.Executing),
            (RollingStationPhase.Stopped, RollingStationPhase.Advancing),
            (RollingStationPhase.Stopped, RollingStationPhase.Failed),
            (RollingStationPhase.Advancing, RollingStationPhase.Claimed),
            (RollingStationPhase.Advancing, RollingStationPhase.WaitingForBlock),
            (RollingStationPhase.Advancing, RollingStationPhase.Failed),
            (RollingStationPhase.WaitingForBlock, RollingStationPhase.WaitingForBlock),
            (RollingStationPhase.WaitingForBlock, RollingStationPhase.Claimed),
            (RollingStationPhase.WaitingForBlock, RollingStationPhase.Failed),
            (RollingStationPhase.Failed, RollingStationPhase.Executing),
            (RollingStationPhase.Failed, RollingStationPhase.Advancing),
            (RollingStationPhase.Failed, RollingStationPhase.WaitingForBlock),
            (RollingStationPhase.Failed, RollingStationPhase.Claimed),
            (RollingStationPhase.Failed, RollingStationPhase.Failed),
        };
        RollingStationPhase?[] sources = [null, .. Enum.GetValues<RollingStationPhase>().Cast<RollingStationPhase?>()];

        foreach (RollingStationPhase? source in sources)
        {
            foreach (RollingStationPhase target in Enum.GetValues<RollingStationPhase>())
            {
                Assert.Equal(
                    allowed.Contains((source, target)),
                    RollingStationStateMachine.CanTransition(source, target));
            }
        }
    }

    [Fact]
    public void CoordinatorLock_RejectsConcurrentHolderButNotStaleFilename()
    {
        using var fixture = new ConfigurationFixture();
        var provider = new RollingCoordinatorLockProvider();
        string lockPath = RollingCoordinatorLockProvider.GetLockPath(fixture.StationStatePath);
        using (IRollingCoordinatorLock first = provider.Acquire(fixture.StationStatePath))
        {
            Assert.Throws<RollingStationSafetyException>(() =>
                provider.Acquire(fixture.StationStatePath));
        }

        Assert.True(File.Exists(lockPath));
        using IRollingCoordinatorLock restarted = provider.Acquire(fixture.StationStatePath);
    }

    [Fact]
    public async Task Coordinator_ExistingRollingStateClassifiesUnavailableMediaMountAsRestartable()
    {
        using var fixture = new ConfigurationFixture();
        string mediaRoot = Directory.CreateDirectory(Path.Combine(fixture.Root, "media")).FullName;
        string missingLibrary = Path.Combine(mediaRoot, "library");
        File.WriteAllText(fixture.StationConfigPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            mediaRoot,
            libraryRoot = missingLibrary,
            statePath = fixture.StationStatePath,
            playlists = new[] { Path.Combine(fixture.Root, "playlist.json") },
        }));
        string rollingConfig = fixture.WriteConfiguration();
        await new RollingStationStateStore().WriteAsync(
            fixture.RollingStatePath,
            new RollingStationRuntimeState
            {
                PlannerId = PlannerId,
                Phase = RollingStationPhase.WaitingForBlock,
                UpdatedAtUtc = DateTimeOffset.Parse("2026-10-01T12:00:00Z"),
            },
            CancellationToken.None);

        RollingStationRunResult result = await new RollingStationCoordinator(
            new NeverExecutor()).RunAsync(
                rollingConfig,
                false,
                CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("unavailable", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    internal static RollingStationRuntimeState CreateClaimedState() => new()
    {
        PlannerId = PlannerId,
        Phase = RollingStationPhase.Claimed,
        ActiveBlockSequence = 1,
        ActiveBlockId = new string('a', 64),
        ActiveQueueId = new string('b', 64),
        ClaimedAtUtc = DateTimeOffset.Parse("2026-10-01T12:00:00Z"),
        UpdatedAtUtc = DateTimeOffset.Parse("2026-10-01T12:00:00Z"),
    };

    private sealed class InterruptedWriter : IAtomicTextFileWriter
    {
        public Task WriteAsync(string path, string content, CancellationToken cancellationToken)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, ".rolling-state.interrupted.partial"), content);
            throw new IOException("simulated interrupted rolling-state write");
        }
    }

    private sealed class NeverExecutor : IRollingBlockExecutor
    {
        public Task<StationRunResult> RunAsync(
            StationConfiguration configuration,
            BroadcastPlan plan,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Executor must not run while the media mount is unavailable.");
    }

    private sealed class ConfigurationFixture : IDisposable
    {
        public ConfigurationFixture()
        {
            Root = Directory.CreateTempSubdirectory("nzytetv-rolling-station-state-").FullName;
            StationConfigPath = Path.Combine(Root, "station.json");
            RollingStatePath = Path.Combine(Root, "runtime", "rolling-state.json");
            StationStatePath = Path.Combine(Root, "runtime", "state.json");
            File.WriteAllText(StationConfigPath, "{}");
        }

        public string Root { get; }

        public string StationConfigPath { get; }

        public string RollingStatePath { get; }

        public string StationStatePath { get; }

        public string WriteConfiguration() => WriteRaw(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            stationConfigPath = StationConfigPath,
            plannerId = PlannerId,
            rollingStatePath = RollingStatePath,
        }));

        public string WriteRaw(string json)
        {
            string path = Path.Combine(Root, $"rolling-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
