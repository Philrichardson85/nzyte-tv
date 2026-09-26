using NzyteTv.Core;

namespace NzyteTv.Core.Tests;

public sealed class LibraryPathPolicyTests
{
    [Fact]
    public void EnsureRootsDoNotOverlap_SeparateRoots_Succeeds()
    {
        string parent = Path.GetFullPath("library-policy");

        LibraryPathPolicy.EnsureRootsDoNotOverlap(
            Path.Combine(parent, "source"),
            Path.Combine(parent, "destination"));
    }

    [Theory]
    [InlineData("same", "same")]
    [InlineData("source", "source/output")]
    [InlineData("destination/input", "destination")]
    public void EnsureRootsDoNotOverlap_OverlappingRoots_Throws(string source, string destination)
    {
        string parent = Path.GetFullPath("library-policy");

        Assert.Throws<InvalidOperationException>(() => LibraryPathPolicy.EnsureRootsDoNotOverlap(
            Path.Combine(parent, source),
            Path.Combine(parent, destination)));
    }

    [Fact]
    public void GetDestinationPath_PreservesRelativeFoldersAndChangesExtensionToMp4()
    {
        string sourceRoot = Path.GetFullPath(Path.Combine("library", "source"));
        string destinationRoot = Path.GetFullPath(Path.Combine("library", "ready"));
        string source = Path.Combine(sourceRoot, "Vlog Episodes", "Episode One.MKV");

        string destination = LibraryPathPolicy.GetDestinationPath(sourceRoot, destinationRoot, source);

        Assert.Equal(Path.Combine(destinationRoot, "Vlog Episodes", "Episode One.mp4"), destination);
    }

    [Fact]
    public void GetDestinationPath_SourceOutsideRoot_Throws()
    {
        string parent = Path.GetFullPath("library");

        Assert.Throws<InvalidOperationException>(() => LibraryPathPolicy.GetDestinationPath(
            Path.Combine(parent, "source"),
            Path.Combine(parent, "ready"),
            Path.Combine(parent, "other", "clip.mp4")));
    }
}
