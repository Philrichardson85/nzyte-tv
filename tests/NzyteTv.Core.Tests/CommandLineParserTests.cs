using NzyteTv.Cli;

namespace NzyteTv.Core.Tests;

public sealed class CommandLineParserTests
{
    [Fact]
    public void Parse_NormalizeWithOverwrite_ReturnsCommand()
    {
        CommandParseResult result = CommandLineParser.Parse(["normalize", "clip with spaces.mov", "--overwrite"]);

        Assert.True(result.IsSuccess);
        Assert.Equal(CommandKind.Normalize, result.Command!.Kind);
        Assert.Equal("clip with spaces.mov", result.Command.Input);
        Assert.True(result.Command.Overwrite);
    }

    [Fact]
    public void Parse_NormalizeLibrary_ReturnsBothRootsAndOverwrite()
    {
        CommandParseResult result = CommandLineParser.Parse([
            "normalize-library",
            "/srv/nzyte-tv/media",
            "/srv/nzyte-tv/work/BroadcastReady",
            "--overwrite",
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal(CommandKind.NormalizeLibrary, result.Command!.Kind);
        Assert.Equal("/srv/nzyte-tv/media", result.Command.Input);
        Assert.Equal("/srv/nzyte-tv/work/BroadcastReady", result.Command.Destination);
        Assert.True(result.Command.Overwrite);
    }

    [Theory]
    [MemberData(nameof(InvalidLibraryArguments))]
    public void Parse_NormalizeLibraryWithWrongRootCount_ReturnsUsageError(string[] arguments)
    {
        CommandParseResult result = CommandLineParser.Parse(arguments);

        Assert.False(result.IsSuccess);
        Assert.Contains("source root and a destination root", result.Error, StringComparison.Ordinal);
    }

    public static TheoryData<string[]> InvalidLibraryArguments => new()
    {
        { ["normalize-library", "source"] },
        { ["normalize-library", "source", "destination", "extra"] },
    };

    [Theory]
    [InlineData("inspect")]
    [InlineData("verify")]
    [InlineData("normalize")]
    public void Parse_MissingInput_ReturnsUsageError(string command)
    {
        CommandParseResult result = CommandLineParser.Parse([command]);

        Assert.False(result.IsSuccess);
        Assert.Contains("exactly one", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_OverwriteOnInspect_ReturnsUsageError()
    {
        CommandParseResult result = CommandLineParser.Parse(["inspect", "clip.mp4", "--overwrite"]);

        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Parse_RootHelp_ReturnsHelp(string option)
    {
        CommandParseResult result = CommandLineParser.Parse([option]);

        Assert.True(result.Command!.ShowHelp);
        Assert.Equal(CommandKind.RootHelp, result.Command.Kind);
    }
}
