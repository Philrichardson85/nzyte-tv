using NzyteTv.Dashboard.Configuration;

namespace NzyteTv.Dashboard.Status;

internal sealed class DashboardStatusCache : BackgroundService, IDashboardStatusProvider
{
    private readonly IDashboardStatusSnapshotSource _source;
    private readonly TimeProvider _timeProvider;
    private readonly DashboardRuntimeInfo _runtime;
    private readonly TimeSpan _refreshInterval;
    private readonly ILogger<DashboardStatusCache> _logger;
    private DashboardStatusSnapshot _snapshot;

    public DashboardStatusCache(
        IDashboardStatusSnapshotSource source,
        DashboardOptions options,
        TimeProvider timeProvider,
        DashboardRuntimeInfo runtime,
        ILogger<DashboardStatusCache> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _timeProvider = timeProvider;
        _runtime = runtime;
        _refreshInterval = TimeSpan.FromSeconds(options.StatusRefreshSeconds);
        _logger = logger;
        _snapshot = DashboardStatusSnapshot.CreateUnavailable(
            _timeProvider.GetUtcNow(),
            _runtime,
            DashboardIssueCode.SnapshotRefreshFailed);
    }

    public DashboardStatusSnapshot GetStatus() => Volatile.Read(ref _snapshot);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Refresh();
        using var timer = new PeriodicTimer(_refreshInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Refresh();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal void Refresh()
    {
        try
        {
            Volatile.Write(ref _snapshot, _source.ReadStatus());
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            _logger.LogWarning("Dashboard status refresh failed; serving a safe unavailable snapshot.");
            Volatile.Write(
                ref _snapshot,
                DashboardStatusSnapshot.CreateUnavailable(
                    _timeProvider.GetUtcNow(),
                    _runtime,
                    DashboardIssueCode.SnapshotRefreshFailed));
        }
    }
}
