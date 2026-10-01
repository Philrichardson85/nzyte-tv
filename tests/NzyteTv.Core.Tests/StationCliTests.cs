using NzyteTv.Cli;
using NzyteTv.Core;

namespace NzyteTv.Core.Tests;

public sealed class StationCliTests
{
    [Theory]
    [InlineData("validate", CommandKind.StationRollingValidate)]
    [InlineData("status", CommandKind.StationRollingStatus)]
    [InlineData("run", CommandKind.StationRollingRun)]
    public void Parse_RollingStationCommandsRequireSeparateConfiguration(
        string verb,
        CommandKind expected)
    {
        CommandParseResult result = CommandLineParser.Parse(
            ["station", "rolling", verb, "--config", "/etc/nzyte-tv/rolling-station.json"]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, result.Command!.Kind);
        Assert.Equal("/etc/nzyte-tv/rolling-station.json", result.Command.ConfigPath);
    }

    [Fact]
    public void Parse_RollingRunAcceptsExplicitStoppedStaticCutoverOnlyOnRun()
    {
        CommandParseResult run = CommandLineParser.Parse(
        [
            "station", "rolling", "run",
            "--config", "/etc/nzyte-tv/rolling-station.json",
            "--accept-stopped-static-cutover",
        ]);
        CommandParseResult status = CommandLineParser.Parse(
        [
            "station", "rolling", "status",
            "--config", "/etc/nzyte-tv/rolling-station.json",
            "--accept-stopped-static-cutover",
        ]);

        Assert.True(run.IsSuccess, run.Error);
        Assert.True(run.Command!.AcceptStoppedStaticCutover);
        Assert.False(status.IsSuccess);
    }

    [Theory]
    [InlineData("station rolling --help", CommandKind.StationRollingHelp)]
    [InlineData("station rolling validate --help", CommandKind.StationRollingValidate)]
    [InlineData("station rolling status --help", CommandKind.StationRollingStatus)]
    [InlineData("station rolling run --help", CommandKind.StationRollingRun)]
    public void Parse_RollingStationHelpForms(string text, CommandKind expected)
    {
        CommandParseResult result = CommandLineParser.Parse(text.Split(' '));

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.Command!.ShowHelp);
        Assert.Equal(expected, result.Command.Kind);
    }

    [Theory]
    [InlineData("station rolling validate")]
    [InlineData("station rolling unknown --config c.json")]
    [InlineData("station rolling run --config c.json --bogus")]
    public void Parse_InvalidRollingStationArgumentsAreRejected(string text)
    {
        CommandParseResult result = CommandLineParser.Parse(text.Split(' '));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Run_MissingStationConfigurationUsesPermanentStartupExitCode()
    {
        string missingPath = Path.Combine(
            Path.GetTempPath(),
            $"nzytetv-missing-station-{Guid.NewGuid():N}.json");

        int exitCode = await CliApplication.RunAsync(
            ["station", "run", "--config", missingPath],
            CancellationToken.None);

        Assert.Equal(StationExitCodes.PermanentStartupFailure, exitCode);
    }
}
