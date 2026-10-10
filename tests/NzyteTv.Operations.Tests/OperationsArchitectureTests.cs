using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using NzyteTv.Operations.Configuration;

namespace NzyteTv.Operations.Tests;

public sealed class OperationsArchitectureTests
{
    [Fact]
    public void HostRejectsEveryTcpListenerConfiguration()
    {
        string[] keys = ["urls", "http_ports", "https_ports", "Kestrel:Endpoints:Remote:Url"];
        foreach (string key in keys)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [key] = "http://0.0.0.0:9999" })
                .Build();
            Assert.Throws<InvalidOperationException>(() =>
                OperationsHostConfiguration.RejectTcpListeners(configuration));
        }
    }

    [Fact]
    public void HelperSourceUsesUnixSocketAndContainsNoProcessOrSecretDependencies()
    {
        string root = FindRepositoryRoot();
        string sourceRoot = Path.Combine(root, "src", "NzyteTv.Operations");
        string source = string.Join("\n", Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));
        Assert.Contains("ListenUnixSocket", source, StringComparison.Ordinal);
        string[] forbidden = ["ListenAnyIP", "IPAddress.Any", "ProcessStartInfo", "Process.Start", "systemctl", "ffmpeg", "secrets.env", "NZYTE_TV_RTMP_URL"];
        foreach (string value in forbidden) Assert.DoesNotContain(value, source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MediaIntegrationHasNoBootstrapStationProcessOrNormalizationSurface()
    {
        string root = FindRepositoryRoot();
        string source = string.Join("\n", Directory.EnumerateFiles(
                Path.Combine(root, "src", "NzyteTv.Operations"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));
        foreach (string forbidden in new[]
        {
            "MediaMetadataBootstrapService",
            "MediaNormalizer",
            "BroadcastFfmpeg",
            "StationSupervisor",
            "RollingStationCoordinator",
            "ProcessStartInfo",
            "systemctl",
            "File.Delete",
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("MapPost(\"/api/v1/media-library/bootstrap", source, StringComparison.Ordinal);
        Assert.Contains("ListenUnixSocket", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaFeatureDefaultsDisabledWithoutTrustedPaths()
    {
        var options = OperationsOptions.Load(new ConfigurationBuilder().Build());
        Assert.False(options.MediaLibrary.Enabled);
        Assert.Null(options.MediaLibrary.MediaRoot);
        Assert.Null(options.MediaLibrary.MetadataRoot);
        Assert.Null(options.MediaLibrary.InboxRoot);
    }

    [Fact]
    public void HelperProjectDoesNotReferenceCliOrDashboard()
    {
        string root = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(root, "src", "NzyteTv.Operations", "NzyteTv.Operations.csproj"));
        Assert.Contains("NzyteTv.Core", project, StringComparison.Ordinal);
        Assert.Contains("NzyteTv.Media", project, StringComparison.Ordinal);
        Assert.DoesNotContain("NzyteTv.Cli", project, StringComparison.Ordinal);
        Assert.DoesNotContain("NzyteTv.Dashboard", project, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxDefaultUsesTheActiveProductionMediaRoot()
    {
        Assert.Equal("/srv/nzyte-tv/media", OperationsOptions.LinuxDefaultMediaRoot);
    }

    [Fact]
    public void CandidateServiceTemplatesContainNoSecretsOrBroadcasterControls()
    {
        string root = FindRepositoryRoot();
        string directory = Path.Combine(root, "deploy", "checkpoint-3b3-b1");
        string operations = File.ReadAllText(Path.Combine(directory, "nzyte-tv-operations.service"));
        string dashboard = File.ReadAllText(Path.Combine(directory, "nzyte-tv-dashboard.service"));
        string mount = File.ReadAllText(Path.Combine(directory, "nzyte-tv-programming-catalog.mount.template"));
        string dropIn = File.ReadAllText(Path.Combine(directory, "nzyte-tv.service.d", "programming-catalog.conf"));
        Assert.Contains("ExecStart=/opt/nzyte-tv/operations/nzytetv-operations", operations, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/opt/nzyte-tv/dashboard/nzytetv-dashboard", dashboard, StringComparison.Ordinal);
        Assert.Contains("User=nzyte-ops", operations, StringComparison.Ordinal);
        Assert.Contains("User=nzyte-dashboard", dashboard, StringComparison.Ordinal);
        string[] operationLines = operations.Split('\n', StringSplitOptions.TrimEntries);
        string[] dashboardLines = dashboard.Split('\n', StringSplitOptions.TrimEntries);
        Assert.Contains(
            "Environment=Operations__MediaRoot=/srv/nzyte-tv/media",
            operationLines);
        Assert.Contains(
            "ReadOnlyPaths=/srv/nzyte-tv/media",
            operationLines);
        Assert.Contains(
            "ReadWritePaths=/srv/nzyte-tv/media/catalog /run/nzyte-tv-operations",
            operationLines);
        Assert.DoesNotContain(
            "Environment=Operations__MediaRoot=/srv/nzyte-tv",
            operationLines);
        Assert.DoesNotContain(
            operationLines,
            line => line.StartsWith("ReadWritePaths=", StringComparison.Ordinal)
                && line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("/srv/nzyte-tv/catalog", StringComparer.Ordinal));
        Assert.Contains("-/srv/nzyte-tv", dashboard, StringComparison.Ordinal);
        Assert.Contains("SupplementaryGroups=nzyte-programming", operationLines);
        Assert.Contains("SupplementaryGroups=nzyte-tv-state nzyte-ops", dashboardLines);
        Assert.Contains("InaccessiblePaths=-/etc/nzyte-tv -/var/lib/nzyte-tv -/var/lib/nzyte-tv-programming", operationLines);
        Assert.Contains(
            "InaccessiblePaths=-/etc/nzyte-tv -/var/lib/nzyte-tv/broadcast-diagnostics.json -/var/lib/nzyte-tv-programming -/srv/nzyte-tv",
            dashboardLines);
        Assert.DoesNotContain(
            dashboardLines,
            line => line.StartsWith("SupplementaryGroups=", StringComparison.Ordinal)
                && line.Contains("nzyte-programming", StringComparison.Ordinal));
        Assert.Equal(
            ["/srv/nzyte-tv/media/catalog", "/run/nzyte-tv-operations"],
            Assert.Single(
                    operationLines,
                    line => line.StartsWith("ReadWritePaths=", StringComparison.Ordinal))
                .Remove(0, "ReadWritePaths=".Length)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string combined = operations + dashboard;
        string[] forbidden = ["EnvironmentFile=", "secrets.env", "sudo", "systemctl", "rtmp://", "rtmps://", "nzyte-tv.service"];
        foreach (string value in forbidden) Assert.DoesNotContain(value, combined, StringComparison.OrdinalIgnoreCase);
        string allCandidates = combined + mount + dropIn;
        foreach (string value in forbidden[..^1])
        {
            Assert.DoesNotContain(value, allCandidates, StringComparison.OrdinalIgnoreCase);
        }
        Assert.All(
            operationLines.Concat(dashboardLines)
                .Where(line => line.StartsWith("CapabilityBoundingSet=", StringComparison.Ordinal)
                    || line.StartsWith("AmbientCapabilities=", StringComparison.Ordinal)),
            line => Assert.DoesNotContain(' ', line));
    }

    [Fact]
    public void CandidateCatalogMountAndBroadcasterDropInFailClosed()
    {
        string root = FindRepositoryRoot();
        string directory = Path.Combine(root, "deploy", "checkpoint-3b3-b1");
        string mount = File.ReadAllText(Path.Combine(
            directory,
            "nzyte-tv-programming-catalog.mount.template"));
        string dropIn = File.ReadAllText(Path.Combine(
            directory,
            "nzyte-tv.service.d",
            "programming-catalog.conf"));
        string operations = File.ReadAllText(Path.Combine(directory, "nzyte-tv-operations.service"));

        Assert.Contains("What=/var/lib/nzyte-tv-programming/catalog", mount, StringComparison.Ordinal);
        Assert.Contains("Where=/srv/nzyte-tv/media/catalog", mount, StringComparison.Ordinal);
        Assert.Contains("Type=none", mount, StringComparison.Ordinal);
        Assert.Contains("Options=bind", mount, StringComparison.Ordinal);
        Assert.Contains("Requires=srv-nzyte\\x2dtv-media.mount", mount, StringComparison.Ordinal);
        Assert.Contains("After=srv-nzyte\\x2dtv-media.mount", mount, StringComparison.Ordinal);
        Assert.Contains("Requires=srv-nzyte\\x2dtv-media-catalog.mount", dropIn, StringComparison.Ordinal);
        Assert.Contains(
            "RequiresMountsFor=/var/lib/nzyte-tv /srv/nzyte-tv/media /srv/nzyte-tv/media/catalog",
            dropIn,
            StringComparison.Ordinal);
        Assert.Contains("SupplementaryGroups=nzyte-tv-state", dropIn, StringComparison.Ordinal);
        Assert.Contains("StateDirectory=", dropIn.Split('\n', StringSplitOptions.TrimEntries));
        Assert.Contains("Requires=srv-nzyte\\x2dtv-media-catalog.mount", operations, StringComparison.Ordinal);
        Assert.Contains("RequiresMountsFor=/srv/nzyte-tv/media/catalog", operations, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateSocketAccessDoesNotGrantDashboardProgrammingAccess()
    {
        string root = FindRepositoryRoot();
        string directory = Path.Combine(root, "deploy", "checkpoint-3b3-b1");
        string[] operations = File.ReadAllLines(Path.Combine(directory, "nzyte-tv-operations.service"));
        string[] dashboard = File.ReadAllLines(Path.Combine(directory, "nzyte-tv-dashboard.service"));

        Assert.Contains("Group=nzyte-ops", operations);
        Assert.Contains("RuntimeDirectory=nzyte-tv-operations", operations);
        Assert.Contains("RuntimeDirectoryMode=0750", operations);
        Assert.Contains("UMask=0007", operations);
        Assert.Contains("SupplementaryGroups=nzyte-tv-state nzyte-ops", dashboard);
        Assert.DoesNotContain(dashboard, line => line.Contains("nzyte-programming", StringComparison.Ordinal));
        Assert.Contains(dashboard, line => line.Contains("-/srv/nzyte-tv", StringComparison.Ordinal));
    }

    [Fact]
    public void DeploymentPlanRecordsAcceptedExt4PermissionAndB2Boundaries()
    {
        string root = FindRepositoryRoot();
        string plan = File.ReadAllText(Path.Combine(root, "docs", "deployment-3b3-b1.md"));

        Assert.Contains(
            "systemd-escape --path --suffix=mount /srv/nzyte-tv/media/catalog",
            plan,
            StringComparison.Ordinal);
        Assert.Contains("srv-nzyte\\x2dtv-media-catalog.mount", plan, StringComparison.Ordinal);
        Assert.Contains("/var/lib/nzyte-tv-programming/catalog", plan, StringComparison.Ordinal);
        Assert.Contains("u24:nzyte-programming  2770", plan, StringComparison.Ordinal);
        Assert.Contains("u24:nzyte-tv-state  2750", plan, StringComparison.Ordinal);
        Assert.Contains("Do not delete them", plan, StringComparison.Ordinal);
        Assert.Contains("Deferred 3B3-B2 storage decision", plan, StringComparison.Ordinal);
        Assert.Contains("does not establish a safe B2 permission model", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasedDashboardServiceTemplateRemainsFrozen()
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(
            root,
            "deploy",
            "checkpoint-3b3-a",
            "nzyte-tv-dashboard.service");
        string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal("7d62c58c9642984b17eba80eb1c35c0af9f5823f0cd025c17e247814ee5c6210", hash);
    }

    [Fact]
    public void StateStoresUseSameDirectoryCreateAndAtomicRename()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "NzyteTv.Media",
            "StationStateStore.cs"));
        Assert.Contains("string temporaryPath = Path.Combine(", source, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", source, StringComparison.Ordinal);
        Assert.Contains("File.Move(temporaryPath, fullPath, overwrite: true)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetUnixFileMode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetAccessControl", source, StringComparison.Ordinal);

        string productionUnit = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "systemd",
            "nzyte-tv.service"));
        Assert.Contains("User=u24", productionUnit, StringComparison.Ordinal);
        Assert.Contains("Group=u24", productionUnit, StringComparison.Ordinal);
        Assert.Contains("UMask=0027", productionUnit, StringComparison.Ordinal);
    }

    internal static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "NzyteTv.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
