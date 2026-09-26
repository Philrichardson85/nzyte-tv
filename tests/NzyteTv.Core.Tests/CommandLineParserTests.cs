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
