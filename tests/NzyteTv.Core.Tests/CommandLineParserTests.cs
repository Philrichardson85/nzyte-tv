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

    [Fact]
    public void Parse_MetadataEditWithoutType_IsRejected()
    {
        CommandParseResult result = CommandLineParser.Parse(["metadata", "edit", "source.mp4"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("--type", result.Error, StringComparison.Ordinal);
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
