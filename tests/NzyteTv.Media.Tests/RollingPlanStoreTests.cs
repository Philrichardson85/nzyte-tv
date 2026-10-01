using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class RollingPlanStoreTests
{
    [Fact]
    public async Task ImmutableWrite_IsIdempotentButRefusesDifferentContent()
    {
        string root = Directory.CreateTempSubdirectory("nzytetv-rolling-store-").FullName;
        try
        {
            string path = Path.Combine(root, "artifact.json");
            var store = new RollingPlanStore();
            var first = new PlaylistHistoryDocument { Plays = [] };
            await store.WriteImmutableAsync(path, first, CancellationToken.None);
            DateTime write = File.GetLastWriteTimeUtc(path);

            await store.WriteImmutableAsync(path, first, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteImmutableAsync(
                path,
                new PlaylistHistoryDocument { ScheduleEndUtc = DateTimeOffset.UtcNow, Plays = [] },
                CancellationToken.None));

            Assert.Equal(write, File.GetLastWriteTimeUtc(path));
            Assert.Null(new PlaylistHistoryStore().Load(path).ScheduleEndUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedManifestReplacementLeavesPriorManifestUnchangedAndValid()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        RollingInitializationResult initialized = await fixture.InitializeAsync(planner);
        string before = File.ReadAllText(initialized.Paths.ManifestPath);
        var failing = new RollingPlanStore(new ThrowingAtomicWriter());

        await Assert.ThrowsAsync<IOException>(() => failing.WriteManifestAsync(
            initialized.Paths.ManifestPath,
            initialized.Manifest,
            CancellationToken.None));

        Assert.Equal(before, File.ReadAllText(initialized.Paths.ManifestPath));
        RollingManifestValidator.Validate(new RollingPlanStore().LoadManifest(initialized.Paths.ManifestPath));
    }

    [Fact]
    public void JsonReader_RejectsUnmappedFieldsSoSecretsCannotHideInArtifacts()
    {
        string json = """
            {
              "schemaVersion": 1,
              "plannerId": "00112233445566778899aabbccddeeff",
              "baseSeed": 1,
              "targetBlockDurationSeconds": 21600,
              "targetPreparedBlockCount": 3,
              "nextSequence": 1,
              "genesisHistory": { "relativePath": "history/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
              "historyHead": { "relativePath": "history/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
              "blocks": [],
              "initializedAtUtc": "2026-10-01T00:00:00Z",
              "rtmpsDestination": "rtmps://example.invalid/live2/FAKE"
            }
            """;

        Assert.Throws<InvalidDataException>(() =>
            RollingProgrammingJson.Deserialize<RollingProgrammingManifest>(json, "test manifest"));
    }

    private sealed class ThrowingAtomicWriter : IAtomicTextFileWriter
    {
        public Task WriteAsync(string path, string content, CancellationToken cancellationToken) =>
            throw new IOException("Injected write interruption.");
    }
}
