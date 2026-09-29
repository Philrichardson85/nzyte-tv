using NzyteTv.Cli;
using NzyteTv.Core;

namespace NzyteTv.Core.Tests;

public sealed class StationCliTests
{
    [Fact]
    public async Task Run_MissingStationConfigurationUsesPermanentStartupExitCode()
    {
        string missingPath = Path.Combine(
            Path.GetTempPath(),
            $"nzytetv-missing-station-{Guid.NewGuid():N}.json");

        int exitCode = await CliApplication.RunAsync(
            ["station", "run", "--config", missingPath],
            CancellationToken.None);

        Assert.Equal(StationExitCodes.PermanentStartupFailure, exitCode);
    }
}
