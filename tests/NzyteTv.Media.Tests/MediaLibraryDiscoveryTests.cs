using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class MediaLibraryDiscoveryTests
{
    [Fact]
    public void Discover_RecursesFiltersAndPreservesCategoryPaths()
    {
        string directory = Directory.CreateTempSubdirectory("nzytetv-discovery-").FullName;
        try
        {
            string source = Path.Combine(directory, "Media Library");
            string destination = Path.Combine(directory, "Broadcast Ready");
            CreateFile(source, "Music Videos", "Lady Lady.mp4");
            CreateFile(source, "Vlog Episodes", "Episode One.MOV");
            CreateFile(source, "Specials", "Concert.mKv");
            CreateFile(source, "Music Videos", "cover.jpg");
            CreateFile(source, "notes.txt");
            CreateFile(source, "archive.avi");
            CreateFile(source, "System Volume Information", "metadata.mp4");

            IReadOnlyList<LibraryMediaFile> result = new MediaLibraryDiscovery().Discover(source, destination);

            Assert.Equal(3, result.Count);
            Assert.Contains(result, file => file.SourcePath.EndsWith(
                Path.Combine("Music Videos", "Lady Lady.mp4"),
                StringComparison.Ordinal));
            Assert.Contains(result, file => file.SourcePath.EndsWith(
                Path.Combine("Vlog Episodes", "Episode One.MOV"),
                StringComparison.Ordinal));
            Assert.Contains(result, file => file.SourcePath.EndsWith(
                Path.Combine("Specials", "Concert.mKv"),
                StringComparison.Ordinal));
            Assert.DoesNotContain(result, file => file.SourcePath.Contains(
                "System Volume Information",
                StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result, file => file.DestinationPath == Path.Combine(
                destination,
                "Music Videos",
                "Lady Lady.mp4"));
            Assert.Contains(result, file => file.DestinationPath == Path.Combine(
                destination,
                "Vlog Episodes",
                "Episode One.mp4"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Discover_OverlappingRoots_ThrowsBeforeScanning()
    {
        string directory = Directory.CreateTempSubdirectory("nzytetv-discovery-").FullName;
        try
        {
            Assert.Throws<InvalidOperationException>(() => new MediaLibraryDiscovery().Discover(
                directory,
                Path.Combine(directory, "BroadcastReady")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Discover_MissingSourceRoot_ThrowsClearError()
    {
        string directory = Directory.CreateTempSubdirectory("nzytetv-discovery-").FullName;
        try
        {
            string missing = Path.Combine(directory, "missing");

            DirectoryNotFoundException exception = Assert.Throws<DirectoryNotFoundException>(() =>
                new MediaLibraryDiscovery().Discover(missing, Path.Combine(directory, "ready")));

            Assert.Contains(Path.GetFullPath(missing), exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CreateFile(string root, params string[] pathParts)
    {
        string path = pathParts.Aggregate(root, Path.Combine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test");
    }
}
