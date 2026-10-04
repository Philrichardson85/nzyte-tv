using System.Collections.Concurrent;
using System.Text.Json;
using NzyteTv.Core;

namespace NzyteTv.Media.Tests;

public sealed class BroadcastDiagnosticsTests
{
    private const string Destination = "rtmps://example.invalid/live2/SECRET-STREAM-KEY";

    [Fact]
    public void ProgressBatchParser_WaitsForCompleteContinueBatchAndParsesDocumentedValues()
    {
        var parser = new FfmpegProgressBatchParser();

        Assert.False(parser.TryAddLine("frame=125", out _));
        Assert.False(parser.TryAddLine("fps=29.97", out _));
        Assert.False(parser.TryAddLine("bitrate=5833.5kbits/s", out _));
        Assert.False(parser.TryAddLine("total_size=987654", out _));
        Assert.False(parser.TryAddLine("out_time_us=1234000", out _));
        Assert.False(parser.TryAddLine("dup_frames=2", out _));
        Assert.False(parser.TryAddLine("drop_frames=3", out _));
        Assert.False(parser.TryAddLine("speed=1.01x", out _));

        Assert.True(parser.TryAddLine("progress=continue", out FfmpegProgressBatch? batch));
        Assert.NotNull(batch);
        Assert.Equal(FfmpegProgressState.Continue, batch.State);
        Assert.Equal(1.234, batch.OutputTime!.Value.TotalSeconds, 3);
        Assert.Equal(125, batch.Frame);
        Assert.Equal(29.97, batch.FramesPerSecond);
        Assert.Equal(5833.5, batch.BitrateKbitsPerSecond);
        Assert.Equal(987654, batch.TotalSizeBytes);
        Assert.Equal(2, batch.DuplicateFrames);
        Assert.Equal(3, batch.DroppedFrames);
        Assert.Equal(1.01, batch.Speed);
    }

    [Fact]
    public void ProgressBatchParser_RecognizesEndAndDoesNotReusePreviousBatchValues()
    {
        var parser = new FfmpegProgressBatchParser();
        parser.TryAddLine("out_time=00:00:02.500000", out _);
        Assert.True(parser.TryAddLine("progress=continue", out FfmpegProgressBatch? first));

        Assert.True(parser.TryAddLine("progress=end", out FfmpegProgressBatch? final));

        Assert.Equal(TimeSpan.FromSeconds(2.5), first!.OutputTime);
        Assert.Equal(FfmpegProgressState.End, final!.State);
        Assert.Null(final.OutputTime);
    }

    [Fact]
    public async Task Recorder_TracksFirstProgressAndAdvancingVersusStagnantOutputMonotonically()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var store = new RecordingDiagnosticsStore();
        var recorder = new BroadcastDiagnosticsRecorder(store, time);
        BroadcastPlanItem item = Item();

        recorder.BeginAttempt("attempt-1", item, 7, launchReason: null);
        time.Advance(TimeSpan.FromSeconds(1));
        recorder.RecordProcessStarted("attempt-1", 4321);
        time.Advance(TimeSpan.FromSeconds(2));
        recorder.RecordProgress("attempt-1", Progress(TimeSpan.FromSeconds(1)));
        time.Advance(TimeSpan.FromSeconds(3));
        recorder.RecordProgress("attempt-1", Progress(TimeSpan.FromSeconds(1)));
        time.Advance(TimeSpan.FromSeconds(4));
        recorder.RecordProgress("attempt-1", Progress(TimeSpan.FromMilliseconds(500)));
        time.Advance(TimeSpan.FromSeconds(5));
        recorder.RecordProgress("attempt-1", Progress(TimeSpan.FromSeconds(2)));
        time.Advance(TimeSpan.FromSeconds(1));
        recorder.RecordProgress("attempt-1", Progress(TimeSpan.FromSeconds(2)));
        await recorder.FlushAsync();

        BroadcastAttemptDiagnostics attempt = Assert.Single(recorder.Snapshot().Attempts);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero), attempt.LaunchTimeUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 12, 0, 1, TimeSpan.Zero), attempt.ProcessStartTimeUtc);
        Assert.Equal(1000, attempt.ProcessStartElapsedMilliseconds);
        Assert.Equal(7, attempt.StartingGlobalItemIndex);
        Assert.Equal("playlist-01.json", attempt.StartingPlaylistFileName);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 12, 0, 3, TimeSpan.Zero), attempt.FirstProgressTimeUtc);
        Assert.Equal(3000, attempt.FirstProgressElapsedMilliseconds);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 12, 0, 16, TimeSpan.Zero), attempt.LastProgressTimeUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 12, 0, 15, TimeSpan.Zero), attempt.LastAdvancingProgressTimeUtc);
        Assert.Equal(TimeSpan.FromSeconds(2), attempt.LatestOutputTime);
        Assert.Equal(5, attempt.ProgressBatchCount);
        Assert.Equal(2, attempt.AdvancingProgressBatchCount);
        Assert.Equal(3, attempt.StagnantProgressBatchCount);
    }

    [Fact]
    public async Task Broadcaster_SeparatesProgressFromStandardErrorAndRecordsAttemptLifecycle()
    {
        var store = new RecordingDiagnosticsStore();
        var recorder = new BroadcastDiagnosticsRecorder(store);
        var runner = new LifecycleRunner(exitCode: 23);
        var forwardedDiagnostics = new List<string>();
        BroadcastPlan plan = Plan();

        BroadcastAttemptResult result = await new FfmpegBroadcaster("ffmpeg", runner, recorder)
            .BroadcastAttemptAsync(
                plan,
                Destination,
                0,
                forwardedDiagnostics.Add,
                onProgress: null,
                CancellationToken.None);
        await recorder.FlushAsync();

        BroadcastAttemptDiagnostics attempt = Assert.Single(recorder.Snapshot().Attempts);
        Assert.Equal(result.AttemptId, attempt.AttemptId);
        Assert.Equal(777, attempt.FfmpegPid);
        Assert.NotNull(attempt.ProcessStartTimeUtc);
        Assert.NotNull(attempt.FirstProgressTimeUtc);
        Assert.Equal(TimeSpan.FromSeconds(1), attempt.LatestOutputTime);
        Assert.NotNull(attempt.ProcessExitTimeUtc);
        Assert.Equal(23, attempt.ProcessExitCode);
        Assert.Contains("frame=1", forwardedDiagnostics);
        Assert.Contains("progress=continue", forwardedDiagnostics);
        Assert.Contains("network warning", forwardedDiagnostics);
        Assert.Equal(["network warning"], attempt.RecentStandardError);
        Assert.DoesNotContain("frame=1", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("network warning", result.Diagnostic, StringComparison.Ordinal);
        Assert.False(runner.Request!.CaptureStandardOutput);
        Assert.False(runner.Request.CaptureStandardError);
    }

    [Fact]
    public void DiagnosticBuffer_IsBoundedSynchronizedAndTruncatesOversizedLines()
    {
        var buffer = new BoundedDiagnosticBuffer(capacity: 8, maximumLineLength: 24);

        Parallel.For(0, 500, index => buffer.Add($"line-{index:D4}-{new string('x', 40)}"));

        IReadOnlyList<string> lines = buffer.Snapshot();
        Assert.Equal(8, lines.Count);
        Assert.All(lines, line => Assert.InRange(line.Length, 1, 24));
        Assert.Equal(8, lines.Distinct(StringComparer.Ordinal).Count());

        var endpointBuffer = new BoundedDiagnosticBuffer(capacity: 4);
        endpointBuffer.Add("initial connection error");
        endpointBuffer.Add("initial detail");
        for (int index = 0; index < 20; index++)
        {
            endpointBuffer.Add($"routine-{index}");
        }

        endpointBuffer.Add("final muxing error");
        Assert.Contains("initial connection error", endpointBuffer.Snapshot());
        Assert.Contains("final muxing error", endpointBuffer.Snapshot());
    }

    [Fact]
    public async Task Recorder_ConcurrentProgressAndErrorCallbacksRemainConsistent()
    {
        var recorder = new BroadcastDiagnosticsRecorder(new RecordingDiagnosticsStore());
        recorder.BeginAttempt("concurrent", Item(), 0, null);

        var actions = new ConcurrentBag<Action>();
        for (int index = 1; index <= 200; index++)
        {
            int captured = index;
            actions.Add(() => recorder.RecordProgress(
                "concurrent",
                Progress(TimeSpan.FromMilliseconds(captured))));
            actions.Add(() => recorder.RecordStandardError("concurrent", $"diagnostic-{captured}"));
        }

        Parallel.ForEach(actions, action => action());
        await recorder.FlushAsync();

        BroadcastAttemptDiagnostics attempt = Assert.Single(recorder.Snapshot().Attempts);
        Assert.Equal(200, attempt.ProgressBatchCount);
        Assert.InRange(attempt.AdvancingProgressBatchCount, 1, 200);
        Assert.Equal(200, attempt.AdvancingProgressBatchCount + attempt.StagnantProgressBatchCount);
        Assert.Equal(50, attempt.RecentStandardError.Count);
        Assert.All(attempt.RecentStandardError, line => Assert.StartsWith("diagnostic-", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Broadcaster_CancellationRecordsCancelledAttemptWithoutChangingExceptionFlow()
    {
        var recorder = new BroadcastDiagnosticsRecorder(new RecordingDiagnosticsStore());
        var runner = new CancellableRunner();
        using var cancellation = new CancellationTokenSource();
        Task<BroadcastAttemptResult> action = new FfmpegBroadcaster("ffmpeg", runner, recorder)
            .BroadcastAttemptAsync(Plan(), Destination, 0, null, null, cancellation.Token);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        await recorder.FlushAsync();

        BroadcastAttemptDiagnostics attempt = Assert.Single(recorder.Snapshot().Attempts);
        Assert.True(attempt.Cancelled);
        Assert.Equal(BroadcastFailureKind.Cancelled, attempt.FailureClassification);
        Assert.Equal(BroadcastRetryDecision.Cancelled, attempt.RetryDecision);
        Assert.Null(attempt.ProcessExitCode);
    }

    [Fact]
    public async Task Recovery_RecordsUnexpectedExitDecisionAndReliableRetryLaunchReason()
    {
        var recorder = new BroadcastDiagnosticsRecorder(new RecordingDiagnosticsStore());
        var runner = new SequencedLifecycleRunner(17, 0);
        var recovery = new BroadcastRecoveryRunner(
            new FfmpegBroadcaster("ffmpeg", runner, recorder),
            new BroadcastRecoveryPolicy(MaxConsecutiveRetries: 1, MaximumDelay: TimeSpan.Zero));

        BroadcastRecoveryResult result = await recovery.RunAsync(
            Plan(), Destination, null, null, CancellationToken.None);
        await recorder.FlushAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, recorder.Snapshot().Attempts.Count);
        BroadcastAttemptDiagnostics first = recorder.Snapshot().Attempts[0];
        BroadcastAttemptDiagnostics second = recorder.Snapshot().Attempts[1];
        Assert.Equal(BroadcastFailureKind.Ambiguous, first.FailureClassification);
        Assert.Equal(BroadcastRetryDecision.RetryScheduled, first.RetryDecision);
        Assert.Equal(1, first.RetryAttempt);
        Assert.Null(first.LaunchReason);
        Assert.Equal(BroadcastLaunchReason.RecoveryRetry, second.LaunchReason);
        Assert.Equal(BroadcastRetryDecision.Completed, second.RetryDecision);
    }

    [Fact]
    public void CredentialRedactor_RemovesDestinationsKeysTokensAndAuthorizationValues()
    {
        string unsafeText = $"""
            failed {Destination}
            repeated SECRET-STREAM-KEY
            Authorization: Bearer oauth-access-value
            access_token=access-value
            "refresh_token": "refresh-value"
            client_secret=client-value
            stream_key=stream-value
            """;

        string safe = BroadcastCredentialRedactor.Redact(unsafeText, Destination);

        Assert.DoesNotContain(Destination, safe, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-STREAM-KEY", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("oauth-access-value", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("access-value", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-value", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("client-value", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("stream-value", safe, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", safe, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdvisoryDiagnostics_PersistAtomicallyWithSchemaAndSafeAllowlistedFields()
    {
        string root = Directory.CreateTempSubdirectory("nzytetv-diagnostics-").FullName;
        try
        {
            string path = Path.Combine(root, "broadcast-diagnostics.json");
            var recorder = Assert.IsType<BroadcastDiagnosticsRecorder>(
                BroadcastDiagnosticsConfiguration.Create(path));
            recorder.BeginAttempt("persisted", Item(), 4, null);
            recorder.RecordStandardError("persisted", $"failed {Destination}");
            recorder.RecordProcessExit("persisted", 8, false, [$"failed {Destination}"]);
            await recorder.FlushAsync();

            Assert.True(File.Exists(path));
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(
                BroadcastDiagnosticsDocument.CurrentSchemaVersion,
                json.RootElement.GetProperty("schemaVersion").GetInt32());
            string content = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(Destination, content, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET-STREAM-KEY", content, StringComparison.Ordinal);
            Assert.DoesNotContain("arguments", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PersistenceFailures_AreIsolatedFromBroadcastResult()
    {
        var recorder = new BroadcastDiagnosticsRecorder(new ThrowingDiagnosticsStore());

        BroadcastAttemptResult result = await new FfmpegBroadcaster(
            "ffmpeg",
            new LifecycleRunner(exitCode: 0),
            recorder).BroadcastAttemptAsync(
                Plan(), Destination, 0, null, null, CancellationToken.None);

        await recorder.FlushAsync();
        Assert.Equal(0, result.FfmpegExitCode);
        Assert.True(recorder.PersistenceFailureCount > 0);
    }

    [Fact]
    public async Task CorruptAdvisoryFile_IsIgnoredAndReplacedWithoutBlockingAnAttempt()
    {
        string root = Directory.CreateTempSubdirectory("nzytetv-corrupt-diagnostics-").FullName;
        try
        {
            string path = Path.Combine(root, "broadcast-diagnostics.json");
            await File.WriteAllTextAsync(path, "{ not-json");
            var recorder = Assert.IsType<BroadcastDiagnosticsRecorder>(
                BroadcastDiagnosticsConfiguration.Create(path));

            recorder.BeginAttempt("replacement", Item(), 0, null);
            await recorder.FlushAsync();

            Assert.True(recorder.PersistenceFailureCount > 0);
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(
                BroadcastDiagnosticsDocument.CurrentSchemaVersion,
                json.RootElement.GetProperty("schemaVersion").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProgressPersistence_IsThrottledBetweenLifecycleWrites()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var store = new RecordingDiagnosticsStore();
        var recorder = new BroadcastDiagnosticsRecorder(store, time);
        recorder.BeginAttempt("throttled", Item(), 0, null);

        for (int index = 1; index <= 1000; index++)
        {
            recorder.RecordProgress("throttled", Progress(TimeSpan.FromMilliseconds(index)));
        }

        await recorder.FlushAsync();

        Assert.InRange(store.WriteCount, 1, 3);
        Assert.Equal(1000, Assert.Single(recorder.Snapshot().Attempts).ProgressBatchCount);
    }

    [Fact]
    public void DiagnosticsConfiguration_DisabledIsANoOp()
    {
        IBroadcastDiagnostics diagnostics = BroadcastDiagnosticsConfiguration.Create(null);

        Assert.False(diagnostics.IsEnabled);
        Assert.Same(DisabledBroadcastDiagnostics.Instance, diagnostics);
    }

    private static FfmpegProgressBatch Progress(TimeSpan outputTime) => new(
        FfmpegProgressState.Continue,
        outputTime,
        Frame: null,
        FramesPerSecond: null,
        BitrateKbitsPerSecond: null,
        TotalSizeBytes: null,
        DuplicateFrames: null,
        DroppedFrames: null,
        Speed: null);

    private static BroadcastPlanItem Item() => new(
        "playlist-01.json",
        1,
        "asset-1",
        "folder/item.mp4",
        Path.Combine(Path.GetTempPath(), "folder", "item.mp4"),
        10,
        "Item");

    private static BroadcastPlan Plan()
    {
        BroadcastPlanItem item = Item();
        return new BroadcastPlan(
            Path.GetTempPath(),
            [item.PlaylistPath],
            [item],
            [],
            1,
            item.DurationSeconds);
    }

    private sealed class RecordingDiagnosticsStore : IBroadcastDiagnosticsStore
    {
        private int _writeCount;

        public int WriteCount => Volatile.Read(ref _writeCount);

        public BroadcastDiagnosticsDocument? LastDocument { get; private set; }

        public BroadcastDiagnosticsDocument? ReadIfExists() => null;

        public Task WriteAsync(BroadcastDiagnosticsDocument document, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastDocument = document;
            Interlocked.Increment(ref _writeCount);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingDiagnosticsStore : IBroadcastDiagnosticsStore
    {
        public BroadcastDiagnosticsDocument? ReadIfExists() => null;

        public Task WriteAsync(BroadcastDiagnosticsDocument document, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("injected advisory write failure"));
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
            _timestamp += duration.Ticks;
        }
    }

    private sealed class LifecycleRunner(int exitCode) : IProcessRunner
    {
        public ProcessRequest? Request { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            request.OnStarted?.Invoke(777);
            request.OnStandardOutput?.Invoke("frame=1");
            request.OnStandardError?.Invoke("network warning");
            request.OnStandardOutput?.Invoke("out_time_us=1000000");
            request.OnStandardOutput?.Invoke("progress=continue");
            request.OnExited?.Invoke(777);
            return Task.FromResult(new ProcessResult(exitCode, string.Empty, "network warning"));
        }
    }

    private sealed class CancellableRunner : IProcessRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            request.OnStarted?.Invoke(888);
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                request.OnExited?.Invoke(888);
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }

    private sealed class SequencedLifecycleRunner(params int[] exitCodes) : IProcessRunner
    {
        private readonly Queue<int> _exitCodes = new(exitCodes);
        private int _processId = 900;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            int processId = Interlocked.Increment(ref _processId);
            request.OnStarted?.Invoke(processId);
            int exitCode = _exitCodes.Dequeue();
            if (exitCode != 0)
            {
                request.OnStandardError?.Invoke("unexpected child exit");
            }

            request.OnExited?.Invoke(processId);
            return Task.FromResult(new ProcessResult(
                exitCode,
                string.Empty,
                exitCode == 0 ? string.Empty : "unexpected child exit"));
        }
    }
}
