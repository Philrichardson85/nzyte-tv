using System.Text.Json;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class StationStateTests
{
    [Fact]
    public async Task WriteAsync_AtomicallyPublishesValidVersionedJsonWithoutSecrets()
    {
        using var fixture = new StateFixture();
        const string secret = "rtmps://example.invalid/live2/SECRET-KEY";
        StationRuntimeState state = fixture.CreateState() with { LastError = $"output failed for {secret}" };

        await new StationStateStore().WriteAsync(
            fixture.StatePath,
            state,
            CancellationToken.None);

        string json = File.ReadAllText(fixture.StatePath);
        StationRuntimeState roundTrip = new StationStateStore().Read(fixture.StatePath);
        Assert.Equal(StationRuntimeState.CurrentSchemaVersion, roundTrip.SchemaVersion);
        Assert.Equal(new string('a', 64), roundTrip.QueueId);
        Assert.Equal(2, roundTrip.QueueItemCount);
        Assert.Equal(0, roundTrip.ResumeGlobalIndex);
        Assert.Equal(StationStartMode.Fresh, roundTrip.LastStartMode);
        Assert.Equal("output failed for [REDACTED]", roundTrip.LastError);
        Assert.Contains("\"stationState\": \"broadcasting\"", json, StringComparison.Ordinal);
        Assert.Contains("\"broadcastState\": \"broadcasting\"", json, StringComparison.Ordinal);
        Assert.Contains("\"queueId\":", json, StringComparison.Ordinal);
        Assert.Contains("\"queueItemCount\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"currentGlobalIndex\": 0", json, StringComparison.Ordinal);
        Assert.Contains("\"lastCompletedGlobalIndex\": null", json, StringComparison.Ordinal);
        Assert.Contains("\"resumeGlobalIndex\": 0", json, StringComparison.Ordinal);
        Assert.Contains("\"lastStartMode\": \"fresh\"", json, StringComparison.Ordinal);
        Assert.Contains("\"resumeCount\": 0", json, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("rtmp", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(fixture.StatePath)!,
            "*.partial"));
    }

    [Fact]
    public void Read_SchemaVersionOneRemainsReadableWithoutInventingResumeMetadata()
    {
        using var fixture = new StateFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.StatePath)!);
        File.WriteAllText(fixture.StatePath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            stationState = "stopped",
            stationPid = 41,
            startedAtUtc = fixture.Now - TimeSpan.FromMinutes(1),
            lastHeartbeatUtc = fixture.Now,
            mediaRoot = fixture.MediaRoot,
            libraryRoot = fixture.LibraryRoot,
            broadcastState = "stopped",
            recoveryAttempts = 0,
            queuedPlaylistCount = 1,
            totalPlaylistCount = 2,
        }));

        StationRuntimeState state = new StationStateStore().Read(fixture.StatePath);

        Assert.Equal(StationRuntimeState.LegacySchemaVersion, state.SchemaVersion);
        Assert.Null(state.QueueId);
        Assert.Null(state.ResumeGlobalIndex);
        Assert.Null(state.LastStartMode);
        StationStatusSnapshot status = new StationStatusService(
            new MemoryStateStore(state),
            new RecordingProcessExistence([]),
            new FixedTimeProvider(fixture.Now)).GetStatus("ignored.json");
        Assert.Equal(StationStatusKind.Stopped, status.Status);
    }

    [Fact]
    public void ReadIfExists_MissingStateReturnsNull()
    {
        using var fixture = new StateFixture();

        Assert.Null(new StationStateStore().ReadIfExists(fixture.StatePath));
    }

    [Fact]
    public async Task FailedTemporaryWriteDoesNotCorruptPriorState()
    {
        using var fixture = new StateFixture();
        var normalStore = new StationStateStore();
        StationRuntimeState original = fixture.CreateState();
        await normalStore.WriteAsync(fixture.StatePath, original, CancellationToken.None);
        var failingStore = new StationStateStore(new InterruptedWriter());

        await Assert.ThrowsAsync<IOException>(() => failingStore.WriteAsync(
            fixture.StatePath,
            original with { StationState = StationState.Failed },
            CancellationToken.None));

        Assert.Equal(original, normalStore.Read(fixture.StatePath));
    }

    [Fact]
    public void Read_MissingStateIsHandledClearly()
    {
        using var fixture = new StateFixture();

        FileNotFoundException exception = Assert.Throws<FileNotFoundException>(() =>
            new StationStateStore().Read(fixture.StatePath));

        Assert.Contains("state file not found", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_MalformedStateIsHandledClearly()
    {
        using var fixture = new StateFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.StatePath)!);
        File.WriteAllText(fixture.StatePath, "{ invalid");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new StationStateStore().Read(fixture.StatePath));

        Assert.Contains("Malformed station state", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_InconsistentVersionTwoResumeMetadataIsRejected()
    {
        using var fixture = new StateFixture();
        StationRuntimeState inconsistent = fixture.CreateState() with
        {
            LastCompletedGlobalIndex = 0,
            ResumeGlobalIndex = 0,
        };
        await new StationStateStore().WriteAsync(
            fixture.StatePath,
            inconsistent,
            CancellationToken.None);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new StationStateStore().Read(fixture.StatePath));

        Assert.Contains("inconsistent", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Status_FreshHeartbeatAndLivePidIsRunningAndChecksOnlyRecordedIds()
    {
        using var fixture = new StateFixture();
        DateTimeOffset now = fixture.Now;
        StationRuntimeState state = fixture.CreateState() with
        {
            StationState = StationState.Broadcasting,
            BroadcastState = StationBroadcastState.Broadcasting,
            StationPid = 41,
            FfmpegPid = 42,
            LastHeartbeatUtc = now - TimeSpan.FromSeconds(5),
        };
        var store = new MemoryStateStore(state);
        var processes = new RecordingProcessExistence([41, 42]);
        var service = new StationStatusService(
            store,
            processes,
            new FixedTimeProvider(now));

        StationStatusSnapshot status = service.GetStatus("ignored.json");

        Assert.Equal(StationStatusKind.Running, status.Status);
        Assert.True(status.FfmpegProcessExists);
        Assert.Equal([41, 42], processes.RequestedIds);
    }

    [Fact]
    public void Status_StaleHeartbeatIsStaleEvenWhenPidExists()
    {
        using var fixture = new StateFixture();
        StationRuntimeState state = fixture.CreateState() with
        {
            StationState = StationState.Broadcasting,
            LastHeartbeatUtc = fixture.Now - TimeSpan.FromSeconds(31),
        };

        StationStatusSnapshot status = new StationStatusService(
            new MemoryStateStore(state),
            new RecordingProcessExistence([state.StationPid]),
            new FixedTimeProvider(fixture.Now)).GetStatus("ignored.json");

        Assert.Equal(StationStatusKind.Stale, status.Status);
    }

    [Fact]
    public void Status_DeadStationPidIsStaleEvenWithFreshHeartbeat()
    {
        using var fixture = new StateFixture();
        StationRuntimeState state = fixture.CreateState() with
        {
            StationState = StationState.Broadcasting,
            LastHeartbeatUtc = fixture.Now,
        };

        StationStatusSnapshot status = new StationStatusService(
            new MemoryStateStore(state),
            new RecordingProcessExistence([]),
            new FixedTimeProvider(fixture.Now)).GetStatus("ignored.json");

        Assert.Equal(StationStatusKind.Stale, status.Status);
    }

    [Theory]
    [InlineData(StationState.Completed, StationStatusKind.Completed)]
    [InlineData(StationState.Stopped, StationStatusKind.Stopped)]
    [InlineData(StationState.Failed, StationStatusKind.Failed)]
    public void Status_FinalStatesDoNotRequireLiveStationPid(
        StationState stationState,
        StationStatusKind expected)
    {
        using var fixture = new StateFixture();
        StationRuntimeState state = fixture.CreateState() with { StationState = stationState };

        StationStatusSnapshot status = new StationStatusService(
            new MemoryStateStore(state),
            new RecordingProcessExistence([]),
            new FixedTimeProvider(fixture.Now)).GetStatus("ignored.json");

        Assert.Equal(expected, status.Status);
    }

    private sealed class InterruptedWriter : IAtomicTextFileWriter
    {
        public Task WriteAsync(string path, string content, CancellationToken cancellationToken)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, ".state.interrupted.partial"), content);
            throw new IOException("simulated interrupted state write");
        }
    }

    private sealed class MemoryStateStore(StationRuntimeState state) : IStationStateStore
    {
        public Task WriteAsync(string path, StationRuntimeState value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public StationRuntimeState Read(string path) => state;

        public StationRuntimeState? ReadIfExists(string path) => state;
    }

    private sealed class RecordingProcessExistence(IEnumerable<int> liveIds) : IProcessExistence
    {
        private readonly HashSet<int> _liveIds = [.. liveIds];

        public List<int> RequestedIds { get; } = [];

        public bool Exists(int processId)
        {
            RequestedIds.Add(processId);
            return _liveIds.Contains(processId);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StateFixture : IDisposable
    {
        public StateFixture()
        {
            Root = Directory.CreateTempSubdirectory("nzytetv-station-state-").FullName;
            MediaRoot = Directory.CreateDirectory(Path.Combine(Root, "media")).FullName;
            LibraryRoot = Directory.CreateDirectory(Path.Combine(MediaRoot, "library")).FullName;
            StatePath = Path.Combine(Root, "state", "state.json");
        }

        public DateTimeOffset Now { get; } = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        public string Root { get; }

        public string MediaRoot { get; }

        public string LibraryRoot { get; }

        public string StatePath { get; }

        public StationRuntimeState CreateState() => new()
        {
            StationState = StationState.Broadcasting,
            BroadcastState = StationBroadcastState.Broadcasting,
            StationPid = 41,
            StartedAtUtc = Now - TimeSpan.FromMinutes(1),
            LastHeartbeatUtc = Now,
            MediaRoot = MediaRoot,
            LibraryRoot = LibraryRoot,
            TotalPlaylistCount = 2,
            QueuedPlaylistCount = 1,
            QueueId = new string('a', 64),
            QueueItemCount = 2,
            CurrentGlobalIndex = 0,
            LastCompletedGlobalIndex = null,
            ResumeGlobalIndex = 0,
            LastStartMode = StationStartMode.Fresh,
        };

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
