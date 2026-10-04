using System.Diagnostics;

namespace NzyteTv.Media.Tests;

public sealed class E3BootReconnectDeploymentTests
{
    [Fact]
    public void Units_AreDedicatedTestOnlyOneShotAndBootTimer()
    {
        string root = FindRepositoryRoot();
        string service = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "checkpoint-3b2-e3",
            "nzyte-tv-3b2e-boot-reconnect.service"));
        string timer = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "checkpoint-3b2-e3",
            "nzyte-tv-3b2e-boot-reconnect.timer"));

        Assert.Contains("Type=oneshot", service, StringComparison.Ordinal);
        Assert.Contains(
            "ExecStart=/opt/nzyte-tv/integration/3b2d/e3/nzyte-tv-3b2e-boot-reconnect.sh run",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReadWritePaths=/opt/nzyte-tv/integration/3b2d/state/e3",
            service,
            StringComparison.Ordinal);
        Assert.DoesNotContain("EnvironmentFile", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart=", service, StringComparison.Ordinal);
        Assert.DoesNotContain("nzyte-tv.service", service, StringComparison.Ordinal);

        Assert.Contains("OnBootSec=150s", timer, StringComparison.Ordinal);
        Assert.Contains("Persistent=false", timer, StringComparison.Ordinal);
        Assert.Contains("WantedBy=timers.target", timer, StringComparison.Ordinal);
        Assert.DoesNotContain("OnUnitActiveSec", timer, StringComparison.Ordinal);
        Assert.DoesNotContain("OnCalendar", timer, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_UsesSafeProcessChecksAndDoesNotLoadOrDisplayCredentials()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "checkpoint-3b2-e3",
            "nzyte-tv-3b2e-boot-reconnect.sh"));

        Assert.Contains("/proc/sys/kernel/random/boot_id", script, StringComparison.Ordinal);
        Assert.Contains("last-attempt-boot-id", script, StringComparison.Ordinal);
        Assert.Contains("$PGREP_BIN -x -c", script, StringComparison.Ordinal);
        Assert.Contains("$FLOCK_BIN\" -n 9", script, StringComparison.Ordinal);
        Assert.Contains("/${pid}/status", script, StringComparison.Ordinal);
        Assert.Contains("PPid:", script, StringComparison.Ordinal);
        Assert.Contains("controlled-reconnection-started", script, StringComparison.Ordinal);
        Assert.DoesNotContain("pgrep -a", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ps ", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("journalctl", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EnvironmentFile", script, StringComparison.Ordinal);
        Assert.DoesNotContain("NZYTE_TV_RTMP_URL", script, StringComparison.Ordinal);
        Assert.DoesNotContain("rtmp://", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rtmps://", script, StringComparison.OrdinalIgnoreCase);
    }

    [E3ShellFact]
    public async Task MockSuite_CoversBootGuardReadinessConcurrencyAndBoundedRecovery()
    {
        string root = FindRepositoryRoot();
        string bash = E3ShellFactAttribute.FindBash()!;
        string python = E3ShellFactAttribute.FindPython()!;
        var startInfo = new ProcessStartInfo
        {
            FileName = bash,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("tests/checkpoint-3b2-e3/run-tests.sh");
        startInfo.Environment["E3_TEST_PYTHON_BIN"] = ToBashPath(python);

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
            $"E3 mock suite exited {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        Assert.Contains("Checkpoint 3B2-E3 mock tests passed.", output, StringComparison.Ordinal);
    }

    private static string ToBashPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        string fullPath = Path.GetFullPath(path).Replace('\\', '/');
        return $"/{char.ToLowerInvariant(fullPath[0])}{fullPath[2..]}";
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

public sealed class E3ShellFactAttribute : FactAttribute
{
    public E3ShellFactAttribute()
    {
        if (FindBash() is null || FindPython() is null)
        {
            Skip = "Bash and Python 3 are required for the E3 deployment mock suite.";
        }
    }

    public static string? FindBash()
    {
        if (OperatingSystem.IsWindows())
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return FindExisting(
                Path.Combine(programFiles, "Git", "bin", "bash.exe"),
                Path.Combine(programFiles, "Git", "usr", "bin", "bash.exe"));
        }

        return FindExisting("/bin/bash", "/usr/bin/bash");
    }

    public static string? FindPython()
    {
        string executable = OperatingSystem.IsWindows() ? "python.exe" : "python3";
        string[] pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return FindExisting(pathDirectories.Select(path => Path.Combine(path, executable)).ToArray());
    }

    private static string? FindExisting(params string[] paths) =>
        paths.FirstOrDefault(File.Exists);
}
