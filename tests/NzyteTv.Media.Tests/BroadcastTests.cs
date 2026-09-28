using System.Text.Json;
using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class BroadcastTests
{
    private const string Destination = "rtmps://example.invalid/live2/SECRET-KEY";

    [Fact]
    public void CreatePlan_OneValidPlaylistResolvesNestedPathWithSpaces()
    {
        using var fixture = new BroadcastFixture();
        fixture.AddReadyMedia("Animated Visuals/My Clip (Final).mp4");
        string playlist = fixture.WritePlaylist(
            "one.json",
            Item(1, "Animated Visuals/My Clip (Final).mp4", 28.677));

        BroadcastPlan plan = new BroadcastPlanner().CreatePlan([playlist], fixture.LibraryRoot);

        BroadcastPlanItem item = Assert.Single(plan.Items);
        Assert.Equal(Path.GetFullPath(fixture.GetMediaPath("Animated Visuals/My Clip (Final).mp4")), item.MediaPath);
        Assert.True(plan.IsReady);
        Assert.Equal(28.677, plan.ScheduledDurationSeconds, precision: 3);
    }

    [Fact]
    public void CreatePlan_MultiplePlaylistsAndItemSequencesRetainRequiredOrder()
    {
        using var fixture = new BroadcastFixture();
        fixture.AddReadyMedia("Music Videos/first.mp4");
        fixture.AddReadyMedia("Music Videos/second.mp4");
        fixture.AddReadyMedia("Music Videos/third.mp4");
        string firstPlaylist = fixture.WritePlaylist(
            "01.json",
            Item(2, "Music Videos/second.mp4", 20),
            Item(1, "Music Videos/first.mp4", 10));
        string secondPlaylist = fixture.WritePlaylist(
            "02.json",
            Item(1, "Music Videos/third.mp4", 30));

        BroadcastPlan plan = new BroadcastPlanner().CreatePlan(
            [firstPlaylist, secondPlaylist],
            fixture.LibraryRoot);

        Assert.Equal([firstPlaylist, secondPlaylist], plan.PlaylistPaths);
        Assert.Equal(
            ["Music Videos/first.mp4", "Music Videos/second.mp4", "Music Videos/third.mp4"],
            plan.Items.Select(item => item.RelativePath));
        Assert.Equal(60, plan.ScheduledDurationSeconds);
    }

    [Fact]
    public void CreatePlan_MissingPlaylistIsRejected()
    {
        using var fixture = new BroadcastFixture();

        Assert.Throws<FileNotFoundException>(() => new BroadcastPlanner().CreatePlan(
            [Path.Combine(fixture.Root, "missing.json")],
            fixture.LibraryRoot));
    }

    [Fact]
    public void CreatePlan_InvalidJsonIsRejected()
    {
        using var fixture = new BroadcastFixture();
        string playlist = fixture.WriteRawPlaylist("invalid.json", "{ invalid");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new BroadcastPlanner().CreatePlan([playlist], fixture.LibraryRoot));

        Assert.Contains("Malformed playlist JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePlan_UnsupportedSchemaIsRejected()
    {
        using var fixture = new BroadcastFixture();
        string playlist = fixture.WriteRawPlaylist("future.json", """
            { "schemaVersion": 99, "items": [{ "sequence": 1, "relativePath": "a.mp4", "durationSeconds": 1 }] }
            """);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new BroadcastPlanner().CreatePlan([playlist], fixture.LibraryRoot));

        Assert.Contains("schemaVersion 99", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 1 }")]
    [InlineData("{ \"schemaVersion\": 1, \"items\": [] }")]
    public void CreatePlan_MissingOrEmptyItemsAreRejected(string json)
    {
        using var fixture = new BroadcastFixture();
        string playlist = fixture.WriteRawPlaylist("empty.json", json);

        Assert.Throws<InvalidDataException>(() =>
            new BroadcastPlanner().CreatePlan([playlist], fixture.LibraryRoot));
    }

    [Fact]
    public void CreatePlan_MissingRelativePathIsInvalid()
    {
        using var fixture = new BroadcastFixture();
        string playlist = fixture.WritePlaylist("missing-path.json", Item(1, null, 10));

        BroadcastPlan plan = new BroadcastPlanner().CreatePlan([playlist], fixture.LibraryRoot);

        Assert.False(plan.IsReady);
        Assert.Equal(1, plan.InvalidPathCount);
    }

    [Fact]
    public void CreatePlan_MissingMediaFileIsReported()
    {
        using var fixture = new BroadcastFixture();
        string playlist = fixture.WritePlaylist("missing-media.json", Item(1, "Music Videos/missing.mp4", 10));

        BroadcastPlan plan = new BroadcastPlanner().CreatePlan([playlist], fixture.LibraryRoot);

        Assert.False(plan.IsReady);
        Assert.Equal(1, plan.MissingFileCount);
    }

    [Theory]
    [InlineData("../outside.mp4")]
    [InlineData("nested/../../outside.mp4")]
    public void CreatePlan_PathTraversalIsRejected(string relativePath)
    {
        using var fixture = new BroadcastFixture();
        string playlist = fixture.WritePlaylist("traversal.json", Item(1, relativePath, 10));

        BroadcastPlan plan = new BroadcastPlanner().CreatePlan([playlist], fixture.LibraryRoot);

        Assert.False(plan.IsReady);
        Assert.Equal(1, plan.InvalidPathCount);
    }

    [Fact]
    public void CreatePlan_AbsolutePathIsRejectedEvenWhenFileExists()
    {
        using var fixture = new BroadcastFixture();
        string outside = Path.Combine(fixture.Root, "outside.mp4");
        File.WriteAllText(outside, "outside");
        string playlist = fixture.WritePlaylist("absolute.json", Item(1, outside, 10));

        BroadcastPlan plan = new BroadcastPlanner().CreatePlan([playlist], fixture.LibraryRoot);

        Assert.False(plan.IsReady);
        Assert.Equal(1, plan.InvalidPathCount);
    }

    [Fact]
    public void CreatePlan_MissingManifestIsReportedAsUnready()
    {
        using var fixture = new BroadcastFixture();
        fixture.AddMedia("Music Videos/unready.mp4");
        string playlist = fixture.WritePlaylist("unready.json", Item(1, "Music Videos/unready.mp4", 10));

        BroadcastPlan plan = new BroadcastPlanner().CreatePlan([playlist], fixture.LibraryRoot);

        Assert.False(plan.IsReady);
        Assert.Equal(1, plan.UnreadyAssetCount);
    }

    [Fact]
    public void CreatePlan_DuplicateSequenceValuesAreRejected()
    {
        using var fixture = new BroadcastFixture();
        string playlist = fixture.WritePlaylist(
            "duplicates.json",
            Item(1, "a.mp4", 10),
            Item(1, "b.mp4", 10));

        Assert.Throws<InvalidDataException>(() =>
            new BroadcastPlanner().CreatePlan([playlist], fixture.LibraryRoot));
    }

    [Fact]
    public void ConcatContent_EscapesApostrophesAndPreservesSpaces()
    {
        string path = Path.Combine(Path.GetTempPath(), "Nzyte's Clips", "My Clip.mp4");

        string content = FfmpegConcatFile.BuildContent([path]);

        Assert.StartsWith("ffconcat version 1.0\n", content, StringComparison.Ordinal);
        Assert.Contains("Nzyte'\\''s Clips", content, StringComparison.Ordinal);
        Assert.Contains("My Clip.mp4", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Destination_DryRunDoesNotRequireEnvironmentValueButLiveBroadcastDoes()
    {
        Assert.Null(BroadcastDestination.Resolve(null, dryRun: true));
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            BroadcastDestination.Resolve(null, dryRun: false));

        Assert.Contains(BroadcastDestination.DefaultEnvironmentVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArguments_UsesConcatRealtimeStreamCopyFlvWithoutEncoders()
    {
        IReadOnlyList<string> arguments = BroadcastFfmpegArgumentBuilder.Build("playlist.ffconcat", Destination);

        AssertOption(arguments, "-f", "concat", occurrence: 1);
        AssertOption(arguments, "-safe", "0");
        Assert.Contains("-re", arguments);
        AssertOption(arguments, "-c", "copy");
        AssertOption(arguments, "-f", "flv", occurrence: 2);
        Assert.DoesNotContain("-c:v", arguments);
        Assert.DoesNotContain("-c:a", arguments);
        Assert.DoesNotContain("libx264", arguments);
        Assert.DoesNotContain("h264_nvenc", arguments);
        Assert.DoesNotContain("aac", arguments);
        Assert.DoesNotContain("-vf", arguments);
        Assert.DoesNotContain("-filter_complex", arguments);
    }

    [Fact]
    public async Task BroadcastAttemptAsync_NonzeroExitReturnsAttemptCleansTempFileAndRedactsDestination()
    {
        using var fixture = new BroadcastFixture();
        BroadcastPlan plan = fixture.ReadyPlan();
        var runner = new RecordingRunner(exitCode: 7, emittedLine: $"Failed to connect to {Destination}");
        var output = new List<string>();

        BroadcastAttemptResult result = await new FfmpegBroadcaster("ffmpeg", runner).BroadcastAttemptAsync(
            plan, Destination, 0, output.Add, onProgress: null, CancellationToken.None);

        Assert.Equal(7, result.FfmpegExitCode);
        Assert.NotNull(runner.ConcatPath);
        Assert.False(File.Exists(runner.ConcatPath));
        Assert.DoesNotContain(Destination, string.Join(Environment.NewLine, output), StringComparison.Ordinal);
        Assert.Contains(output, line => line.Contains("[REDACTED]", StringComparison.Ordinal));
        Assert.DoesNotContain(Destination, result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BroadcastAttemptAsync_CancellationReachesRunnerAndCleansTempFile()
    {
        using var fixture = new BroadcastFixture();
        BroadcastPlan plan = fixture.ReadyPlan();
        var runner = new CancelAwareRunner();
        using var cancellation = new CancellationTokenSource();

        Task action = new FfmpegBroadcaster("ffmpeg", runner).BroadcastAttemptAsync(
            plan, Destination, 0, onOutput: null, onProgress: null, cancellation.Token);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.True(runner.CancellationObserved);
        Assert.NotNull(runner.ConcatPath);
        Assert.False(File.Exists(runner.ConcatPath));
    }

    private static object Item(int sequence, string? relativePath, double durationSeconds) => new
    {
        sequence,
        assetId = $"asset-{sequence}",
        relativePath,
        durationSeconds,
    };

    private static void AssertOption(
        IReadOnlyList<string> arguments,
        string option,
        string expectedValue,
        int occurrence = 1)
    {
        int index = -1;
        for (int count = 0; count < occurrence; count++)
        {
            index = arguments.ToList().FindIndex(index + 1, value => value == option);
        }

        Assert.True(index >= 0 && index + 1 < arguments.Count);
        Assert.Equal(expectedValue, arguments[index + 1]);
    }

    private sealed class BroadcastFixture : IDisposable
    {
        public BroadcastFixture()
        {
            Root = Directory.CreateTempSubdirectory("nzytetv-broadcast-").FullName;
            LibraryRoot = Directory.CreateDirectory(Path.Combine(Root, "library")).FullName;
        }

        public string Root { get; }

        public string LibraryRoot { get; }

        public string GetMediaPath(string relativePath) => Path.Combine(
            LibraryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        public string AddMedia(string relativePath)
        {
            string path = GetMediaPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "normalized media");
            return path;
        }

        public string AddReadyMedia(string relativePath)
        {
            string path = AddMedia(relativePath);
            File.WriteAllText(SourceManifestStore.GetManifestPath(path), "{}");
            return path;
        }

        public string WritePlaylist(string name, params object[] items) => WriteRawPlaylist(
            name,
            JsonSerializer.Serialize(new { schemaVersion = PlaylistDocument.CurrentSchemaVersion, items }));

        public string WriteRawPlaylist(string name, string json)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllText(path, json);
            return path;
        }

        public BroadcastPlan ReadyPlan()
        {
            AddReadyMedia("Music Videos/ready.mp4");
            string playlist = WritePlaylist("ready.json", Item(1, "Music Videos/ready.mp4", 60));
            return new BroadcastPlanner().CreatePlan([playlist], LibraryRoot);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class RecordingRunner(int exitCode, string? emittedLine = null) : IProcessRunner
    {
        public string? ConcatPath { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            int inputIndex = request.Arguments.ToList().IndexOf("-i");
            ConcatPath = request.Arguments[inputIndex + 1];
            Assert.True(File.Exists(ConcatPath));
            if (emittedLine is not null)
            {
                request.OnStandardError?.Invoke(emittedLine);
            }

            return Task.FromResult(new ProcessResult(exitCode, string.Empty, emittedLine ?? string.Empty));
        }
    }

    private sealed class CancelAwareRunner : IProcessRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? ConcatPath { get; private set; }

        public bool CancellationObserved { get; private set; }

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            int inputIndex = request.Arguments.ToList().IndexOf("-i");
            ConcatPath = request.Arguments[inputIndex + 1];
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }
}
