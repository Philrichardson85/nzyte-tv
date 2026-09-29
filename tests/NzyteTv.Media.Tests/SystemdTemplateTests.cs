using NzyteTv.Core;

namespace NzyteTv.Media.Tests;

public sealed class SystemdTemplateTests
{
    [Fact]
    public void Unit_UsesCheckpointTwoRestartSafetyAndCompletionSemantics()
    {
        string root = FindRepositoryRoot();
        string unitPath = Path.Combine(root, "deploy", "systemd", "nzyte-tv.service");

        string unit = File.ReadAllText(unitPath);

        Assert.Contains("User=u24", unit, StringComparison.Ordinal);
        Assert.Contains("Group=u24", unit, StringComparison.Ordinal);
        Assert.Contains("EnvironmentFile=/etc/nzyte-tv/secrets.env", unit, StringComparison.Ordinal);
        Assert.Contains(
            "ExecStart=/opt/nzyte-tv/app/nzytetv station run --config /etc/nzyte-tv/station.json",
            unit,
            StringComparison.Ordinal);
        Assert.Contains("RequiresMountsFor=/srv/nzyte-tv/media", unit, StringComparison.Ordinal);
        Assert.Contains("Restart=on-failure", unit, StringComparison.Ordinal);
        Assert.Contains(
            $"RestartPreventExitStatus={StationExitCodes.PermanentStartupFailure}",
            unit,
            StringComparison.Ordinal);
        Assert.NotEqual(1, StationExitCodes.PermanentStartupFailure);
        Assert.DoesNotContain("RestartPreventExitStatus=1", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart=always", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("NZYTE_TV_RTMP_URL=", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("rtmp://", unit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rtmps://", unit, StringComparison.OrdinalIgnoreCase);

        string secretsExample = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "config",
            "secrets.env.example"));
        string stationExample = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "config",
            "station.json.example"));
        Assert.Equal("NZYTE_TV_RTMP_URL=", secretsExample.Trim());
        Assert.DoesNotContain("rtmp", stationExample, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", stationExample, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NzyteTv.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Unable to locate repository root from the test output directory.");
    }
}
