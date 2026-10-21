using System.Diagnostics;

namespace NzyteTv.Media.Tests;

public sealed class NetworkWatchdogDeploymentTests
{
    [Fact]
    public void Units_AreDedicatedRecurringOneshotWithHardenedStateAccess()
    {
        string root = FindRepositoryRoot();
        string deployment = Path.Combine(root, "deploy", "checkpoint-network-watchdog");

        string service = File.ReadAllText(
            Path.Combine(deployment, "nzyte-tv-network-watchdog.service"));
        string timer = File.ReadAllText(
            Path.Combine(deployment, "nzyte-tv-network-watchdog.timer"));

        Assert.Contains("Type=oneshot", service, StringComparison.Ordinal);
        Assert.Contains(
            "ConditionPathExists=/opt/nzyte-tv/production/recovery/nzyte-tv-network-watchdog.sh",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExecStart=/opt/nzyte-tv/production/recovery/nzyte-tv-network-watchdog.sh run",
            service,
            StringComparison.Ordinal);

        Assert.Contains("Environment=NW_CONNECTION_NAME=g-route", service, StringComparison.Ordinal);
        Assert.Contains("Environment=NW_INTERFACE=wlan0", service, StringComparison.Ordinal);
        Assert.Contains(
            "Environment=NW_STATE_DIR=/var/lib/nzyte-tv/network-watchdog",
            service,
            StringComparison.Ordinal);

        Assert.Contains("ProtectSystem=strict", service, StringComparison.Ordinal);
        Assert.Contains(
            "StateDirectory=nzyte-tv/network-watchdog",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "StateDirectoryMode=0700",
            service,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReadWritePaths=",
            service,
            StringComparison.Ordinal);
        Assert.Contains("ProtectKernelTunables=true", service, StringComparison.Ordinal);
        Assert.Contains("ProtectKernelModules=true", service, StringComparison.Ordinal);
        Assert.Contains("ProtectControlGroups=true", service, StringComparison.Ordinal);
        Assert.Contains("RestrictSUIDSGID=true", service, StringComparison.Ordinal);
        Assert.Contains("LockPersonality=true", service, StringComparison.Ordinal);
        Assert.Contains("MemoryDenyWriteExecute=true", service, StringComparison.Ordinal);
        Assert.Contains(
            "RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK",
            service,
            StringComparison.Ordinal);

        Assert.DoesNotContain("EnvironmentFile", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart=", service, StringComparison.Ordinal);

        Assert.Contains("OnBootSec=300s", timer, StringComparison.Ordinal);
        Assert.Contains("OnUnitInactiveSec=60s", timer, StringComparison.Ordinal);
        Assert.Contains("AccuracySec=5s", timer, StringComparison.Ordinal);
        Assert.Contains("Persistent=false", timer, StringComparison.Ordinal);
        Assert.Contains(
            "Unit=nzyte-tv-network-watchdog.service",
            timer,
            StringComparison.Ordinal);
        Assert.Contains("WantedBy=timers.target", timer, StringComparison.Ordinal);
        Assert.DoesNotContain("OnCalendar", timer, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_UsesConservativeEscalationAndPersistentRebootGuard()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "checkpoint-network-watchdog",
            "nzyte-tv-network-watchdog.sh"));

        Assert.Contains(
            "FAILURE_THRESHOLD=\"${NW_FAILURE_THRESHOLD:-3}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "DNS_TIMEOUT_SECONDS=\"${NW_DNS_TIMEOUT_SECONDS:-8}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "MIN_UPTIME_SECONDS=\"${NW_MIN_UPTIME_SECONDS:-900}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "REBOOT_COOLDOWN_SECONDS=\"${NW_REBOOT_COOLDOWN_SECONDS:-21600}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "HEALTHY_REARM_THRESHOLD=\"${NW_HEALTHY_REARM_THRESHOLD:-5}\"",
            script,
            StringComparison.Ordinal);

        Assert.Contains(
            "gateway-failures",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "recovery-stage",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "last-watchdog-reboot-epoch",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "reboot-consumed",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "healthy-streak",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "watchdog.lock",
            script,
            StringComparison.Ordinal);

        Assert.Contains(
            "dns-unresolved-gateway-healthy",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "connection-cycle-started",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "wifi-radio-cycle-started",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "network-recovery-exhausted",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "reboot-suppressed-cooldown",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "reboot-suppressed-minimum-uptime",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "reboot-suppressed-incident-consumed",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "reboot-suppressed-invalid-incident-state",
            script,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "nzyte-tv.service",
            script,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "NZYTE_TV_RTMP_URL",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "rtmp://",
            script,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "rtmps://",
            script,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "secrets.env",
            script,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "journalctl",
            script,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "pgrep -a",
            script,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            1,
            CountOccurrences(
                script,
                "\"$SYSTEMCTL_BIN\" reboot",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Script_ConsumesIncidentBeforeRebootAndBoundsDnsResolution()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "checkpoint-network-watchdog",
            "nzyte-tv-network-watchdog.sh"));

        int dnsProbe = script.IndexOf(
            "\"$TIMEOUT_BIN\" \"$DNS_TIMEOUT_SECONDS\"",
            StringComparison.Ordinal);
        int getentProbe = script.IndexOf(
            "\"$GETENT_BIN\" ahostsv4 \"$DNS_NAME\"",
            dnsProbe,
            StringComparison.Ordinal);

        Assert.True(dnsProbe >= 0);
        Assert.True(getentProbe > dnsProbe);

        int consumeIncident = script.IndexOf(
            "write_atomic_line \"$REBOOT_CONSUMED_FILE\" \"1\"",
            StringComparison.Ordinal);
        int recordRebootHistory = script.IndexOf(
            "write_atomic_line \"$REBOOT_FILE\" \"$current_epoch\"",
            consumeIncident,
            StringComparison.Ordinal);
        int requestReboot = script.IndexOf(
            "\"$SYSTEMCTL_BIN\" reboot",
            recordRebootHistory,
            StringComparison.Ordinal);

        Assert.True(consumeIncident >= 0);
        Assert.True(recordRebootHistory > consumeIncident);
        Assert.True(requestReboot > recordRebootHistory);
    }

    [Fact]
    public void Script_SeparatesGatewayHealthFromDnsHealth()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "checkpoint-network-watchdog",
            "nzyte-tv-network-watchdog.sh"));

        int gatewayHealthy = script.IndexOf(
            "if [[ \"$gateway\" != \"unavailable\" ]] && probe_gateway \"$gateway\"; then",
            StringComparison.Ordinal);
        int resetState = script.IndexOf(
            "reset_health_state || {",
            gatewayHealthy,
            StringComparison.Ordinal);
        int dnsProbe = script.IndexOf(
            "if probe_dns; then",
            resetState,
            StringComparison.Ordinal);
        int dnsDegraded = script.IndexOf(
            "dns-unresolved-gateway-healthy",
            dnsProbe,
            StringComparison.Ordinal);
        int failureIncrement = script.IndexOf(
            "failures=$((failures + 1))",
            dnsDegraded,
            StringComparison.Ordinal);

        Assert.True(gatewayHealthy >= 0);
        Assert.True(resetState > gatewayHealthy);
        Assert.True(dnsProbe > resetState);
        Assert.True(dnsDegraded > dnsProbe);
        Assert.True(failureIncrement > dnsDegraded);
    }

    [E3ShellFact]
    public async Task MockSuite_CoversHealthRecoveryEscalationAndRebootGuards()
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

        startInfo.ArgumentList.Add("tests/checkpoint-network-watchdog/run-tests.sh");

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
            $"Network watchdog mock suite exited {process.ExitCode}.{Environment.NewLine}" +
            $"{output}{Environment.NewLine}{error}");

        Assert.Contains(
            "PASS healthy",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "PASS dns-degraded",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "PASS connection-recovery-success",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "PASS connection-recovery-failure",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "PASS radio-recovery-success",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "PASS radio-recovery-failure",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "PASS reboot",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "PASS reboot-cooldown",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "PASS minimum-uptime",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "PASS incident-consumed",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "PASS healthy-rearm",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "PASS healthy-streak-break",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "PASS reboot-request-failure-latched",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "PASS invalid-incident-state",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "Network watchdog mock tests passed.",
            output,
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(
        string source,
        string value,
        StringComparison comparison)
    {
        int count = 0;
        int start = 0;

        while (true)
        {
            int index = source.IndexOf(value, start, comparison);

            if (index < 0)
            {
                return count;
            }

            count++;
            start = index + value.Length;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (
            directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "NzyteTv.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException(
                "Unable to locate repository root from the test output directory.");
    }
}
