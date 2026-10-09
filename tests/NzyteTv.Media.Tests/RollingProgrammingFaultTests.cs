using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class RollingProgrammingFaultTests
{
    [Fact]
    public async Task CrashBeforeIntent_ResnapshotsAndCompletesNormally()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        var fault = new ThrowOnceFault(RollingPlannerCheckpoint.BeforeIntent, 1);
        RollingProgrammingPlanner planner = fixture.CreatePlanner(fault);
        await fixture.InitializeAsync(planner);

        await Assert.ThrowsAsync<InjectedRollingFailure>(() => planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1));
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(fixture.Root);
        Assert.False(File.Exists(Path.Combine(paths.StagingDirectory, "000000000001", "intent.json")));

        RollingMaintainResult recovered = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        Assert.Single(recovered.Manifest.Blocks!);
        Assert.Equal(1, recovered.GeneratedBlockCount);
    }

    [Fact]
    public async Task CrashAfterIntent_RetriesSameFrozenSeedTimestampAndPolicySnapshot()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        var fault = new ThrowOnceFault(RollingPlannerCheckpoint.AfterIntent, 1);
        RollingProgrammingPlanner planner = fixture.CreatePlanner(fault);
        await fixture.InitializeAsync(planner);

        await Assert.ThrowsAsync<InjectedRollingFailure>(() => planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1));
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(fixture.Root);
        string intentPath = Path.Combine(paths.StagingDirectory, "000000000001", "intent.json");
        RollingGenerationIntent intent = RollingProgrammingJson.Deserialize<RollingGenerationIntent>(
            File.ReadAllText(intentPath),
            "test intent");
        ProgrammingConfiguration before = new ProgrammingConfigurationStore().Load(fixture.ProgrammingPath);
        await new ProgrammingConfigurationStore().WriteAsync(
            fixture.ProgrammingPath,
            before with { Revision = before.Revision + 1 },
            CancellationToken.None);

        RollingMaintainResult recovered = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        RollingCommittedBlock block = recovered.Manifest.Blocks!.Single();

        Assert.Equal(intent.Seed, block.Seed);
        Assert.Equal(intent.GeneratedAtUtc, block.GeneratedAtUtc);
        Assert.Equal(before.Revision, block.ProgrammingRevision);
    }

    [Fact]
    public async Task LegacyDurableIntent_RetryAfterExternalActivationKeepsAdjacentAuthority()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        var fault = new ThrowOnceFault(RollingPlannerCheckpoint.AfterIntent, 1);
        RollingProgrammingPlanner adjacentPlanner = fixture.CreatePlanner(fault);
        await fixture.InitializeAsync(adjacentPlanner);

        await Assert.ThrowsAsync<InjectedRollingFailure>(() => adjacentPlanner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1));
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(fixture.Root);
        RollingPlanningInputSnapshot frozen = RollingProgrammingJson.Deserialize<RollingPlanningInputSnapshot>(
            File.ReadAllText(Path.Combine(paths.StagingDirectory, "000000000001", "input.json")),
            "legacy durable-intent input");
        Assert.Null(frozen.AssetMetadataGenerationId);
        Assert.Null(frozen.AssetMetadataRevision);

        var repository = new ExternalAssetMetadataGenerationStore(
            Path.Combine(fixture.Root, "external-metadata"));
        await repository.CreateGenerationAsync("000000000001", 1, [], CancellationToken.None);
        await repository.PublishCurrentAsync("000000000001", 0, CancellationToken.None);
        await repository.CreateGenerationAsync("000000000002", 2, [], CancellationToken.None);
        await repository.PublishCurrentAsync("000000000002", 1, CancellationToken.None);
        RollingProgrammingPlanner externalPlanner = fixture.CreatePlanner(metadataRepository: repository);

        RollingMaintainResult recovered = await externalPlanner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);

        Assert.Single(recovered.Manifest.Blocks!);
        Assert.Null(fixture.ReadInput(recovered.Manifest.Blocks!.Single()).AssetMetadataGenerationId);
        Assert.Equal("000000000002", repository.ReadCurrent().GenerationId);
    }

    [Fact]
    public async Task CrashAfterIntent_TestLineageRetainsFrozenDurationSeedAndSnapshot()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        var fault = new ThrowOnceFault(RollingPlannerCheckpoint.AfterIntent, 1);
        RollingProgrammingPlanner planner = fixture.CreatePlanner(fault);
        await fixture.InitializeAsync(planner, TimeSpan.FromMinutes(4));

        await Assert.ThrowsAsync<InjectedRollingFailure>(() => planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1));
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(fixture.Root);
        string stage = Path.Combine(paths.StagingDirectory, "000000000001");
        RollingGenerationIntent frozenIntent = RollingProgrammingJson.Deserialize<RollingGenerationIntent>(
            File.ReadAllText(Path.Combine(stage, "intent.json")),
            "test frozen intent");
        string frozenInputJson = File.ReadAllText(Path.Combine(stage, "input.json"));
        RollingPlanningInputSnapshot frozenInput = RollingProgrammingJson.Deserialize<RollingPlanningInputSnapshot>(
            frozenInputJson,
            "test frozen input");

        RollingMaintainResult recovered = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        RollingCommittedBlock block = recovered.Manifest.Blocks!.Single();
        string publishedInput = File.ReadAllText(RollingPathSafety.ResolveExistingFile(
            recovered.Paths.RollingRoot,
            block.InputSnapshotPath!));

        Assert.Equal(240, frozenIntent.TargetDurationSeconds);
        Assert.Equal(240, frozenInput.TargetDurationSeconds);
        Assert.Equal(frozenIntent.Seed, block.Seed);
        Assert.Equal(frozenIntent.GeneratedAtUtc, block.GeneratedAtUtc);
        Assert.Equal(frozenInputJson, publishedInput);
        Assert.Equal(240, block.TargetDurationSeconds);
    }

    [Fact]
    public async Task CrashAfterPlaylistStage_RetryFinishesFrozenIntent()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        var fault = new ThrowOnceFault(RollingPlannerCheckpoint.AfterPlaylistStaged, 1);
        RollingProgrammingPlanner planner = fixture.CreatePlanner(fault);
        await fixture.InitializeAsync(planner);

        await Assert.ThrowsAsync<InjectedRollingFailure>(() => planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1));
        RollingMaintainResult recovered = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);

        Assert.Single(recovered.Manifest.Blocks!);
        Assert.True(planner.Validate(fixture.Root).IsValid);
    }

    [Fact]
    public async Task CrashAfterFinalArtifacts_AdoptsExactOrphanAndAdvancesHistoryAtomically()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        var fault = new ThrowOnceFault(RollingPlannerCheckpoint.AfterArtifactsPublished, 1);
        RollingProgrammingPlanner planner = fixture.CreatePlanner(fault);
        RollingInitializationResult initialized = await fixture.InitializeAsync(planner);

        await Assert.ThrowsAsync<InjectedRollingFailure>(() => planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1));
        RollingProgrammingManifest oldManifest = new RollingPlanStore().LoadManifest(initialized.Paths.ManifestPath);
        Assert.Empty(oldManifest.Blocks!);
        Assert.Equal(oldManifest.GenesisHistory, oldManifest.HistoryHead);
        Assert.Single(Directory.EnumerateDirectories(initialized.Paths.BlocksDirectory));
        RollingValidationResult pending = planner.Validate(fixture.Root);
        Assert.True(pending.IsValid);
        Assert.Contains(pending.Warnings, warning => warning.Contains("exact uncommitted", StringComparison.Ordinal));

        RollingMaintainResult recovered = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);

        Assert.Equal(1, recovered.AdoptedBlockCount);
        Assert.Equal(0, recovered.GeneratedBlockCount);
        Assert.Single(recovered.Manifest.Blocks!);
        Assert.Equal(recovered.Manifest.Blocks![0].HistoryAfter, recovered.Manifest.HistoryHead);
        Assert.Empty(Directory.EnumerateFileSystemEntries(initialized.Paths.StagingDirectory));
    }

    [Fact]
    public async Task CrashBeforeManifestCommit_LeavesOldValidManifestThenAdopts()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        var fault = new ThrowOnceFault(RollingPlannerCheckpoint.BeforeManifestCommit, 1);
        RollingProgrammingPlanner planner = fixture.CreatePlanner(fault);
        RollingInitializationResult initialized = await fixture.InitializeAsync(planner);

        await Assert.ThrowsAsync<InjectedRollingFailure>(() => planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1));
        RollingManifestValidator.Validate(new RollingPlanStore().LoadManifest(initialized.Paths.ManifestPath));

        RollingMaintainResult recovered = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        Assert.Equal(1, recovered.AdoptedBlockCount);
    }

    [Fact]
    public async Task CrashAfterManifestCommit_LeavesNewValidManifestAndCleansStagingOnRetry()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        var fault = new ThrowOnceFault(RollingPlannerCheckpoint.AfterManifestCommit, 1);
        RollingProgrammingPlanner planner = fixture.CreatePlanner(fault);
        RollingInitializationResult initialized = await fixture.InitializeAsync(planner);

        await Assert.ThrowsAsync<InjectedRollingFailure>(() => planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1));
        RollingProgrammingManifest committed = new RollingPlanStore().LoadManifest(initialized.Paths.ManifestPath);
        Assert.Single(committed.Blocks!);

        RollingMaintainResult recovered = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        Assert.Equal(0, recovered.GeneratedBlockCount);
        Assert.Equal(0, recovered.AdoptedBlockCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(initialized.Paths.StagingDirectory));
    }

    [Fact]
    public async Task FailureDuringBlockTwoNeverClaimsBlockTwoOrThree()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        var fault = new ThrowOnceFault(RollingPlannerCheckpoint.AfterIntent, 2);
        RollingProgrammingPlanner planner = fixture.CreatePlanner(fault);
        RollingInitializationResult initialized = await fixture.InitializeAsync(planner);

        await Assert.ThrowsAsync<InjectedRollingFailure>(() => planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None));
        RollingProgrammingManifest manifest = new RollingPlanStore().LoadManifest(initialized.Paths.ManifestPath);

        Assert.Single(manifest.Blocks!);
        Assert.Equal(2, manifest.NextSequence);
        Assert.Equal(manifest.Blocks![0].HistoryAfter, manifest.HistoryHead);
    }

    [Fact]
    public async Task SelectedFileMutationAbortsAndFrozenIntentDoesNotResnapshotNewerInput()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        var mutation = new MutateSelectedMediaFault(fixture);
        RollingProgrammingPlanner planner = fixture.CreatePlanner(mutation);
        RollingInitializationResult initialized = await fixture.InitializeAsync(planner);

        await Assert.ThrowsAsync<InvalidDataException>(() => planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1));
        Assert.Empty(new RollingPlanStore().LoadManifest(initialized.Paths.ManifestPath).Blocks!);

        RollingProgrammingPlanner retry = fixture.CreatePlanner();
        await Assert.ThrowsAsync<InvalidDataException>(() => retry.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1));
        Assert.Empty(new RollingPlanStore().LoadManifest(initialized.Paths.ManifestPath).Blocks!);
    }

    [Fact]
    public async Task SelectedFileDisappearanceBeforeCommitAbortsWithoutManifestClaim()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        var disappearance = new DeleteSelectedMediaFault(fixture);
        RollingProgrammingPlanner planner = fixture.CreatePlanner(disappearance);
        RollingInitializationResult initialized = await fixture.InitializeAsync(planner);

        await Assert.ThrowsAsync<InvalidDataException>(() => planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1));

        Assert.Empty(new RollingPlanStore().LoadManifest(initialized.Paths.ManifestPath).Blocks!);
        Assert.True(File.Exists(Path.Combine(
            initialized.Paths.StagingDirectory,
            "000000000001",
            "intent.json")));
    }

    [Fact]
    public async Task CorruptUncommittedBlockIsQuarantinedAndRefused()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        RollingInitializationResult initialized = await fixture.InitializeAsync(planner);
        string corrupt = Path.Combine(initialized.Paths.BlocksDirectory, "corrupt");
        Directory.CreateDirectory(corrupt);
        await File.WriteAllTextAsync(Path.Combine(corrupt, "block.json"), "{ not-json }");

        await Assert.ThrowsAsync<InvalidDataException>(() => planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1));

        Assert.False(Directory.Exists(corrupt));
        Assert.Single(Directory.EnumerateDirectories(initialized.Paths.OrphanedDirectory));
        Assert.Empty(new RollingPlanStore().LoadManifest(initialized.Paths.ManifestPath).Blocks!);
    }

    [Fact]
    public async Task CompetingPlausibleOrphansAreRefusedRatherThanGuessed()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        var fault = new ThrowOnceFault(RollingPlannerCheckpoint.AfterArtifactsPublished, 1);
        RollingProgrammingPlanner planner = fixture.CreatePlanner(fault);
        RollingInitializationResult initialized = await fixture.InitializeAsync(planner);
        await Assert.ThrowsAsync<InjectedRollingFailure>(() => planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1));
        string original = Directory.EnumerateDirectories(initialized.Paths.BlocksDirectory).Single();
        string competing = Path.Combine(initialized.Paths.BlocksDirectory, "competing-copy");
        CopyDirectory(original, competing);
        string competingDescriptorPath = Path.Combine(competing, "block.json");
        RollingBlockDescriptor competingDescriptor = RollingProgrammingJson.Deserialize<RollingBlockDescriptor>(
            File.ReadAllText(competingDescriptorPath),
            "competing descriptor");
        string competingPrefix = "blocks/competing-copy";
        competingDescriptor = new RollingBlockDescriptor
        {
            Block = competingDescriptor.Block! with
            {
                PlaylistPath = $"{competingPrefix}/playlist.json",
                DescriptorPath = $"{competingPrefix}/block.json",
                InputSnapshotPath = $"{competingPrefix}/input.json",
            },
        };
        await File.WriteAllTextAsync(
            competingDescriptorPath,
            RollingProgrammingJson.Serialize(competingDescriptor));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            planner.MaintainAsync(fixture.Root, CancellationToken.None, committedBlockTarget: 1));

        Assert.Contains("Multiple plausible", error.Message, StringComparison.Ordinal);
        Assert.Empty(new RollingPlanStore().LoadManifest(initialized.Paths.ManifestPath).Blocks!);
    }

    [Fact]
    public async Task CommittedBlockIsNeverOverwrittenWhenValidationFails()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult first = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        RollingCommittedBlock block = first.Manifest.Blocks!.Single();
        string playlistPath = RollingPathSafety.ResolveExistingFile(
            first.Paths.RollingRoot,
            block.PlaylistPath!);
        await File.AppendAllTextAsync(playlistPath, " ");
        string corrupted = File.ReadAllText(playlistPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 2));

        Assert.Equal(corrupted, File.ReadAllText(playlistPath));
        Assert.Single(new RollingPlanStore().LoadManifest(first.Paths.ManifestPath).Blocks!);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private sealed class ThrowOnceFault(
        RollingPlannerCheckpoint target,
        long targetSequence) : IRollingPlannerFaultInjector
    {
        private bool _thrown;

        public void Reach(RollingPlannerCheckpoint checkpoint, long sequence)
        {
            if (!_thrown && checkpoint == target && sequence == targetSequence)
            {
                _thrown = true;
                throw new InjectedRollingFailure();
            }
        }
    }

    private sealed class MutateSelectedMediaFault(RollingLibraryFixture fixture) : IRollingPlannerFaultInjector
    {
        private bool _mutated;

        public void Reach(RollingPlannerCheckpoint checkpoint, long sequence)
        {
            if (_mutated || checkpoint != RollingPlannerCheckpoint.AfterPlaylistStaged)
            {
                return;
            }

            _mutated = true;
            RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(fixture.Root);
            string playlistPath = Path.Combine(paths.StagingDirectory, sequence.ToString("D12"), "playlist.json");
            PlaylistDocument playlist = RollingProgrammingJson.Deserialize<PlaylistDocument>(
                File.ReadAllText(playlistPath),
                "staged playlist");
            string mediaPath = Path.Combine(
                fixture.LibraryRoot,
                playlist.Items[0].RelativePath.Replace('/', Path.DirectorySeparatorChar));
            File.AppendAllText(mediaPath, "changed-after-snapshot");
        }
    }

    private sealed class DeleteSelectedMediaFault(RollingLibraryFixture fixture) : IRollingPlannerFaultInjector
    {
        public void Reach(RollingPlannerCheckpoint checkpoint, long sequence)
        {
            if (checkpoint != RollingPlannerCheckpoint.AfterPlaylistStaged)
            {
                return;
            }

            RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(fixture.Root);
            string playlistPath = Path.Combine(paths.StagingDirectory, sequence.ToString("D12"), "playlist.json");
            PlaylistDocument playlist = RollingProgrammingJson.Deserialize<PlaylistDocument>(
                File.ReadAllText(playlistPath),
                "staged playlist");
            string mediaPath = Path.Combine(
                fixture.LibraryRoot,
                playlist.Items[0].RelativePath.Replace('/', Path.DirectorySeparatorChar));
            File.Delete(mediaPath);
        }
    }

    private sealed class InjectedRollingFailure : Exception;
}
