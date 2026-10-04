using System.Diagnostics;

namespace NzyteTv.Media.Tests;

public sealed class ProductionCutoverDeploymentTests
{
    [Fact]
    public void ProductionUnits_UseRollingDiagnosticsAndIsolatedRecoveryProfile()
    {
        string root = FindRepositoryRoot();
        string deployment = Path.Combine(root, "deploy", "checkpoint-3b2-f");
        string station = File.ReadAllText(Path.Combine(deployment, "nzyte-tv.service"));
        string recovery = File.ReadAllText(Path.Combine(deployment, "nzyte-tv-boot-reconnect.service"));
        string timer = File.ReadAllText(Path.Combine(deployment, "nzyte-tv-boot-reconnect.timer"));

        Assert.Contains("User=u24", station, StringComparison.Ordinal);
        Assert.Contains("EnvironmentFile=/etc/nzyte-tv/secrets.env", station, StringComparison.Ordinal);
        Assert.Contains(
            "Environment=NZYTE_TV_BROADCAST_DIAGNOSTICS_PATH=/var/lib/nzyte-tv/broadcast-diagnostics.json",
            station,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExecStart=/opt/nzyte-tv/production/nzyte-tv-production-launch.sh",
            station,
            StringComparison.Ordinal);
        Assert.DoesNotContain("station run --config", station, StringComparison.Ordinal);

        Assert.Contains("E3_TARGET_SERVICE=nzyte-tv.service", recovery, StringComparison.Ordinal);
        Assert.Contains("E3_BLOCKED_SERVICE=nzyte-tv-3b2d.service", recovery, StringComparison.Ordinal);
        Assert.Contains(
            "E3_BLOCKED_TIMER=nzyte-tv-3b2e-boot-reconnect.timer",
            recovery,
            StringComparison.Ordinal);
        Assert.Contains("E3_STATE_DIR=/var/lib/nzyte-tv/recovery", recovery, StringComparison.Ordinal);
        Assert.Contains(
            "E3_DIAGNOSTICS_FILE=/var/lib/nzyte-tv/broadcast-diagnostics.json",
            recovery,
            StringComparison.Ordinal);
        Assert.DoesNotContain("EnvironmentFile", recovery, StringComparison.Ordinal);

        Assert.Contains("OnBootSec=150s", timer, StringComparison.Ordinal);
        Assert.Contains("Persistent=false", timer, StringComparison.Ordinal);
        Assert.DoesNotContain("OnUnitActiveSec", timer, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchAndHandoff_AreExplicitCredentialSafeAndDoNotMutateProgrammingState()
    {
        string root = FindRepositoryRoot();
        string deployment = Path.Combine(root, "deploy", "checkpoint-3b2-f");
        string launch = File.ReadAllText(Path.Combine(deployment, "nzyte-tv-production-launch.sh"));
        string handoff = File.ReadAllText(Path.Combine(deployment, "nzyte-tv-production-handoff.sh"));

        Assert.Contains("station rolling run", launch, StringComparison.Ordinal);
        Assert.Contains("--accept-stopped-static-cutover", launch, StringComparison.Ordinal);
        Assert.Contains("ROLLING_STATE_PATH", launch, StringComparison.Ordinal);
        Assert.Contains("CUTOVER_MARKER", launch, StringComparison.Ordinal);
        Assert.Contains("exec \"$APP_BIN\"", launch, StringComparison.Ordinal);

        Assert.Contains("rollback-stop-production", handoff, StringComparison.Ordinal);
        Assert.Contains("rollback-start-test", handoff, StringComparison.Ordinal);
        Assert.Contains("$PGREP_BIN -x -c", handoff, StringComparison.Ordinal);
        Assert.Contains("PPid:", handoff, StringComparison.Ordinal);
        Assert.DoesNotContain("state.json", handoff, StringComparison.Ordinal);
        Assert.DoesNotContain("manifest.json", handoff, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.env", handoff, StringComparison.Ordinal);
        Assert.DoesNotContain("journalctl", handoff, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pgrep -a", handoff, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rtmp://", handoff, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rtmps://", handoff, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runbook_PinsAcceptedStateAndRequiresReversibleExclusiveTransition()
    {
        string root = FindRepositoryRoot();
        string runbook = File.ReadAllText(Path.Combine(root, "docs", "production-cutover-3b2f.md"));

        Assert.Contains(
            "27bdb55cba7b125866f727625ee72eac36c136106edbc8a1b9218c5f28f20d93",
            runbook,
            StringComparison.Ordinal);
        Assert.Contains(
            "638a6996d9cb4d5cbf2f473266f5bc2432f52d36a45bfb8ea868c987512b44eb",
            runbook,
            StringComparison.Ordinal);
        Assert.Contains("rolling-programming.tar", runbook, StringComparison.Ordinal);
        Assert.Contains("sudo test ! -e /var/lib/nzyte-tv/rolling-state.json", runbook, StringComparison.Ordinal);
        Assert.Contains("Do **not** run `programming rolling init`", runbook, StringComparison.Ordinal);
        Assert.Contains("Do not edit `/etc/nzyte-tv/station.json`", runbook, StringComparison.Ordinal);

        int stop = runbook.IndexOf("rollback-stop-production", StringComparison.Ordinal);
        int restore = runbook.IndexOf(
            "This is the explicitly authorized restoration",
            StringComparison.Ordinal);
        int startTest = runbook.IndexOf("rollback-start-test", StringComparison.Ordinal);
        Assert.True(stop >= 0 && restore > stop && startTest > restore);
    }

    [E3ShellFact]
    public async Task MockSuite_CoversExclusiveCutoverFailureAndRollback()
    {
        string root = FindRepositoryRoot();
        string bash = E3ShellFactAttribute.FindBash()!;
        var startInfo = new ProcessStartInfo
        {
            FileName = bash,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("tests/checkpoint-3b2-f/run-tests.sh");

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token);

        string output = await stdout;
        string error = await stderr;
        Assert.True(
            process.ExitCode == 0,
            $"3B2-F mock suite exited {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        Assert.Contains("Checkpoint 3B2-F deployment mock tests passed.", output, StringComparison.Ordinal);
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
