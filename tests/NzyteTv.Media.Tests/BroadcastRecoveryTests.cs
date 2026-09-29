using NzyteTv.Core;

namespace NzyteTv.Media.Tests;

public sealed class BroadcastRecoveryTests
{
    [Theory]
    [InlineData("out_time_us=1234000", 1.234)]
    [InlineData("out_time=00:00:02.500000", 2.5)]
    public void ProgressParser_ReadsMachineReadableTimes(string line, double expectedSeconds)
    {
        Assert.True(FfmpegProgressParser.TryParseOutputTime(line, out TimeSpan value));
        Assert.Equal(expectedSeconds, value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("Connection reset by peer")]
    [InlineData("Error closing file: Broken pipe")]
    [InlineData("Connection timed out")]
    public void FailureClassifier_RecognizesTransientOutputFailures(string diagnostic)
    {
        Assert.Equal(BroadcastFailureKind.Transient, BroadcastFailureClassifier.Classify(152, diagnostic, false));
    }

    [Fact]
    public void FailureClassifier_NeverRetriesCancellation()
    {
        Assert.Equal(BroadcastFailureKind.Cancelled, BroadcastFailureClassifier.Classify(152, "Broken pipe", true));
    }

    [Fact]
    public void FailureClassifier_Exit255DependsOnParentCancellationState()
    {
        Assert.Equal(BroadcastFailureKind.Ambiguous, BroadcastFailureClassifier.Classify(255, string.Empty, false));
        Assert.Equal(BroadcastFailureKind.Cancelled, BroadcastFailureClassifier.Classify(255, string.Empty, true));
    }

    [Fact]
    public async Task RecoveryRunner_Exit255WithoutCancellationRestartsInterruptedItemOnce()
    {
        using var fixture = new RecoveryFixture();
        var process = new SequencedRunner(
            new Step(255, TimeSpan.FromSeconds(15), "Terminated"),
            new Step(0, null, null));
        var updates = new List<BroadcastRecoveryUpdate>();
        var recovery = CreateRecovery(process, maxRetries: 10);

        BroadcastRecoveryResult result = await recovery.RunAsync(
            fixture.Plan, Destination, null, updates.Add, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, process.InvocationCount);
        Assert.Contains(updates, update => update.Message == "Broadcast connection interrupted." && update.Attempt == 1);
        Assert.Contains(updates, update => update.Message == "Broadcast connection restored.");
        Assert.Contains(ConcatPath(fixture.Paths[0]), process.ConcatContents[0], StringComparison.Ordinal);
        Assert.DoesNotContain(ConcatPath(fixture.Paths[0]), process.ConcatContents[1], StringComparison.Ordinal);
        Assert.Contains(ConcatPath(fixture.Paths[1]), process.ConcatContents[1], StringComparison.Ordinal);
        Assert.Contains(ConcatPath(fixture.Paths[2]), process.ConcatContents[1], StringComparison.Ordinal);
        Assert.Equal(2, result.LastItem!.Sequence);
    }

    [Fact]
    public async Task RecoveryRunner_Exit255WithCancellationDoesNotRetry()
    {
        using var fixture = new RecoveryFixture();
        using var cancellation = new CancellationTokenSource();
        var process = new SequencedRunner(new Step(255, TimeSpan.FromSeconds(1), null, cancellation.Cancel));
        var recovery = CreateRecovery(process, maxRetries: 10);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.RunAsync(
            fixture.Plan, Destination, null, null, cancellation.Token));

        Assert.Equal(1, process.InvocationCount);
    }

    [Fact]
    public async Task RecoveryRunner_AmbiguousExitOneUsesBoundedRetryBudget()
    {
        using var fixture = new RecoveryFixture();
        var process = new SequencedRunner(new Step(1, null, "unexpected"), new Step(1, null, "unexpected"));
        var recovery = CreateRecovery(process, maxRetries: 1);

        BroadcastRecoveryResult result = await recovery.RunAsync(
            fixture.Plan, Destination, null, null, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(2, process.InvocationCount);
    }

    [Fact]
    public async Task RecoveryRunner_RedactsDestinationAcrossRetry()
    {
        using var fixture = new RecoveryFixture();
        var process = new SequencedRunner(
            new Step(255, TimeSpan.FromSeconds(1), $"output failed for {Destination}"),
            new Step(0, null, null));
        var output = new List<string>();

        await CreateRecovery(process, maxRetries: 1).RunAsync(
            fixture.Plan, Destination, output.Add, null, CancellationToken.None);

        string combined = string.Join(Environment.NewLine, output);
        Assert.DoesNotContain(Destination, combined, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoveryRunner_ObserverReceivesReplacementFfmpegPidAndRecoveryCount()
    {
        using var fixture = new RecoveryFixture();
        var process = new SequencedRunner(
            new Step(255, TimeSpan.FromSeconds(15), "Terminated"),
            new Step(0, TimeSpan.FromSeconds(1), null));
        var observer = new RecordingObserver();

        BroadcastRecoveryResult result = await CreateRecovery(process, maxRetries: 10).RunAsync(
            fixture.Plan,
            Destination,
            null,
            null,
            observer,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([4001, 4002], observer.Events
            .Where(value => value.Kind == BroadcastRuntimeEventKind.FfmpegProcessStarted)
            .Select(value => value.FfmpegPid));
        Assert.Contains(observer.Events, value =>
            value.Kind == BroadcastRuntimeEventKind.RecoveryStarted
            && value.RecoveryAttempts == 1
            && value.Item?.Sequence == 2
            && value.GlobalItemIndex == 1);
        Assert.Equal([0, 1, 2], observer.Events
            .Where(value => value.Kind == BroadcastRuntimeEventKind.ItemCompleted)
            .Select(value => value.GlobalItemIndex));
        Assert.Contains(observer.Events, value => value.Kind == BroadcastRuntimeEventKind.BroadcastCompleted);
    }

    [Fact]
    public async Task RecoveryRunner_ObserverReceivesEveryItemChangeWithoutParsingConsoleText()
    {
        using var fixture = new RecoveryFixture();
        var observer = new RecordingObserver();
        var recovery = new BroadcastRecoveryRunner(
            new FfmpegBroadcaster("ffmpeg", new MultiProgressRunner()),
            new BroadcastRecoveryPolicy(MaximumDelay: TimeSpan.Zero));

        BroadcastRecoveryResult result = await recovery.RunAsync(
            fixture.Plan,
            Destination,
            null,
            null,
            observer,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["asset-1", "asset-2", "asset-3"], observer.Events
            .Where(value => value.Kind == BroadcastRuntimeEventKind.ItemChanged)
            .Select(value => value.Item!.AssetId));
        Assert.Equal([0, 1, 2], observer.Events
            .Where(value => value.Kind == BroadcastRuntimeEventKind.ItemCompleted)
            .Select(value => value.GlobalItemIndex));
    }

    [Fact]
    public async Task RecoveryRunner_ColdResumeUsesOriginalGlobalIndexesAndOmitsEarlierItems()
    {
        using var fixture = new RecoveryFixture();
        var process = new SequencedRunner(new Step(0, null, null));
        var observer = new RecordingObserver();

        BroadcastRecoveryResult result = await CreateRecovery(process, maxRetries: 1).RunAsync(
            fixture.Plan,
            Destination,
            startItemIndex: 1,
            onFfmpegOutput: null,
            onUpdate: null,
            observer,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(ConcatPath(fixture.Paths[0]), process.ConcatContents[0], StringComparison.Ordinal);
        Assert.Contains(ConcatPath(fixture.Paths[1]), process.ConcatContents[0], StringComparison.Ordinal);
        Assert.Contains(ConcatPath(fixture.Paths[2]), process.ConcatContents[0], StringComparison.Ordinal);
        Assert.Equal(1, observer.Events.Single(value =>
            value.Kind == BroadcastRuntimeEventKind.BroadcastStarted).GlobalItemIndex);
        Assert.Equal([1, 2], observer.Events
            .Where(value => value.Kind == BroadcastRuntimeEventKind.ItemCompleted)
            .Select(value => value.GlobalItemIndex));
    }

    [Fact]
    public async Task RecoveryRunner_DuplicateProgressDoesNotCompleteAnItemTwice()
    {
        using var fixture = new RecoveryFixture();
        var observer = new RecordingObserver();
        var recovery = new BroadcastRecoveryRunner(
            new FfmpegBroadcaster("ffmpeg", new DuplicateProgressRunner()),
            new BroadcastRecoveryPolicy(MaximumDelay: TimeSpan.Zero));

        await recovery.RunAsync(
            fixture.Plan,
            Destination,
            null,
            null,
            observer,
            CancellationToken.None);

        Assert.Equal([0, 1, 2], observer.Events
            .Where(value => value.Kind == BroadcastRuntimeEventKind.ItemCompleted)
            .Select(value => value.GlobalItemIndex));
    }

    [Fact]
    public async Task RecoveryRunner_ProgressBeforeBoundaryBiasesTowardReplay()
    {
        using var fixture = new RecoveryFixture();
        var observer = new RecordingObserver();
        var process = new SequencedRunner(new Step(1, TimeSpan.FromSeconds(9.999), "unexpected"));

        BroadcastRecoveryResult result = await CreateRecovery(process, maxRetries: 0).RunAsync(
            fixture.Plan,
            Destination,
            null,
            null,
            observer,
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain(observer.Events, value => value.Kind == BroadcastRuntimeEventKind.ItemCompleted);
        Assert.Equal(0, observer.Events.Last(value =>
            value.Kind == BroadcastRuntimeEventKind.BroadcastFailed).GlobalItemIndex);
    }

    private const string Destination = "rtmps://example.invalid/live2/SECRET-KEY";

    private static BroadcastRecoveryRunner CreateRecovery(IProcessRunner process, int maxRetries) => new(
        new FfmpegBroadcaster("ffmpeg", process),
        new BroadcastRecoveryPolicy(maxRetries, MaximumDelay: TimeSpan.Zero));

    private static string ConcatPath(string path) => Path.GetFullPath(path).Replace('\\', '/');

    private sealed record Step(int ExitCode, TimeSpan? Progress, string? Diagnostic, Action? BeforeReturn = null);

    private sealed class SequencedRunner(params Step[] steps) : IProcessRunner
    {
        private readonly Queue<Step> _steps = new(steps);

        public int InvocationCount { get; private set; }

        public List<string> ConcatContents { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            int processId = 4000 + InvocationCount;
            request.OnStarted?.Invoke(processId);
            int inputIndex = request.Arguments.ToList().IndexOf("-i");
            ConcatContents.Add(File.ReadAllText(request.Arguments[inputIndex + 1]));
            Step step = _steps.Dequeue();
            if (step.Progress is not null)
            {
                request.OnStandardOutput?.Invoke($"out_time_us={(long)(step.Progress.Value.TotalMilliseconds * 1000)}");
            }
            if (step.Diagnostic is not null) request.OnStandardError?.Invoke(step.Diagnostic);
            step.BeforeReturn?.Invoke();
            request.OnExited?.Invoke(processId);
            return Task.FromResult(new ProcessResult(step.ExitCode, string.Empty, step.Diagnostic ?? string.Empty));
        }
    }

    private sealed class RecordingObserver : IBroadcastRuntimeObserver
    {
        public List<BroadcastRuntimeEvent> Events { get; } = [];

        public void OnEvent(BroadcastRuntimeEvent runtimeEvent) => Events.Add(runtimeEvent);
    }

    private sealed class MultiProgressRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            request.OnStarted?.Invoke(5001);
            request.OnStandardOutput?.Invoke("out_time_us=1000000");
            request.OnStandardOutput?.Invoke("out_time_us=11000000");
            request.OnStandardOutput?.Invoke("out_time_us=35000000");
            request.OnExited?.Invoke(5001);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class DuplicateProgressRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            request.OnStarted?.Invoke(5002);
            request.OnStandardOutput?.Invoke("out_time_us=11000000");
            request.OnStandardOutput?.Invoke("out_time_us=11000000");
            request.OnStandardOutput?.Invoke("out_time_us=35000000");
            request.OnStandardOutput?.Invoke("out_time_us=35000000");
            request.OnExited?.Invoke(5002);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class RecoveryFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("nzytetv-recovery-").FullName;

        public RecoveryFixture()
        {
            Paths = Enumerable.Range(1, 3).Select(index => Path.Combine(_root, $"item-{index}.mp4")).ToArray();
            foreach (string path in Paths) File.WriteAllText(path, "media");
            BroadcastPlanItem[] items =
            [
                new("playlist-01.json", 1, "asset-1", "item-1.mp4", Paths[0], 10, "One"),
                new("playlist-01.json", 2, "asset-2", "item-2.mp4", Paths[1], 20, "Two"),
                new("playlist-02.json", 1, "asset-3", "item-3.mp4", Paths[2], 30, "Three"),
            ];
            Plan = new BroadcastPlan(_root, ["playlist-01.json", "playlist-02.json"], items, [], 3, 60);
        }

        public string[] Paths { get; }

        public BroadcastPlan Plan { get; }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
