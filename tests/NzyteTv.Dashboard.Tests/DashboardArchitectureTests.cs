using System.Xml.Linq;

namespace NzyteTv.Dashboard.Tests;

public sealed class DashboardArchitectureTests
{
    [Fact]
    public void ProjectReferencesCoreAndMediaButNotCli()
    {
        string root = DashboardRenderingTests.FindRepositoryRoot();
        string projectPath = Path.Combine(
            root,
            "src",
            "NzyteTv.Dashboard",
            "NzyteTv.Dashboard.csproj");
        XDocument project = XDocument.Load(projectPath);
        string[] references = project.Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .ToArray();

        Assert.Contains(references, value => value.Contains("NzyteTv.Core", StringComparison.Ordinal));
        Assert.Contains(references, value => value.Contains("NzyteTv.Media", StringComparison.Ordinal));
        Assert.DoesNotContain(references, value => value.Contains("NzyteTv.Cli", StringComparison.Ordinal));
    }

    [Fact]
    public void DashboardSourceContainsNoProcessExecutionDiagnosticsOrSystemControl()
    {
        string root = DashboardRenderingTests.FindRepositoryRoot();
        string dashboardRoot = Path.Combine(root, "src", "NzyteTv.Dashboard");
        string source = string.Join(
            "\n",
            Directory.EnumerateFiles(dashboardRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));
        string[] forbidden =
        [
            "ProcessStartInfo",
            "System.Diagnostics",
            "Process.Start",
            "ProcessRunner",
            "MediaToolLocator",
            "FfmpegBroadcaster",
            "BroadcastDiagnostics",
            "RollingPlanStore",
            "RollingProgrammingManifest",
            "RecentStandardError",
            "secrets.env",
            "NZYTE_TV_RTMP_URL",
            "Environment.GetEnvironmentVariable",
        ];

        foreach (string value in forbidden)
        {
            Assert.DoesNotContain(value, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PagesDependOnlyOnDashboardStatusBoundary()
    {
        string root = DashboardRenderingTests.FindRepositoryRoot();
        string pagesRoot = Path.Combine(root, "src", "NzyteTv.Dashboard", "Pages");
        string source = string.Join(
            "\n",
            Directory.EnumerateFiles(pagesRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.Contains("IDashboardStatusProvider", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IStationStateStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IRollingStationStateStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IProcessExistence", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Ffmpeg", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostingSourceBindsKestrelOnlyToIpv4Loopback()
    {
        string root = DashboardRenderingTests.FindRepositoryRoot();
        string program = File.ReadAllText(Path.Combine(
            root,
            "src",
            "NzyteTv.Dashboard",
            "Program.cs"));

        Assert.Contains("IPAddress.Loopback", program, StringComparison.Ordinal);
        Assert.DoesNotContain("IPAddress.Any", program, StringComparison.Ordinal);
        Assert.DoesNotContain("IPAddress.IPv6Any", program, StringComparison.Ordinal);
        Assert.DoesNotContain("UseUrls", program, StringComparison.Ordinal);
        Assert.DoesNotContain("IsEnvironment(\"Testing\")", program, StringComparison.Ordinal);
        Assert.Contains("RejectAlternativeListeners", program, StringComparison.Ordinal);
    }

    [Fact]
    public void StateAndHttpBoundariesExposeOnlyReadOperations()
    {
        string[] stateMethods = typeof(NzyteTv.Dashboard.Status.IDashboardStateReader)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();
        string[] statusMethods = typeof(NzyteTv.Dashboard.Status.IDashboardStatusProvider)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.All(stateMethods, method =>
            Assert.StartsWith("Read", method, StringComparison.Ordinal));
        Assert.Equal(["GetStatus"], statusMethods);
        Assert.DoesNotContain(
            stateMethods,
            method => method.Contains("Write", StringComparison.Ordinal));
        Assert.DoesNotContain(
            stateMethods,
            method => method.Contains("Delete", StringComparison.Ordinal));
    }

    [Fact]
    public void DashboardServiceTemplate_ContainsNoSecretsOrBroadcasterControls()
    {
        string root = DashboardRenderingTests.FindRepositoryRoot();
        string service = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "checkpoint-3b3-a",
            "nzyte-tv-dashboard.service"));

        Assert.Contains(
            "ExecStart=/opt/nzyte-tv/dashboard/nzytetv-dashboard",
            service,
            StringComparison.Ordinal);
        string[] forbidden =
        [
            "/etc/nzyte-tv/secrets.env",
            "EnvironmentFile=",
            "systemctl",
            "nzyte-tv.service",
            "station run",
            "rtmp://",
            "rtmps://",
        ];
        foreach (string value in forbidden)
        {
            Assert.DoesNotContain(value, service, StringComparison.OrdinalIgnoreCase);
        }
    }
}
