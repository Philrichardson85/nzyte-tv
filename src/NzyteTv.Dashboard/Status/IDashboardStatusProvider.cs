namespace NzyteTv.Dashboard.Status;

public interface IDashboardStatusProvider
{
    DashboardStatusSnapshot GetStatus();
}

internal interface IDashboardStatusSnapshotSource
{
    DashboardStatusSnapshot ReadStatus();
}
