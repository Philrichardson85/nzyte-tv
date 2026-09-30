using NzyteTv.Cli;

namespace NzyteTv.Core.Tests;

public sealed class ProgrammingCommandLineTests
{
    [Theory]
    [InlineData("init", CommandKind.ProgrammingInit)]
    [InlineData("validate", CommandKind.ProgrammingValidate)]
    [InlineData("status", CommandKind.ProgrammingStatus)]
    public void Parse_BaseProgrammingCommandsRequireMediaRoot(string verb, CommandKind expected)
    {
        CommandParseResult result = CommandLineParser.Parse(
            ["programming", verb, "--media-root", "/srv/nzyte-tv/media"]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, result.Command!.Kind);
        Assert.Equal("/srv/nzyte-tv/media", result.Command.ProgrammingMediaRoot);
    }

    [Fact]
    public void Parse_CampaignSetReturnsIdentityAndWeight()
    {
        CommandParseResult result = CommandLineParser.Parse(
        [
            "programming",
            "campaign",
            "set",
            "free-fallin",
            "--media-root",
            "/media",
            "--weight",
            "2.5",
        ]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(CommandKind.ProgrammingCampaignSet, result.Command!.Kind);
        Assert.Equal("free-fallin", result.Command.ProgrammingContentGroupId);
        Assert.Equal(2.5, result.Command.ProgrammingWeightMultiplier);
    }

    [Fact]
    public void Parse_CampaignClearNeedsNoIdentity()
    {
        CommandParseResult result = CommandLineParser.Parse(
            ["programming", "campaign", "clear", "--media-root", "/media"]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(CommandKind.ProgrammingCampaignClear, result.Command!.Kind);
        Assert.Null(result.Command.ProgrammingContentGroupId);
    }

    [Fact]
    public void Parse_AssetSetReturnsSparseEditorialValues()
    {
        CommandParseResult result = CommandLineParser.Parse(
        [
            "programming",
            "asset",
            "set",
            "free-fallin-video",
            "--media-root",
            "/media",
            "--do-not-air",
            "true",
            "--weight",
            "1.25",
        ]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(CommandKind.ProgrammingAssetSet, result.Command!.Kind);
        Assert.Equal("free-fallin-video", result.Command.ProgrammingAssetId);
        Assert.True(result.Command.ProgrammingDoNotAir);
        Assert.Equal(1.25, result.Command.ProgrammingWeightMultiplier);
    }

    [Fact]
    public void Parse_AssetResetReturnsAssetIdentity()
    {
        CommandParseResult result = CommandLineParser.Parse(
            ["programming", "asset", "reset", "asset-one", "--media-root", "/media"]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(CommandKind.ProgrammingAssetReset, result.Command!.Kind);
        Assert.Equal("asset-one", result.Command.ProgrammingAssetId);
    }

    [Theory]
    [InlineData("programming --help", CommandKind.ProgrammingHelp)]
    [InlineData("programming init --help", CommandKind.ProgrammingInit)]
    [InlineData("programming validate --help", CommandKind.ProgrammingValidate)]
    [InlineData("programming status --help", CommandKind.ProgrammingStatus)]
    [InlineData("programming campaign --help", CommandKind.ProgrammingCampaignHelp)]
    [InlineData("programming campaign set --help", CommandKind.ProgrammingCampaignSet)]
    [InlineData("programming campaign clear --help", CommandKind.ProgrammingCampaignClear)]
    [InlineData("programming asset --help", CommandKind.ProgrammingAssetHelp)]
    [InlineData("programming asset set --help", CommandKind.ProgrammingAssetSet)]
    [InlineData("programming asset reset --help", CommandKind.ProgrammingAssetReset)]
    public void Parse_ProgrammingHelpIsSupported(string command, CommandKind kind)
    {
        CommandParseResult result = CommandLineParser.Parse(command.Split(' '));

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.Command!.ShowHelp);
        Assert.Equal(kind, result.Command.Kind);
    }

    [Theory]
    [InlineData("programming status")]
    [InlineData("programming asset set asset --media-root /media")]
    [InlineData("programming asset set asset --media-root /media --do-not-air maybe")]
    [InlineData("programming campaign set song --media-root /media --weight NaN")]
    public void Parse_InvalidProgrammingArgumentsAreRejected(string command)
    {
        CommandParseResult result = CommandLineParser.Parse(command.Split(' '));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }
}
