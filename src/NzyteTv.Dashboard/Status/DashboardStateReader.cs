using NzyteTv.Core;
using NzyteTv.Dashboard.Configuration;
using NzyteTv.Media;

namespace NzyteTv.Dashboard.Status;

public interface IDashboardStateReader
{
    StationRuntimeState? ReadStationState();

    RollingStationRuntimeState? ReadRollingState();

    RollingReplenishmentState? ReadReplenishmentState();
}

public sealed class DashboardStateReader : IDashboardStateReader
{
    private readonly DashboardOptions _options;
    private readonly StationStateStore _stationStore = new();
    private readonly RollingStationStateStore _rollingStore = new();
    private readonly RollingReplenishmentStateStore _replenishmentStore = new();

    public DashboardStateReader(DashboardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public StationRuntimeState? ReadStationState() =>
        _stationStore.ReadIfExists(_options.StationStatePath);

    public RollingStationRuntimeState? ReadRollingState() =>
        _rollingStore.ReadIfExists(_options.RollingStatePath);

    public RollingReplenishmentState? ReadReplenishmentState() =>
        _replenishmentStore.ReadIfExists(_options.ReplenishmentStatePath);
}
