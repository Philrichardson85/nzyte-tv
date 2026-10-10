using System.Threading.Channels;
using NzyteTv.Media;
using NzyteTv.Operations.Configuration;
using NzyteTv.Operations.Contracts;

namespace NzyteTv.Operations.MediaLibrary;

public interface IMediaLibraryOperationCoordinator
{
    Task<MediaLibrarySummaryResponse> GetSummaryAsync(CancellationToken cancellationToken);
    Task<MediaLibraryRefreshAcceptedResponse> StartRefreshAsync(
        MediaLibraryRefreshRequest request, CancellationToken cancellationToken);
    Task<MediaLibraryOperationResponse> GetOperationAsync(
        string operationId, CancellationToken cancellationToken);
}

public sealed class MediaLibraryOperationException(string code)
    : InvalidOperationException("The media-library operation could not be completed.")
{
    public string Code { get; } = code;
}

public interface IMediaLibraryRefreshRunner
{
    Task<MediaLibraryRefreshResult> RunAsync(
        MediaLibraryRefreshOptions options,
        ExternalAssetMetadataGenerationStore store,
        IMediaRefreshOperationStore operations,
        IMediaMetadataRefreshLease lease,
        string operationId,
        CancellationToken cancellationToken);
}

public sealed class MediaLibraryRefreshRunner : IMediaLibraryRefreshRunner
{
    private readonly IMediaMetadataRefreshLock _refreshLock;
    private readonly TimeProvider _timeProvider;

    public MediaLibraryRefreshRunner(IMediaMetadataRefreshLock refreshLock, TimeProvider timeProvider)
    {
        _refreshLock = refreshLock;
        _timeProvider = timeProvider;
    }

    public Task<MediaLibraryRefreshResult> RunAsync(
        MediaLibraryRefreshOptions options,
        ExternalAssetMetadataGenerationStore store,
        IMediaRefreshOperationStore operations,
        IMediaMetadataRefreshLease lease,
        string operationId,
        CancellationToken cancellationToken) => new MediaLibraryRefreshService(
            options,
            generationStore: store,
            refreshLock: _refreshLock,
            operationStore: operations,
            timeProvider: _timeProvider,
            operationIdFactory: () => operationId)
        .RefreshWithLeaseAsync(lease, cancellationToken);
}

public sealed class MediaLibraryOperationCoordinator : BackgroundService, IMediaLibraryOperationCoordinator
{
    private readonly OperationsOptions _options;
    private readonly IMediaMetadataRefreshLock _refreshLock;
    private readonly TimeProvider _timeProvider;
    private readonly IMediaLibraryRefreshRunner _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<WorkItem> _work = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });
    private string? _activeOperationId;
    private DateTimeOffset? _activeStartedAt;
    private long? _activeRevision;
    private string? _activeGenerationId;
    private int _accepting;

    public MediaLibraryOperationCoordinator(
        OperationsOptions options,
        IMediaMetadataRefreshLock refreshLock,
        TimeProvider timeProvider,
        IMediaLibraryRefreshRunner? runner = null)
    {
        _options = options;
        _refreshLock = refreshLock;
        _timeProvider = timeProvider;
        _runner = runner ?? new MediaLibraryRefreshRunner(refreshLock, timeProvider);
    }

    public Task<MediaLibrarySummaryResponse> GetSummaryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.MediaLibrary.Enabled)
        {
            return Task.FromResult(new MediaLibrarySummaryResponse(
                OperationsProtocol.SchemaVersion, MediaLibraryFeatureState.Disabled,
                null, null, null, null));
        }

        try
        {
            string? localActive = _activeOperationId;
            if (localActive is not null)
            {
                return Task.FromResult(new MediaLibrarySummaryResponse(
                    OperationsProtocol.SchemaVersion,
                    MediaLibraryFeatureState.Busy,
                    _activeRevision,
                    _activeGenerationId,
                    localActive,
                    null));
            }
            Runtime runtime = CreateRuntime();
            AssetMetadataGenerationPointer current = runtime.Store.ReadCurrent();
            IReadOnlyList<MediaLibraryRefreshResult> operations = runtime.Operations.LoadAll();
            MediaLibraryRefreshResult? last = operations.MaxBy(value => value.StartedAt);
            string? active = _activeOperationId ?? operations
                .Where(value => value.Status == MediaLibraryRefreshStatus.Running)
                .MaxBy(value => value.StartedAt)?.OperationId;
            return Task.FromResult(new MediaLibrarySummaryResponse(
                OperationsProtocol.SchemaVersion,
                active is null ? MediaLibraryFeatureState.Ready : MediaLibraryFeatureState.Busy,
                current.Revision, current.GenerationId, active,
                last is null ? null : Map(last)));
        }
        catch (Exception exception) when (IsConfigurationOrStorageFailure(exception))
        {
            return Task.FromResult(new MediaLibrarySummaryResponse(
                OperationsProtocol.SchemaVersion, MediaLibraryFeatureState.Unavailable,
                null, null, null, null));
        }
    }

    public async Task<MediaLibraryRefreshAcceptedResponse> StartRefreshAsync(
        MediaLibraryRefreshRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != OperationsProtocol.SchemaVersion
            || request.ExpectedMetadataRevision <= 0)
        {
            throw new MediaLibraryOperationException(OperationsErrorCodes.ValidationFailed);
        }
        if (!_options.MediaLibrary.Enabled)
        {
            throw new MediaLibraryOperationException(OperationsErrorCodes.FeatureDisabled);
        }
        if (Volatile.Read(ref _accepting) == 0)
        {
            throw new MediaLibraryOperationException(OperationsErrorCodes.FeatureUnavailable);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeOperationId is not null)
            {
                throw new MediaLibraryOperationException(OperationsErrorCodes.RefreshBusy);
            }

            Runtime runtime;
            try { runtime = CreateRuntime(); }
            catch (Exception exception) when (IsConfigurationOrStorageFailure(exception))
            {
                throw new MediaLibraryOperationException(OperationsErrorCodes.FeatureUnavailable);
            }

            IMediaMetadataRefreshLease lease;
            try
            {
                lease = await _refreshLock.TryAcquireAsync(
                    runtime.Options.ExternalMetadataRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (MediaMetadataRefreshBusyException)
            {
                throw new MediaLibraryOperationException(OperationsErrorCodes.RefreshBusy);
            }

            AssetMetadataGenerationPointer current;
            try { current = runtime.Store.ReadCurrent(); }
            catch
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            if (current.Revision != request.ExpectedMetadataRevision)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw new MediaLibraryOperationException(OperationsErrorCodes.StaleRevision);
            }

            string operationId = Guid.NewGuid().ToString("N");
            _activeOperationId = operationId;
            _activeStartedAt = _timeProvider.GetUtcNow();
            _activeRevision = current.Revision;
            _activeGenerationId = current.GenerationId;
            if (!_work.Writer.TryWrite(new WorkItem(operationId, runtime, lease)))
            {
                _activeOperationId = null;
                _activeStartedAt = null;
                _activeRevision = null;
                _activeGenerationId = null;
                await lease.DisposeAsync().ConfigureAwait(false);
                throw new MediaLibraryOperationException(OperationsErrorCodes.RefreshBusy);
            }
            return new MediaLibraryRefreshAcceptedResponse(OperationsProtocol.SchemaVersion, operationId);
        }
        catch (MediaLibraryOperationException) { throw; }
        catch (Exception exception) when (IsConfigurationOrStorageFailure(exception))
        {
            throw new MediaLibraryOperationException(OperationsErrorCodes.FeatureUnavailable);
        }
        finally { _gate.Release(); }
    }

    public Task<MediaLibraryOperationResponse> GetOperationAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReadyMediaPackageValidator.IsPackageId(operationId))
        {
            throw new MediaLibraryOperationException(OperationsErrorCodes.InvalidOperationId);
        }
        if (_activeOperationId == operationId)
        {
            return Task.FromResult(Starting(operationId));
        }
        if (!_options.MediaLibrary.Enabled)
        {
            throw new MediaLibraryOperationException(OperationsErrorCodes.FeatureDisabled);
        }
        try
        {
            MediaLibraryRefreshResult? result = CreateRuntime().Operations.LoadAll()
                .SingleOrDefault(value => value.OperationId == operationId);
            return Task.FromResult(result is null
                ? throw new MediaLibraryOperationException(OperationsErrorCodes.OperationNotFound)
                : Map(result));
        }
        catch (MediaLibraryOperationException) { throw; }
        catch (Exception exception) when (IsConfigurationOrStorageFailure(exception))
        {
            throw new MediaLibraryOperationException(OperationsErrorCodes.FeatureUnavailable);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ReconcileOnStartupAsync(stoppingToken).ConfigureAwait(false);
            Volatile.Write(ref _accepting, 1);
            _ready.TrySetResult();
            await foreach (WorkItem item in _work.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    _ = await _runner.RunAsync(
                        item.Runtime.Options,
                        item.Runtime.Store,
                        item.Runtime.Operations,
                        item.Lease,
                        item.OperationId,
                        stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch { await MarkUnexpectedFailureAsync(item).ConfigureAwait(false); }
                finally
                {
                    await item.Lease.DisposeAsync().ConfigureAwait(false);
                    await ClearActiveAsync(item.OperationId).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _ready.TrySetException(new InvalidOperationException(
                "The media-library coordinator could not start safely.", exception));
            throw;
        }
        finally
        {
            if (!_ready.Task.IsCompleted) _ready.TrySetCanceled(stoppingToken);
            Volatile.Write(ref _accepting, 0);
            while (_work.Reader.TryRead(out WorkItem? pending))
            {
                await pending.Lease.DisposeAsync().ConfigureAwait(false);
                await ClearActiveAsync(pending.OperationId).ConfigureAwait(false);
            }
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _accepting, 0);
        _work.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearActiveAsync(string operationId)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_activeOperationId == operationId)
            {
                _activeOperationId = null;
                _activeStartedAt = null;
                _activeRevision = null;
                _activeGenerationId = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReconcileOnStartupAsync(CancellationToken cancellationToken)
    {
        if (!_options.MediaLibrary.Enabled) return;
        try
        {
            Runtime runtime = CreateRuntime();
            await runtime.Operations.ReconcileRunningAsync(
                runtime.Store.ReadCurrent(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConfigurationOrStorageFailure(exception)) { }
    }

    private async Task MarkUnexpectedFailureAsync(WorkItem item)
    {
        try
        {
            MediaLibraryRefreshResult? running = item.Runtime.Operations.LoadAll()
                .SingleOrDefault(value => value.OperationId == item.OperationId);
            if (running?.Status != MediaLibraryRefreshStatus.Running) return;
            await item.Runtime.Operations.WriteAsync(running with
            {
                CompletedAt = _timeProvider.GetUtcNow(),
                Status = MediaLibraryRefreshStatus.Failed,
                ErrorCount = Math.Max(1, running.ErrorCount),
                Issues = [new MediaLibraryRefreshIssue(MediaLibraryRefreshIssueCode.RefreshFailed)],
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    private Runtime CreateRuntime()
    {
        MediaLibraryOperationsOptions configured = _options.MediaLibrary;
        if (!configured.HasCompleteConfiguration)
        {
            throw new InvalidOperationException("Media-library configuration is incomplete.");
        }
        var refreshOptions = new MediaLibraryRefreshOptions(
            configured.MediaRoot!, configured.MetadataRoot!, configured.InboxRoot).Validate();
        var store = new ExternalAssetMetadataGenerationStore(refreshOptions.ExternalMetadataRoot);
        var operations = new MediaRefreshOperationStore(
            refreshOptions.ExternalMetadataRoot, timeProvider: _timeProvider);
        return new Runtime(refreshOptions, store, operations);
    }

    private MediaLibraryOperationResponse Starting(string operationId) => new(
        OperationsProtocol.SchemaVersion, operationId, MediaLibraryOperationState.Starting,
        _activeStartedAt ?? _timeProvider.GetUtcNow(), null, 0, 0, null, null,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);

    private static MediaLibraryOperationResponse Map(MediaLibraryRefreshResult result) => new(
        OperationsProtocol.SchemaVersion, result.OperationId!,
        result.Status switch
        {
            MediaLibraryRefreshStatus.Running => MediaLibraryOperationState.Running,
            MediaLibraryRefreshStatus.Succeeded => MediaLibraryOperationState.Succeeded,
            MediaLibraryRefreshStatus.SucceededWithWarnings => MediaLibraryOperationState.SucceededWithWarnings,
            MediaLibraryRefreshStatus.NoChanges => MediaLibraryOperationState.NoChanges,
            MediaLibraryRefreshStatus.Failed => MediaLibraryOperationState.Failed,
            MediaLibraryRefreshStatus.Interrupted => MediaLibraryOperationState.Interrupted,
            _ => throw new InvalidDataException("The media operation status is unsupported."),
        },
        result.StartedAt, result.CompletedAt,
        result.MetadataRevisionBefore, result.MetadataRevisionAfter,
        result.GenerationIdBefore, result.GenerationIdAfter,
        result.PackagesObserved, result.PackagesAccepted,
        result.PackagesAlreadyProcessed, result.PackagesRejected,
        result.NewSourceAssets, result.NewLibraryAssets,
        result.MetadataRecordsCreated, result.MetadataRecordsPreserved,
        result.AssetsNewlyEligible, result.UnresolvedAssets,
        result.IncompletePackages, result.SkippedPackages,
        result.WarningCount, result.ErrorCount,
        result.Issues!.Select(issue => new MediaLibraryIssueResponse(
            LowerCamel(issue.Code.ToString()), issue.PackageId, issue.RelativeIdentity)).ToArray());

    private static string LowerCamel(string value) => char.ToLowerInvariant(value[0]) + value[1..];
    private static bool IsConfigurationOrStorageFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException;

    private sealed record Runtime(
        MediaLibraryRefreshOptions Options,
        ExternalAssetMetadataGenerationStore Store,
        MediaRefreshOperationStore Operations);
    private sealed record WorkItem(
        string OperationId, Runtime Runtime, IMediaMetadataRefreshLease Lease);
}
