using NzyteTv.Core;

namespace NzyteTv.Media;

public interface IRollingBlockExecutor
{
    Task<StationRunResult> RunAsync(
        StationConfiguration configuration,
        BroadcastPlan plan,
        CancellationToken cancellationToken);
}

public sealed class StationSupervisorRollingBlockExecutor : IRollingBlockExecutor
{
    private readonly StationSupervisor _supervisor;
    private readonly string _destination;
    private readonly Action<string>? _onFfmpegOutput;
    private readonly Action<BroadcastRecoveryUpdate>? _onUpdate;

    public StationSupervisorRollingBlockExecutor(
        StationSupervisor supervisor,
        string destination,
        Action<string>? onFfmpegOutput = null,
        Action<BroadcastRecoveryUpdate>? onUpdate = null)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        _supervisor = supervisor;
        _destination = destination;
        _onFfmpegOutput = onFfmpegOutput;
        _onUpdate = onUpdate;
    }

    public Task<StationRunResult> RunAsync(
        StationConfiguration configuration,
        BroadcastPlan plan,
        CancellationToken cancellationToken) => _supervisor.RunAsync(
        configuration,
        plan,
        _destination,
        _onFfmpegOutput,
        _onUpdate,
        cancellationToken);
}
