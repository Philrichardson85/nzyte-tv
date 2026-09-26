using NzyteTv.Core;

namespace NzyteTv.Core.Tests;

public sealed class MediaPathPolicyTests
{
    [Fact]
    public void GetDefaultOutputPath_UsesBroadcastReadyAndMp4Extension()
    {
        string workingDirectory = Path.GetFullPath(Path.Combine("work", "root"));

        string result = MediaPathPolicy.GetDefaultOutputPath(Path.Combine("input", "My Clip.mov"), workingDirectory);

        Assert.Equal(Path.Combine(workingDirectory, "BroadcastReady", "My Clip.mp4"), result);
    }

    [Fact]
    public void EnsureOutputIsAllowed_ExistingDestinationWithoutOverwrite_Throws()
    {
        string directory = Directory.CreateTempSubdirectory("nzytetv-path-").FullName;
        try
        {
            string source = Path.Combine(directory, "source.mov");
            string output = Path.Combine(directory, "output.mp4");
            File.WriteAllText(source, "source");
            File.WriteAllText(output, "existing");

            IOException exception = Assert.Throws<IOException>(
                () => MediaPathPolicy.EnsureOutputIsAllowed(source, output, overwrite: false));

            Assert.Contains("--overwrite", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EnsureOutputIsAllowed_SourceAndDestinationMatch_AlwaysThrows()
    {
        string path = Path.GetFullPath("same.mp4");

        Assert.Throws<InvalidOperationException>(
            () => MediaPathPolicy.EnsureOutputIsAllowed(path, path, overwrite: true));
    }
}
