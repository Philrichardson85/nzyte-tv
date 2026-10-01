using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class RollingStationRuntimeHostTests
{
    [Fact]
    public async Task ReplenishmentStartsOnlyAfterCoordinatorOwnershipAndIsAwaitedOnExit()
    {
        var ownership = new RollingCoordinatorOwnershipSignal();
        var coordinatorCompletion = new TaskCompletionSource<RollingStationRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new FakeCoordinator(async cancellationToken =>
        {
            await Task.Yield();
            ownership.ConfirmOwnership();
            return await coordinatorCompletion.Task.WaitAsync(cancellationToken);
        });
        var replenisher = new WaitingReplenisher();
        var host = new RollingStationRuntimeHost(coordinator, replenisher, ownership);
        Task<RollingStationRunResult> run = host.RunAsync("config.json", false, CancellationToken.None);

        await replenisher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var expected = new RollingStationRunResult(1, null, "runtime failure");
        coordinatorCompletion.SetResult(expected);
        RollingStationRunResult actual = await run;

        Assert.Equal(expected, actual);
        Assert.True(replenisher.CancellationObserved);
        Assert.True(replenisher.Completed);
    }

    [Fact]
    public async Task CompetingCoordinatorThatNeverOwnsLock_DoesNotStartReplenishment()
    {
        var ownership = new RollingCoordinatorOwnershipSignal();
        var coordinator = new FakeCoordinator(_ => Task.FromResult(
            new RollingStationRunResult(StationExitCodes.PermanentStartupFailure, null, "busy")));
        var replenisher = new CountingReplenisher();
        var host = new RollingStationRuntimeHost(coordinator, replenisher, ownership);

        RollingStationRunResult result = await host.RunAsync(
            "config.json",
            false,
            CancellationToken.None);

        Assert.Equal(StationExitCodes.PermanentStartupFailure, result.ExitCode);
        Assert.Equal(0, replenisher.Calls);
    }

    [Fact]
    public async Task UnexpectedBackgroundException_IsObservedWithoutChangingCoordinatorResult()
    {
        var ownership = new RollingCoordinatorOwnershipSignal();
        var coordinatorCompletion = new TaskCompletionSource<RollingStationRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new FakeCoordinator(async cancellationToken =>
        {
            ownership.ConfirmOwnership();
            return await coordinatorCompletion.Task.WaitAsync(cancellationToken);
        });
        var replenisher = new ThrowingReplenisher();
        var diagnostics = new List<string>();
        var host = new RollingStationRuntimeHost(
            coordinator,
            replenisher,
            ownership,
            diagnostics.Add);
        Task<RollingStationRunResult> run = host.RunAsync("config.json", false, CancellationToken.None);
        await replenisher.Thrown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var expected = new RollingStationRunResult(0, null);
        coordinatorCompletion.SetResult(expected);

        RollingStationRunResult result = await run;

        Assert.Equal(expected, result);
        Assert.Contains(diagnostics, value =>
            value.Contains("simulated background failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignalingStateStore_WakesOnlyAfterSuccessfulDurableWrite()
    {
        var trigger = new CountingTrigger();
        var inner = new RecordingStateStore();
        var store = new SignalingRollingStationStateStore(inner, trigger);
        RollingStationRuntimeState state = CreateState();

        await store.WriteAsync("state.json", state, CancellationToken.None);

        Assert.Equal(1, trigger.Signals);
        Assert.Equal(state, inner.State);

        inner.WriteFailure = new IOException("failed");
        await Assert.ThrowsAsync<IOException>(() =>
            store.WriteAsync("state.json", state, CancellationToken.None));
        Assert.Equal(1, trigger.Signals);
    }

    [Fact]
    public void SignalingLockProvider_ConfirmsOnlySuccessfulOwnership()
    {
        var signal = new RollingCoordinatorOwnershipSignal();
        var inner = new FakeLockProvider();
        var provider = new SignalingRollingCoordinatorLockProvider(inner, signal);

        using IRollingCoordinatorLock held = provider.Acquire("state.json");

        Assert.True(signal.WaitForOwnershipAsync(CancellationToken.None).IsCompletedSuccessfully);

        var failedSignal = new RollingCoordinatorOwnershipSignal();
        inner.Failure = new RollingStationSafetyException("busy");
        var failedProvider = new SignalingRollingCoordinatorLockProvider(inner, failedSignal);
        Assert.Throws<RollingStationSafetyException>(() => failedProvider.Acquire("state.json"));
        Assert.False(failedSignal.WaitForOwnershipAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task ReplenishmentTrigger_CoalescesPreexistingWakeups()
    {
        var trigger = new RollingReplenishmentTrigger();
        trigger.Signal();
        trigger.Signal();

        Task first = trigger.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(first.IsCompletedSuccessfully);
        await first;
        await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None);
    }

    private static RollingStationRuntimeState CreateState() => new()
    {
        PlannerId = "00112233445566778899aabbccddeeff",
        Phase = RollingStationPhase.WaitingForBlock,
        UpdatedAtUtc = DateTimeOffset.Parse("2026-10-01T12:00:00Z"),
    };

    private sealed class FakeCoordinator(
        Func<CancellationToken, Task<RollingStationRunResult>> run)
        : IRollingStationCoordinator
    {
        public Task<RollingStationRunResult> RunAsync(
            string configurationPath,
            bool acceptStoppedStaticCutover,
            CancellationToken cancellationToken) => run(cancellationToken);
    }

    private sealed class WaitingReplenisher : IRollingProgrammingReplenisher
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public bool Completed { get; private set; }

        public async Task RunAsync(string configurationPath, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
            finally
            {
                Completed = true;
            }
        }
    }

    private sealed class CountingReplenisher : IRollingProgrammingReplenisher
    {
        public int Calls { get; private set; }

        public Task RunAsync(string configurationPath, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingReplenisher : IRollingProgrammingReplenisher
    {
        public TaskCompletionSource Thrown { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RunAsync(string configurationPath, CancellationToken cancellationToken)
        {
            Thrown.TrySetResult();
            throw new InvalidOperationException("simulated background failure");
        }
    }

    private sealed class CountingTrigger : IRollingReplenishmentTrigger
    {
        public int Signals { get; private set; }

        public void Signal() => Signals++;

        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RecordingStateStore : IRollingStationStateStore
    {
        public RollingStationRuntimeState? State { get; private set; }

        public Exception? WriteFailure { get; set; }

        public Task WriteAsync(
            string path,
            RollingStationRuntimeState state,
            CancellationToken cancellationToken)
        {
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }

            State = state;
            return Task.CompletedTask;
        }

        public RollingStationRuntimeState Read(string path) => State!;

        public RollingStationRuntimeState? ReadIfExists(string path) => State;
    }

    private sealed class FakeLockProvider : IRollingCoordinatorLockProvider
    {
        public Exception? Failure { get; set; }

        public IRollingCoordinatorLock Acquire(string stationStatePath)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            return new FakeLock();
        }
    }

    private sealed class FakeLock : IRollingCoordinatorLock
    {
        public void Dispose()
        {
        }
    }
}
