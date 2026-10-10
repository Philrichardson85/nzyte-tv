namespace NzyteTv.Media.Tests;

public sealed class B2BArchitectureTests
{
    [Fact]
    public void RefreshEngineHasNoProcessStationFfmpegOrMediaMutationDependency()
    {
        string root = FindRepositoryRoot();
        string refresh = File.ReadAllText(Path.Combine(
            root,
            "src",
            "NzyteTv.Media",
            "MediaLibraryRefreshService.cs"));
        string bootstrap = File.ReadAllText(Path.Combine(
            root,
            "src",
            "NzyteTv.Media",
            "MediaMetadataBootstrapService.cs"));
        string source = refresh + "\n" + bootstrap;
        string[] forbidden =
        [
            "ProcessStartInfo",
            "Process.Start",
            "MediaNormalizer",
            "BroadcastFfmpeg",
            "StationSupervisor",
            "RollingProgrammingPlanner",
            "RollingStationCoordinator",
            "systemctl",
            "RTMP",
            "File.Delete",
            "File.Move",
            "File.WriteAll",
        ];

        foreach (string value in forbidden)
        {
            Assert.DoesNotContain(value, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void B2CAddsNoDirectDashboardRefreshCompositionOrDeploymentSurface()
    {
        string root = FindRepositoryRoot();
        string dashboard = string.Join('\n', Directory.EnumerateFiles(
                Path.Combine(root, "src", "NzyteTv.Dashboard"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !IsGenerated(path))
            .Select(File.ReadAllText));
        string operations = string.Join('\n', Directory.EnumerateFiles(
                Path.Combine(root, "src", "NzyteTv.Operations"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !IsGenerated(path))
            .Select(File.ReadAllText));

        Assert.DoesNotContain("MediaLibraryRefreshService", dashboard, StringComparison.Ordinal);
        Assert.Contains("MediaLibraryRefreshService", operations, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaMetadataBootstrapService", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaMetadataBootstrapService", operations, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, "deploy", "checkpoint-3b3-b2")));
    }

    private static bool IsGenerated(string path) => path.Contains(
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        StringComparison.OrdinalIgnoreCase);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "NzyteTv.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
