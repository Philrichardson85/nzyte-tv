using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class RollingProgrammingValidationTests
{
    [Theory]
    [InlineData("playlist")]
    [InlineData("descriptor")]
    [InlineData("input")]
    [InlineData("history")]
    public async Task Validate_DetectsCommittedArtifactHashMismatch(string artifact)
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        (RollingProgrammingPlanner planner, RollingMaintainResult maintained) = await PrepareOneAsync(fixture);
        RollingCommittedBlock block = maintained.Manifest.Blocks!.Single();
        string relative = artifact switch
        {
            "playlist" => block.PlaylistPath!,
            "descriptor" => block.DescriptorPath!,
            "input" => block.InputSnapshotPath!,
            "history" => block.HistoryAfter!.RelativePath,
            _ => throw new InvalidOperationException(),
        };
        string path = RollingPathSafety.ResolveExistingFile(maintained.Paths.RollingRoot, relative);
        await File.AppendAllTextAsync(path, " ");

        RollingValidationResult validation = planner.Validate(fixture.Root);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("hash mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("playlist")]
    [InlineData("descriptor")]
    [InlineData("input")]
    [InlineData("history")]
    public async Task Validate_DetectsMissingCommittedArtifact(string artifact)
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        (RollingProgrammingPlanner planner, RollingMaintainResult maintained) = await PrepareOneAsync(fixture);
        RollingCommittedBlock block = maintained.Manifest.Blocks!.Single();
        string relative = artifact switch
        {
            "playlist" => block.PlaylistPath!,
            "descriptor" => block.DescriptorPath!,
            "input" => block.InputSnapshotPath!,
            "history" => block.HistoryAfter!.RelativePath,
            _ => throw new InvalidOperationException(),
        };
        File.Delete(RollingPathSafety.ResolveExistingFile(maintained.Paths.RollingRoot, relative));

        RollingValidationResult validation = planner.Validate(fixture.Root);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_DetectsParentChainMismatch()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult maintained = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 2);
        RollingCommittedBlock[] blocks = maintained.Manifest.Blocks!.ToArray();
        blocks[1] = blocks[1] with { ParentBlockId = new string('f', 64) };
        RollingProgrammingManifest broken = CloneManifest(maintained.Manifest, blocks);
        await new AtomicTextFileWriter().WriteAsync(
            maintained.Paths.ManifestPath,
            RollingProgrammingJson.Serialize(broken),
            CancellationToken.None);

        RollingValidationResult validation = planner.Validate(fixture.Root);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("chain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_RejectsRelativePathEscape()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        (RollingProgrammingPlanner planner, RollingMaintainResult maintained) = await PrepareOneAsync(fixture);
        RollingCommittedBlock block = maintained.Manifest.Blocks!.Single() with
        {
            PlaylistPath = "../outside.json",
        };
        RollingProgrammingManifest broken = CloneManifest(maintained.Manifest, [block]);
        await new AtomicTextFileWriter().WriteAsync(
            maintained.Paths.ManifestPath,
            RollingProgrammingJson.Serialize(broken),
            CancellationToken.None);

        RollingValidationResult validation = planner.Validate(fixture.Root);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("escapes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_RejectsSymlinkEscapeWhenPlatformSupportsLinks()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        (RollingProgrammingPlanner planner, RollingMaintainResult maintained) = await PrepareOneAsync(fixture);
        RollingCommittedBlock original = maintained.Manifest.Blocks!.Single();
        string outside = Path.Combine(Path.GetDirectoryName(fixture.Root)!, $"outside-{Guid.NewGuid():N}.json");
        string linked = Path.Combine(maintained.Paths.RollingRoot, "linked-playlist.json");
        try
        {
            File.Copy(
                RollingPathSafety.ResolveExistingFile(maintained.Paths.RollingRoot, original.PlaylistPath!),
                outside);
            try
            {
                File.CreateSymbolicLink(linked, outside);
            }
            catch (Exception exception) when (exception is
                UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            RollingCommittedBlock block = original with
            {
                PlaylistPath = "linked-playlist.json",
                PlaylistSha256 = RollingProgrammingJson.Sha256File(outside),
            };
            await new AtomicTextFileWriter().WriteAsync(
                maintained.Paths.ManifestPath,
                RollingProgrammingJson.Serialize(CloneManifest(maintained.Manifest, [block])),
                CancellationToken.None);

            RollingValidationResult validation = planner.Validate(fixture.Root);

            Assert.False(validation.IsValid);
            Assert.Contains(validation.Errors, error => error.Contains("escapes", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(linked)) File.Delete(linked);
            if (File.Exists(outside)) File.Delete(outside);
        }
    }

    [Fact]
    public async Task Validate_RejectsMalformedManifest()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        RollingInitializationResult initialized = await fixture.InitializeAsync(planner);
        await File.WriteAllTextAsync(initialized.Paths.ManifestPath, "{ malformed }");

        RollingValidationResult validation = planner.Validate(fixture.Root);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("Malformed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_RejectsUnsupportedManifestSchema()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        RollingInitializationResult initialized = await fixture.InitializeAsync(planner);
        string manifest = File.ReadAllText(initialized.Paths.ManifestPath)
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal);
        await File.WriteAllTextAsync(initialized.Paths.ManifestPath, manifest);

        RollingValidationResult validation = planner.Validate(fixture.Root);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("Unsupported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_RejectsDuplicateManifestProperties()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        RollingInitializationResult initialized = await fixture.InitializeAsync(planner);
        string manifest = File.ReadAllText(initialized.Paths.ManifestPath)
            .Replace("{", "{\"schemaVersion\":1,", StringComparison.Ordinal);
        await File.WriteAllTextAsync(initialized.Paths.ManifestPath, manifest);

        RollingValidationResult validation = planner.Validate(fixture.Root);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_ReportsStagingAndQuarantineAsWarningsWithoutClaimingThemCommitted()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        RollingInitializationResult initialized = await fixture.InitializeAsync(planner);
        Directory.CreateDirectory(Path.Combine(initialized.Paths.StagingDirectory, "pending"));
        Directory.CreateDirectory(Path.Combine(initialized.Paths.OrphanedDirectory, "review"));

        RollingValidationResult validation = planner.Validate(fixture.Root);

        Assert.True(validation.IsValid);
        Assert.Equal(2, validation.Warnings.Count);
        Assert.Empty(validation.Manifest!.Blocks!);
    }

    [Fact]
    public void PathSafety_RejectsRootedAndTraversalPaths()
    {
        string root = Directory.CreateTempSubdirectory("nzytetv-path-safety-").FullName;
        try
        {
            Assert.Throws<InvalidDataException>(() => RollingPathSafety.Resolve(root, "../escape.json"));
            Assert.Throws<InvalidDataException>(() => RollingPathSafety.Resolve(root, Path.GetFullPath("absolute.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(RollingProgrammingPlanner Planner, RollingMaintainResult Result)> PrepareOneAsync(
        RollingLibraryFixture fixture)
    {
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult maintained = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        return (planner, maintained);
    }

    private static RollingProgrammingManifest CloneManifest(
        RollingProgrammingManifest source,
        IReadOnlyList<RollingCommittedBlock> blocks) => new()
        {
            SchemaVersion = source.SchemaVersion,
            PlannerId = source.PlannerId,
            BaseSeed = source.BaseSeed,
            TargetBlockDurationSeconds = source.TargetBlockDurationSeconds,
            TargetPreparedBlockCount = source.TargetPreparedBlockCount,
            NextSequence = source.NextSequence,
            GenesisHistory = source.GenesisHistory,
            HistoryHead = source.HistoryHead,
            Blocks = blocks,
            InitializedAtUtc = source.InitializedAtUtc,
        };
}
