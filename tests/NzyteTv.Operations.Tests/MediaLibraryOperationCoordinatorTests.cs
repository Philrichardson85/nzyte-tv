using System.Text.Json;
using NzyteTv.Core;
using NzyteTv.Media;
using NzyteTv.Operations.Configuration;
using NzyteTv.Operations.Contracts;
using NzyteTv.Operations.MediaLibrary;

namespace NzyteTv.Operations.Tests;

public sealed class MediaLibraryOperationCoordinatorTests
{
    [Fact]
    public async Task DisabledFeatureStartsNormallyAndReportsDisabled()
    {
        using var coordinator = new MediaLibraryOperationCoordinator(
            new OperationsOptions(), new MediaMetadataRefreshLock(), TimeProvider.System);
        MediaLibrarySummaryResponse summary = await coordinator.GetSummaryAsync(CancellationToken.None);
        Assert.Equal(MediaLibraryFeatureState.Disabled, summary.FeatureState);
        await Assert.ThrowsAsync<MediaLibraryOperationException>(() => coordinator.StartRefreshAsync(
            Request(1), CancellationToken.None));
    }

    [Fact]
    public async Task InvalidEnabledConfigurationFailsOnlyMediaFeatureClosed()
    {
        var options = new OperationsOptions { MediaLibrary = new() { Enabled = true } };
        using var coordinator = new MediaLibraryOperationCoordinator(
            options, new MediaMetadataRefreshLock(), TimeProvider.System);
        MediaLibrarySummaryResponse summary = await coordinator.GetSummaryAsync(CancellationToken.None);
        Assert.Equal(MediaLibraryFeatureState.Unavailable, summary.FeatureState);
    }

    [Fact]
    public async Task MetadataRootOverlappingReadOnlyMediaFailsClosed()
    {
        string root = Directory.CreateTempSubdirectory("nzytetv-b2c-overlap-").FullName;
        try
        {
            string mediaRoot = Path.Combine(root, "media");
            Directory.CreateDirectory(Path.Combine(mediaRoot, "source"));
            Directory.CreateDirectory(Path.Combine(mediaRoot, "library"));
            Directory.CreateDirectory(Path.Combine(mediaRoot, "inbox"));
            string metadataRoot = Path.Combine(mediaRoot, "metadata");
            Directory.CreateDirectory(metadataRoot);
            var options = new OperationsOptions
            {
                MediaLibrary = new MediaLibraryOperationsOptions
                {
                    Enabled = true,
                    MediaRoot = mediaRoot,
                    MetadataRoot = metadataRoot,
                    InboxRoot = Path.Combine(mediaRoot, "inbox"),
                },
            };
            using var coordinator = new MediaLibraryOperationCoordinator(
                options, new MediaMetadataRefreshLock(), TimeProvider.System);

            Assert.Equal(MediaLibraryFeatureState.Unavailable,
                (await coordinator.GetSummaryAsync(CancellationToken.None)).FeatureState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StaleRevisionIsRejectedBeforeBackgroundOperationStarts()
    {
        using var fixture = await Fixture.CreateAsync();
        using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);
        MediaLibraryOperationException exception = await Assert.ThrowsAsync<MediaLibraryOperationException>(
            () => coordinator.StartRefreshAsync(Request(2), CancellationToken.None));
        Assert.Equal(OperationsErrorCodes.StaleRevision, exception.Code);
        Assert.Empty(new MediaRefreshOperationStore(fixture.MetadataRoot).LoadAll());
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExistingCrossProcessLeaseReturnsBusyRatherThanFalseAcceptance()
    {
        using var fixture = await Fixture.CreateAsync();
        var refreshLock = new MediaMetadataRefreshLock();
        await using IMediaMetadataRefreshLease held = await refreshLock.TryAcquireAsync(
            fixture.MetadataRoot, CancellationToken.None);
        using var coordinator = fixture.CreateCoordinator(refreshLock);
        await coordinator.StartAsync(CancellationToken.None);

        MediaLibraryOperationException exception = await Assert.ThrowsAsync<MediaLibraryOperationException>(
            () => coordinator.StartRefreshAsync(Request(1), CancellationToken.None));

        Assert.Equal(OperationsErrorCodes.RefreshBusy, exception.Code);
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AcceptedNoChangesOperationRunsIndependentlyOfRequestCancellation()
    {
        using var fixture = await Fixture.CreateAsync();
        using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);
        using var requestCancellation = new CancellationTokenSource();

        MediaLibraryRefreshAcceptedResponse accepted = await coordinator.StartRefreshAsync(
            Request(1), requestCancellation.Token);
        requestCancellation.Cancel();
        MediaLibraryOperationResponse completed = await WaitForTerminalAsync(coordinator, accepted.OperationId);

        Assert.Equal(MediaLibraryOperationState.NoChanges, completed.Status);
        Assert.Equal(1, completed.MetadataRevisionBefore);
        Assert.Equal(1, completed.MetadataRevisionAfter);
        Assert.Equal(1, fixture.Store.ReadCurrent().Revision);
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RequestCancellationAfterAcceptanceDoesNotCancelHostOwnedWork()
    {
        using var fixture = await Fixture.CreateAsync();
        var runner = new ControlledRunner();
        using var coordinator = fixture.CreateCoordinator(runner: runner);
        await coordinator.StartAsync(CancellationToken.None);
        using var requestCancellation = new CancellationTokenSource();

        MediaLibraryRefreshAcceptedResponse accepted = await coordinator.StartRefreshAsync(
            Request(1), requestCancellation.Token);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        requestCancellation.Cancel();

        Assert.False(runner.RunCancellation.IsCancellationRequested);
        runner.Complete.TrySetResult();
        MediaLibraryOperationResponse operation = await WaitForTerminalAsync(coordinator, accepted.OperationId);
        Assert.Null(runner.Failure);
        Assert.Equal(MediaLibraryOperationState.NoChanges, operation.Status);
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostShutdownCancelsWorkAndPersistsInterruptedState()
    {
        using var fixture = await Fixture.CreateAsync();
        var runner = new ControlledRunner();
        using var coordinator = fixture.CreateCoordinator(runner: runner);
        await coordinator.StartAsync(CancellationToken.None);
        MediaLibraryRefreshAcceptedResponse accepted = await coordinator.StartRefreshAsync(
            Request(1), CancellationToken.None);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.StopAsync(CancellationToken.None);

        MediaLibraryOperationResponse operation = await coordinator.GetOperationAsync(
            accepted.OperationId, CancellationToken.None);
        Assert.Equal(MediaLibraryOperationState.Interrupted, operation.Status);
        Assert.Equal(1, fixture.Store.ReadCurrent().Revision);
    }

    [Fact]
    public async Task BackgroundExceptionIsObservedAndReducedToFixedFailure()
    {
        using var fixture = await Fixture.CreateAsync();
        var runner = new ControlledRunner { ThrowUnexpected = true };
        using var coordinator = fixture.CreateCoordinator(runner: runner);
        await coordinator.StartAsync(CancellationToken.None);
        MediaLibraryRefreshAcceptedResponse accepted = await coordinator.StartRefreshAsync(
            Request(1), CancellationToken.None);

        MediaLibraryOperationResponse operation = await WaitForTerminalAsync(coordinator, accepted.OperationId);

        Assert.Equal(MediaLibraryOperationState.Failed, operation.Status);
        Assert.Equal("refreshFailed", Assert.Single(operation.Issues).Code);
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SimultaneousSameRevisionRequestsAcceptExactlyOne()
    {
        using var fixture = await Fixture.CreateAsync();
        using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);
        Task<(bool Accepted, string? Code)> Invoke() => TryStartAsync(coordinator);

        (bool Accepted, string? Code)[] results = await Task.WhenAll(Invoke(), Invoke());

        Assert.Single(results, result => result.Accepted);
        Assert.Single(results, result => result.Code == OperationsErrorCodes.RefreshBusy);
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostShutdownCancelsRunningRecordAndRestartDoesNotAutoRetry()
    {
        using var fixture = await Fixture.CreateAsync();
        string operationId = Guid.NewGuid().ToString("N");
        var store = new MediaRefreshOperationStore(fixture.MetadataRoot);
        await store.WriteAsync(new MediaLibraryRefreshResult
        {
            OperationId = operationId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Status = MediaLibraryRefreshStatus.Running,
            MetadataRevisionBefore = 1,
            MetadataRevisionAfter = 1,
            GenerationIdBefore = "000000000001",
            GenerationIdAfter = "000000000001",
        }, CancellationToken.None);

        using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);
        MediaLibraryOperationResponse operation = await WaitForTerminalAsync(coordinator, operationId);
        Assert.Equal(MediaLibraryOperationState.SucceededWithWarnings, operation.Status);
        Assert.Contains(operation.Issues, issue => issue.Code == "operationFinalizationInterrupted");
        await coordinator.StopAsync(CancellationToken.None);
        Assert.Single(store.LoadAll());
    }

    private static async Task<(bool Accepted, string? Code)> TryStartAsync(
        MediaLibraryOperationCoordinator coordinator)
    {
        try
        {
            _ = await coordinator.StartRefreshAsync(Request(1), CancellationToken.None);
            return (true, null);
        }
        catch (MediaLibraryOperationException exception)
        {
            return (false, exception.Code);
        }
    }

    private static async Task<MediaLibraryOperationResponse> WaitForTerminalAsync(
        MediaLibraryOperationCoordinator coordinator, string operationId)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            MediaLibraryOperationResponse value = await coordinator.GetOperationAsync(
                operationId, CancellationToken.None);
            if (value.Status is not MediaLibraryOperationState.Starting and not MediaLibraryOperationState.Running)
            {
                return value;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("The test media operation did not complete.");
    }

    private static MediaLibraryRefreshRequest Request(long revision) => new()
    {
        SchemaVersion = OperationsProtocol.SchemaVersion,
        ExpectedMetadataRevision = revision,
    };

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root)
        {
            Root = root;
            MediaRoot = Path.Combine(root, "media");
            MetadataRoot = Path.Combine(root, "metadata");
            InboxRoot = Path.Combine(MediaRoot, "inbox");
            Store = new ExternalAssetMetadataGenerationStore(MetadataRoot);
        }

        public string Root { get; }
        public string MediaRoot { get; }
        public string MetadataRoot { get; }
        public string InboxRoot { get; }
        public ExternalAssetMetadataGenerationStore Store { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(Directory.CreateTempSubdirectory("nzytetv-b2c-").FullName);
            Directory.CreateDirectory(Path.Combine(fixture.MediaRoot, "source"));
            Directory.CreateDirectory(Path.Combine(fixture.MediaRoot, "library"));
            Directory.CreateDirectory(fixture.InboxRoot);
            Directory.CreateDirectory(Path.Combine(fixture.MediaRoot, "catalog"));
            Directory.CreateDirectory(fixture.MetadataRoot);
            var catalog = new SongCatalog
            {
                SchemaVersion = SongCatalog.CurrentSchemaVersion,
                Songs = [],
            };
            File.WriteAllText(
                Path.Combine(fixture.MediaRoot, "catalog", "song-catalog.json"),
                JsonSerializer.Serialize(catalog, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                }));
            var inventory = new AssetMetadataGenerationInventory
            {
                Revision = 1,
                GenerationId = "000000000001",
                Packages = [],
                Assets = [],
            };
            await fixture.Store.CreateGenerationAsync(
                "000000000001", 1, [], inventory, CancellationToken.None);
            await fixture.Store.BootstrapCurrentAsync("000000000001", CancellationToken.None);
            return fixture;
        }

        public MediaLibraryOperationCoordinator CreateCoordinator(
            IMediaMetadataRefreshLock? refreshLock = null,
            IMediaLibraryRefreshRunner? runner = null)
        {
            IMediaMetadataRefreshLock actualLock = refreshLock ?? new MediaMetadataRefreshLock();
            return new(
                new OperationsOptions
                {
                    MediaLibrary = new MediaLibraryOperationsOptions
                    {
                        Enabled = true,
                        MediaRoot = MediaRoot,
                        MetadataRoot = MetadataRoot,
                        InboxRoot = InboxRoot,
                    },
                },
                actualLock,
                TimeProvider.System,
                runner);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class ControlledRunner : IMediaLibraryRefreshRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken RunCancellation { get; private set; }
        public bool ThrowUnexpected { get; init; }
        public Exception? Failure { get; private set; }

        public async Task<MediaLibraryRefreshResult> RunAsync(
            MediaLibraryRefreshOptions options,
            ExternalAssetMetadataGenerationStore store,
            IMediaRefreshOperationStore operations,
            IMediaMetadataRefreshLease lease,
            string operationId,
            CancellationToken cancellationToken)
        {
            try
            {
                return await RunCoreAsync(
                    options, store, operations, lease, operationId, cancellationToken);
            }
            catch (Exception exception)
            {
                Failure = exception;
                throw;
            }
        }

        private async Task<MediaLibraryRefreshResult> RunCoreAsync(
            MediaLibraryRefreshOptions options,
            ExternalAssetMetadataGenerationStore store,
            IMediaRefreshOperationStore operations,
            IMediaMetadataRefreshLease lease,
            string operationId,
            CancellationToken cancellationToken)
        {
            lease.Consume(options.ExternalMetadataRoot);
            RunCancellation = cancellationToken;
            AssetMetadataGenerationPointer current = store.ReadCurrent();
            var running = new MediaLibraryRefreshResult
            {
                OperationId = operationId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = MediaLibraryRefreshStatus.Running,
                MetadataRevisionBefore = current.Revision,
                MetadataRevisionAfter = current.Revision,
                GenerationIdBefore = current.GenerationId,
                GenerationIdAfter = current.GenerationId,
            };
            await operations.WriteAsync(running, cancellationToken);
            Started.TrySetResult();
            if (ThrowUnexpected) throw new Exception("C:\\secret\\media stack trace");
            try
            {
                await Complete.Task.WaitAsync(cancellationToken);
                MediaLibraryRefreshResult completed = running with
                {
                    CompletedAt = DateTimeOffset.UtcNow,
                    Status = MediaLibraryRefreshStatus.NoChanges,
                };
                await operations.WriteAsync(completed, CancellationToken.None);
                return completed;
            }
            catch (OperationCanceledException)
            {
                await operations.WriteAsync(running with
                {
                    CompletedAt = DateTimeOffset.UtcNow,
                    Status = MediaLibraryRefreshStatus.Interrupted,
                    ErrorCount = 1,
                }, CancellationToken.None);
                throw;
            }
        }
    }
}
