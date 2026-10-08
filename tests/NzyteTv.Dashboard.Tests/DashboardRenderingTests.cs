using System.Net;
using System.Reflection;
using NzyteTv.Dashboard.Status;

namespace NzyteTv.Dashboard.Tests;

public sealed class DashboardRenderingTests
{
    [Fact]
    public async Task Page_RendersMajorSectionsAndPublicStreamQualification()
    {
        await using var factory = new DashboardWebApplicationFactory(
            DashboardSnapshotFactory.Create());
        using HttpClient client = factory.CreateClient();

        string html = await client.GetStringAsync("/");

        Assert.Contains("NZYTE TV", html, StringComparison.Ordinal);
        Assert.Contains("Station", html, StringComparison.Ordinal);
        Assert.Contains("Broadcast", html, StringComparison.Ordinal);
        Assert.Contains("Now Playing", html, StringComparison.Ordinal);
        Assert.Contains("Programming", html, StringComparison.Ordinal);
        Assert.Contains("PROGRAMMING CONTROLS", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Spotlight Record", html, StringComparison.Ordinal);
        Assert.Contains("Newly generated programming only", html, StringComparison.Ordinal);
        Assert.Contains(
            "Public YouTube playback is not independently verified.",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Next Up", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Elapsed", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Duration", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Page_EncodesDynamicMediaText()
    {
        await using var factory = new DashboardWebApplicationFactory(
            DashboardSnapshotFactory.Create("<script>alert('unsafe')</script>"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("<script>alert('unsafe')</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserScript_UsesTextContentAndNeverInnerHtml()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(Path.Combine(
            root,
            "src",
            "NzyteTv.Dashboard",
            "wwwroot",
            "js",
            "dashboard.js"));

        Assert.Contains("textContent", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("option.textContent", script, StringComparison.Ordinal);
        Assert.Contains("X-NZYTE-TV-CSRF", script, StringComparison.Ordinal);
        Assert.Contains("window.setTimeout(poll", script, StringComparison.Ordinal);
        Assert.DoesNotContain("WebSocket", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EventSource", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "setLiteralText(\"playback-title\", snapshot.playback.title)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "setLiteralText(\"dashboard-version\", snapshot.dashboard.version)",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "setStatusText(\"playback-title\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains("overall.dataset.stationState", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageAndRefreshPath_PreserveLiteralTitleAndDashboardVersion()
    {
        DashboardStatusSnapshot snapshot = DashboardSnapshotFactory.Create("Prince - Purple Rain");
        await using var factory = new DashboardWebApplicationFactory(snapshot);
        using HttpClient client = factory.CreateClient();

        string html = await client.GetStringAsync("/");

        Assert.Contains("Prince - Purple Rain", html, StringComparison.Ordinal);
        Assert.Contains("Dashboard version", html, StringComparison.Ordinal);
        Assert.Contains("3B3-A-test", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MainIndicator_ReflectsStationStateSeparatelyFromSnapshotQuality()
    {
        DashboardStatusSnapshot snapshot = DashboardSnapshotFactory.Create() with
        {
            Quality = DashboardSnapshotQuality.Healthy,
            Station = DashboardSnapshotFactory.Create().Station with
            {
                Status = DashboardStationStatus.Failed,
            },
        };
        await using var factory = new DashboardWebApplicationFactory(snapshot);
        using HttpClient client = factory.CreateClient();

        string html = await client.GetStringAsync("/");

        Assert.Contains("data-station-state=\"failed\"", html, StringComparison.Ordinal);
        Assert.Contains("Snapshot quality:", html, StringComparison.Ordinal);
        Assert.Contains(">Failed<", html, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardAssembly_HasIntentionalPrereleaseVersion()
    {
        string? version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Assert.StartsWith("0.2.0-3b3-b1+", version, StringComparison.Ordinal);
    }

    internal static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NzyteTv.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
