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

    [Theory]
    [InlineData("init", CommandKind.ProgrammingRollingInit)]
    [InlineData("maintain", CommandKind.ProgrammingRollingMaintain)]
    [InlineData("validate", CommandKind.ProgrammingRollingValidate)]
    [InlineData("status", CommandKind.ProgrammingRollingStatus)]
    public void Parse_RollingCommandsRequireMediaRoot(string verb, CommandKind expected)
    {
        CommandParseResult result = CommandLineParser.Parse(
            ["programming", "rolling", verb, "--media-root", "/media"]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, result.Command!.Kind);
        Assert.Equal("/media", result.Command.ProgrammingMediaRoot);
    }

    [Fact]
    public void Parse_RollingInitAcceptsExplicitGenesisAndSignedBaseSeed()
    {
        CommandParseResult result = CommandLineParser.Parse(
        [
            "programming", "rolling", "init",
            "--media-root", "/media",
            "--history", "/planned/history.json",
            "--base-seed", "-123",
        ]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("/planned/history.json", result.Command!.RollingHistoryPath);
        Assert.Equal(-123, result.Command.RollingBaseSeed);
    }

    [Theory]
    [InlineData("3m", 180)]
    [InlineData("4m", 240)]
    [InlineData("5m", 300)]
    [InlineData("60s", 60)]
    [InlineData("1800s", 1800)]
    public void Parse_RollingInitAcceptsWholeSecondTestDurations(
        string value,
        int expectedSeconds)
    {
        CommandParseResult result = CommandLineParser.Parse(
        [
            "programming", "rolling", "init",
            "--media-root", "/isolated-media",
            "--base-seed", "42",
            "--test-block-duration", value,
        ]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expectedSeconds, result.Command!.RollingTestBlockDuration?.TotalSeconds);
    }

    [Theory]
    [InlineData("0s")]
    [InlineData("-1s")]
    [InlineData("59s")]
    [InlineData("60.5s")]
    [InlineData("1.5m")]
    [InlineData("1800.5s")]
    [InlineData("1801s")]
    [InlineData("4")]
    [InlineData("four-minutes")]
    public void Parse_RollingInitRejectsInvalidTestDurations(string value)
    {
        CommandParseResult result = CommandLineParser.Parse(
        [
            "programming", "rolling", "init",
            "--media-root", "/isolated-media",
            "--test-block-duration", value,
        ]);

        Assert.False(result.IsSuccess);
        Assert.Contains("whole-second duration", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("maintain")]
    [InlineData("validate")]
    [InlineData("status")]
    public void Parse_TestDurationIsExclusiveToRollingInitialization(string verb)
    {
        CommandParseResult result = CommandLineParser.Parse(
        [
            "programming", "rolling", verb,
            "--media-root", "/isolated-media",
            "--test-block-duration", "4m",
        ]);

        Assert.False(result.IsSuccess);
        Assert.Contains("valid only for programming rolling init", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_TestDurationIsRejectedByUnrelatedCommands()
    {
        CommandParseResult result = CommandLineParser.Parse(
        ["programming", "status", "--media-root", "/media", "--test-block-duration", "4m"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("Unknown option", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RollingInitRejectsDuplicateTestDuration()
    {
        CommandParseResult result = CommandLineParser.Parse(
        [
            "programming", "rolling", "init",
            "--media-root", "/isolated-media",
            "--test-block-duration", "4m",
            "--test-block-duration", "240s",
        ]);

        Assert.False(result.IsSuccess);
        Assert.Contains("only once", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("programming rolling --help", CommandKind.ProgrammingRollingHelp)]
    [InlineData("programming rolling init --help", CommandKind.ProgrammingRollingInit)]
    [InlineData("programming rolling maintain --help", CommandKind.ProgrammingRollingMaintain)]
    [InlineData("programming rolling validate --help", CommandKind.ProgrammingRollingValidate)]
    [InlineData("programming rolling status --help", CommandKind.ProgrammingRollingStatus)]
    public void Parse_RollingHelpIsSupported(string command, CommandKind kind)
    {
        CommandParseResult result = CommandLineParser.Parse(command.Split(' '));

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.Command!.ShowHelp);
        Assert.Equal(kind, result.Command.Kind);
    }

    [Theory]
    [InlineData("programming rolling maintain")]
    [InlineData("programming rolling init --media-root /media --base-seed nope")]
    [InlineData("programming rolling maintain --media-root /media --history h.json")]
    [InlineData("programming rolling status --media-root /media --base-seed 2")]
    [InlineData("programming rolling unknown --media-root /media")]
    public void Parse_InvalidRollingArgumentsAreRejected(string command)
    {
        CommandParseResult result = CommandLineParser.Parse(command.Split(' '));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }
}
