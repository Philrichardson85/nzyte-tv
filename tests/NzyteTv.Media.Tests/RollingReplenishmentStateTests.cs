using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class RollingReplenishmentStateTests
{
    private const string Secret = "rtmps://example.invalid/live2/FAKE-SECRET";

    [Fact]
    public async Task State_RoundTripsAtomicallyAndDerivesPathFromRollingState()
    {
        using var directory = new TemporaryDirectory();
        string rollingPath = Path.Combine(directory.Path, "rolling-state.json");
        string path = RollingReplenishmentStateStore.GetPath(rollingPath);
        var store = new RollingReplenishmentStateStore();
        RollingReplenishmentState expected = CreateState();

        await store.WriteAsync(path, expected, CancellationToken.None);
        RollingReplenishmentState actual = store.Read(path);

        Assert.Equal(rollingPath + ".replenishment.json", path);
        Assert.Equal(expected, actual);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.partial"));
    }

    [Fact]
    public async Task State_RedactsDestinationAndNeverSerializesRuntimeProcessFields()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "replenishment.json");
        var store = new RollingReplenishmentStateStore();
        RollingReplenishmentState state = CreateState() with
        {
            Health = RollingReplenishmentHealth.Blocked,
            ErrorClassification = RollingReplenishmentErrorClassification.PlanningBlocked,
            LastErrorAtUtc = DateTimeOffset.Parse("2026-10-01T12:01:00Z"),
            LastError = $"future planning rejected {Secret}",
        };

        await store.WriteAsync(path, state, CancellationToken.None);
        string json = File.ReadAllText(path);

        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ffmpegPid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stationPid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resumeGlobalIndex", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("commandLine", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InterruptedWrite_DoesNotCorruptPriorDocument()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "replenishment.json");
        var normal = new RollingReplenishmentStateStore();
        RollingReplenishmentState prior = CreateState();
        await normal.WriteAsync(path, prior, CancellationToken.None);
        var failing = new RollingReplenishmentStateStore(new ThrowingWriter());

        await Assert.ThrowsAsync<IOException>(() => failing.WriteAsync(
            path,
            prior with
            {
                AnchorSequence = 3,
                HighestCommittedSequence = 4,
                RequiredHighestSequence = 5,
                BufferDeficit = 1,
            },
            CancellationToken.None));

        Assert.Equal(prior, normal.Read(path));
    }

    [Fact]
    public void MalformedOrUnsupportedState_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "replenishment.json");
        File.WriteAllText(path, "{ \"schemaVersion\": 999 }");

        Assert.Throws<InvalidDataException>(() =>
            new RollingReplenishmentStateStore().Read(path));
    }

    private static RollingReplenishmentState CreateState() => new()
    {
        PlannerId = "00112233445566778899aabbccddeeff",
        Health = RollingReplenishmentHealth.Healthy,
        AnchorSequence = 2,
        HighestCommittedSequence = 4,
        RequiredHighestSequence = 4,
        FutureBlockTarget = 2,
        BufferDeficit = 0,
        LastAttemptAtUtc = DateTimeOffset.Parse("2026-10-01T12:00:00Z"),
        LastSuccessAtUtc = DateTimeOffset.Parse("2026-10-01T12:00:01Z"),
        ErrorClassification = RollingReplenishmentErrorClassification.None,
        UpdatedAtUtc = DateTimeOffset.Parse("2026-10-01T12:00:01Z"),
    };

    private sealed class ThrowingWriter : IAtomicTextFileWriter
    {
        public Task WriteAsync(string path, string content, CancellationToken cancellationToken) =>
            throw new IOException("simulated sidecar write interruption");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = Directory
            .CreateTempSubdirectory("nzytetv-replenishment-state-").FullName;

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
