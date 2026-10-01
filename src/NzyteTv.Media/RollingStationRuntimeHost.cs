using System.Threading.Channels;
using NzyteTv.Core;

namespace NzyteTv.Media;

public interface IRollingReplenishmentTrigger
{
    void Signal();

    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class RollingReplenishmentTrigger : IRollingReplenishmentTrigger
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = false,
        SingleWriter = false,
    });

    public void Signal() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (_channel.Reader.TryRead(out _))
        {
            while (_channel.Reader.TryRead(out _))
            {
            }

            return;
        }

        using var raceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<bool> signal = _channel.Reader.WaitToReadAsync(raceCancellation.Token).AsTask();
        Task delay = Task.Delay(timeout, raceCancellation.Token);
        Task completed = await Task.WhenAny(signal, delay).ConfigureAwait(false);
        if (completed == signal)
        {
            _ = await signal.ConfigureAwait(false);
            raceCancellation.Cancel();
            await IgnoreCancellationAsync(delay).ConfigureAwait(false);
            while (_channel.Reader.TryRead(out _))
            {
            }
        }
        else
        {
            await delay.ConfigureAwait(false);
            raceCancellation.Cancel();
            await IgnoreCancellationAsync(signal).ConfigureAwait(false);
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public sealed class SignalingRollingStationStateStore(
    IRollingStationStateStore inner,
    IRollingReplenishmentTrigger trigger) : IRollingStationStateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task WriteAsync(
        string path,
        RollingStationRuntimeState state,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await inner.WriteAsync(path, state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        trigger.Signal();
    }

    public RollingStationRuntimeState Read(string path)
    {
        _gate.Wait();
        try
        {
            return inner.Read(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public RollingStationRuntimeState? ReadIfExists(string path)
    {
        _gate.Wait();
        try
        {
            return inner.ReadIfExists(path);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public interface IRollingCoordinatorOwnershipSignal
{
    Task WaitForOwnershipAsync(CancellationToken cancellationToken);
}

public sealed class RollingCoordinatorOwnershipSignal : IRollingCoordinatorOwnershipSignal
{
    private readonly TaskCompletionSource _owned = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public void ConfirmOwnership() => _owned.TrySetResult();

    public Task WaitForOwnershipAsync(CancellationToken cancellationToken) =>
        _owned.Task.WaitAsync(cancellationToken);
}

public sealed class SignalingRollingCoordinatorLockProvider(
    IRollingCoordinatorLockProvider inner,
    RollingCoordinatorOwnershipSignal ownership) : IRollingCoordinatorLockProvider
{
    public IRollingCoordinatorLock Acquire(string stationStatePath)
    {
        IRollingCoordinatorLock held = inner.Acquire(stationStatePath);
        ownership.ConfirmOwnership();
        return held;
    }
}

public interface IRollingStationRuntimeHost
{
    Task<RollingStationRunResult> RunAsync(
        string configurationPath,
        bool acceptStoppedStaticCutover,
        CancellationToken cancellationToken);
}

public sealed class RollingStationRuntimeHost : IRollingStationRuntimeHost
{
    private readonly IRollingStationCoordinator _coordinator;
    private readonly IRollingProgrammingReplenisher _replenisher;
    private readonly IRollingCoordinatorOwnershipSignal _ownership;
    private readonly Action<string>? _diagnostic;

    public RollingStationRuntimeHost(
        IRollingStationCoordinator coordinator,
        IRollingProgrammingReplenisher replenisher,
        IRollingCoordinatorOwnershipSignal ownership,
        Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(replenisher);
        ArgumentNullException.ThrowIfNull(ownership);
        _coordinator = coordinator;
        _replenisher = replenisher;
        _ownership = ownership;
        _diagnostic = diagnostic;
    }

    public async Task<RollingStationRunResult> RunAsync(
        string configurationPath,
        bool acceptStoppedStaticCutover,
        CancellationToken cancellationToken)
    {
        using var backgroundCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task<RollingStationRunResult> coordinatorTask = _coordinator.RunAsync(
            configurationPath,
            acceptStoppedStaticCutover,
            cancellationToken);
        Task ownershipTask = _ownership.WaitForOwnershipAsync(CancellationToken.None);
        Task first = await Task.WhenAny(coordinatorTask, ownershipTask).ConfigureAwait(false);
        if (first == coordinatorTask || coordinatorTask.IsCompleted)
        {
            return await coordinatorTask.ConfigureAwait(false);
        }

        await ownershipTask.ConfigureAwait(false);
        Task replenishmentTask = ObserveReplenisherAsync(
            () => _replenisher.RunAsync(configurationPath, backgroundCancellation.Token));
        try
        {
            return await coordinatorTask.ConfigureAwait(false);
        }
        finally
        {
            backgroundCancellation.Cancel();
            await replenishmentTask.ConfigureAwait(false);
        }
    }

    private async Task ObserveReplenisherAsync(Func<Task> run)
    {
        try
        {
            await run().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal host shutdown cancels and observes the background task.
        }
        catch (Exception exception)
        {
            _diagnostic?.Invoke(
                StationSecretRedactor.RedactRtmpUrls(exception.Message)
                    ?? "Rolling replenishment background task failed.");
        }
    }
}
