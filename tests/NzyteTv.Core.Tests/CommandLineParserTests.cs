using NzyteTv.Cli;
using NzyteTv.Core;

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
    [InlineData("normalize", "portrait.mp4")]
    [InlineData("normalize-library", "source", "library")]
    public void Parse_NormalizeCommandsAcceptBlurredBackgroundLayout(
        string command,
        params string[] paths)
    {
        string[] arguments = [command, .. paths, "--vertical-layout", "blurred-background"];

        CommandParseResult result = CommandLineParser.Parse(arguments);

        Assert.True(result.IsSuccess);
        Assert.Equal(VerticalLayoutMode.BlurredBackground, result.Command!.VerticalLayout);
    }

    [Fact]
    public void Parse_NormalizeRejectsUnsupportedVerticalLayout()
    {
        CommandParseResult result = CommandLineParser.Parse([
            "normalize", "portrait.mp4", "--vertical-layout", "pillarbox",
        ]);

        Assert.False(result.IsSuccess);
        Assert.Contains("blurred-background", result.Error, StringComparison.Ordinal);
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

    [Fact]
    public void Parse_MediaInitReturnsPortableRoot()
    {
        CommandParseResult result = CommandLineParser.Parse(["media", "init", "E:\\"]);

        Assert.True(result.IsSuccess);
        Assert.Equal(CommandKind.MediaInit, result.Command!.Kind);
        Assert.Equal("E:\\", result.Command.Input);
    }

    [Theory]
    [InlineData("--help", CommandKind.MediaHelp)]
    [InlineData("init --help", CommandKind.MediaInit)]
    public void Parse_MediaHelpCommandsAreSupported(string arguments, CommandKind expectedKind)
    {
        CommandParseResult result = CommandLineParser.Parse([
            "media",
            .. arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries),
        ]);

        Assert.True(result.IsSuccess);
        Assert.True(result.Command!.ShowHelp);
        Assert.Equal(expectedKind, result.Command.Kind);
    }

    [Fact]
    public void Parse_BuildPlaylistReturnsAllSchedulingOptions()
    {
        CommandParseResult result = CommandLineParser.Parse([
            "build-playlist",
            "/srv/nzyte-tv/library",
            "--catalog",
            "/srv/nzyte-tv/catalog/song-catalog.json",
            "--output",
            "/srv/nzyte-tv/playlists/current.json",
            "--duration",
            "6h",
            "--seed",
            "20260927",
            "--history",
            "/srv/nzyte-tv/playlists/history.json",
            "--dry-run",
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal(CommandKind.BuildPlaylist, result.Command!.Kind);
        Assert.Equal("/srv/nzyte-tv/library", result.Command.Input);
        Assert.Equal("/srv/nzyte-tv/catalog/song-catalog.json", result.Command.CatalogPath);
        Assert.Equal("/srv/nzyte-tv/playlists/current.json", result.Command.OutputPath);
        Assert.Equal(TimeSpan.FromHours(6), result.Command.TargetDuration);
        Assert.Equal(20260927, result.Command.Seed);
        Assert.Equal("/srv/nzyte-tv/playlists/history.json", result.Command.HistoryPath);
        Assert.True(result.Command.DryRun);
    }

    [Theory]
    [InlineData("0h")]
    [InlineData("forever")]
    [InlineData("-1h")]
    public void Parse_BuildPlaylistRejectsInvalidDuration(string duration)
    {
        CommandParseResult result = CommandLineParser.Parse([
            "build-playlist", "library", "--catalog", "catalog.json",
            "--output", "playlist.json", "--duration", duration,
        ]);

        Assert.False(result.IsSuccess);
        Assert.Contains("duration", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_BuildPlaylistRequiresCatalogOutputAndDuration()
    {
        CommandParseResult result = CommandLineParser.Parse(["build-playlist", "library"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("--catalog", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MetadataInitialize_ReturnsRootsCatalogAndDryRun()
    {
        CommandParseResult result = CommandLineParser.Parse([
            "metadata",
            "initialize",
            "/srv/nzyte-tv/source",
            "/srv/nzyte-tv/library",
            "--catalog",
            "/srv/nzyte-tv/catalog/songs.json",
            "--dry-run",
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal(CommandKind.MetadataInitialize, result.Command!.Kind);
        Assert.Equal("/srv/nzyte-tv/source", result.Command.Input);
        Assert.Equal("/srv/nzyte-tv/library", result.Command.Destination);
        Assert.Equal("/srv/nzyte-tv/catalog/songs.json", result.Command.CatalogPath);
        Assert.True(result.Command.DryRun);
    }

    [Fact]
    public void Parse_MetadataReview_RequiresCatalog()
    {
        CommandParseResult result = CommandLineParser.Parse([
            "metadata",
            "review",
            "source",
            "library",
        ]);

        Assert.False(result.IsSuccess);
        Assert.Contains("--catalog", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MetadataSync_DoesNotRequireCatalog()
    {
        CommandParseResult result = CommandLineParser.Parse([
            "metadata",
            "sync",
            "source",
            "library",
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal(CommandKind.MetadataSync, result.Command!.Kind);
    }

    [Fact]
    public void Parse_MetadataRebind_ReturnsBothSourcePaths()
    {
        CommandParseResult result = CommandLineParser.Parse([
            "metadata",
            "rebind",
            "old source.mp4",
            "new source.mp4",
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal(CommandKind.MetadataRebind, result.Command!.Kind);
        Assert.Equal("old source.mp4", result.Command.Input);
        Assert.Equal("new source.mp4", result.Command.Destination);
    }

    [Fact]
    public void Parse_MetadataEdit_ReturnsTypeAndExtensibleSubtype()
    {
        CommandParseResult result = CommandLineParser.Parse([
            "metadata",
            "edit",
            "source file.mp4",
            "--type",
            "short-form",
            "--subtype",
            "vertical-performance",
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal(CommandKind.MetadataEdit, result.Command!.Kind);
        Assert.Equal("source file.mp4", result.Command.Input);
        Assert.Equal("short-form", result.Command.MetadataType);
        Assert.Equal("vertical-performance", result.Command.MetadataSubtype);
    }

    [Theory]
    [InlineData("lipsync")]
    [InlineData("mic-drop")]
    public void Parse_MetadataEdit_AcceptsPerformanceSubtype(string subtype)
    {
        CommandParseResult result = CommandLineParser.Parse([
            "metadata",
            "edit",
            "source file.mp4",
            "--type",
            AssetTypes.Performance,
            "--subtype",
            subtype,
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal(AssetTypes.Performance, result.Command!.MetadataType);
        Assert.Equal(subtype, result.Command.MetadataSubtype);
    }

    [Fact]
    public void Parse_MetadataEdit_RejectsSubtypeForUnsupportedType()
    {
        CommandParseResult result = CommandLineParser.Parse([
            "metadata",
            "edit",
            "source file.mp4",
            "--type",
            AssetTypes.Vlog,
            "--subtype",
            "lipsync",
        ]);

        Assert.False(result.IsSuccess);
        Assert.Contains("short-form or performance", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MetadataEditWithoutType_IsRejected()
    {
        CommandParseResult result = CommandLineParser.Parse(["metadata", "edit", "source.mp4"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("--type", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AssetTypes.Visualizer)]
    [InlineData(AssetTypes.AnimatedVisual)]
    public void Parse_MetadataEdit_AcceptsNewSongTypes(string type)
    {
        CommandParseResult result = CommandLineParser.Parse([
            "metadata",
            "edit",
            "source.mp4",
            "--type",
            type,
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal(type, result.Command!.MetadataType);
    }

    [Fact]
    public void Parse_DryRunOnMetadataReview_IsRejected()
    {
        CommandParseResult result = CommandLineParser.Parse([
            "metadata",
            "review",
            "source",
            "library",
            "--catalog",
            "songs.json",
            "--dry-run",
        ]);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Parse_MetadataCatalogOptionWithoutValue_IsRejected()
    {
        CommandParseResult result = CommandLineParser.Parse([
            "metadata",
            "initialize",
            "source",
            "library",
            "--catalog",
            "--dry-run",
        ]);

        Assert.False(result.IsSuccess);
        Assert.Contains("catalog path", result.Error, StringComparison.Ordinal);
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
