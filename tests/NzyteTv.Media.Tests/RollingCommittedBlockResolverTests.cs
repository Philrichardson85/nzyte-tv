using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class RollingCommittedBlockResolverTests
{
    [Fact]
    public async Task Resolve_VerifiesCommittedArtifactsReadinessPlanAndRuntimeQueueIdentity()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult maintained = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        var resolver = new RollingCommittedBlockResolver();

        ResolvedRollingCommittedBlock resolved = resolver.ResolveManifestBlock(
            maintained.Paths,
            maintained.Manifest,
            1,
            fixture.LibraryRoot);

        Assert.Equal(maintained.Manifest.Blocks![0], resolved.Block);
        Assert.True(resolved.BroadcastPlan.IsReady);
        Assert.Equal(resolved.Block.ItemCount, resolved.BroadcastPlan.Items.Count);
        Assert.Equal(BroadcastQueueIdentity.Create(resolved.BroadcastPlan), resolved.QueueId);
        Assert.Equal(Path.GetFullPath(resolved.PlaylistPath), resolved.BroadcastPlan.PlaylistPaths.Single());
    }

    [Fact]
    public async Task Resolve_DoesNotConsultCurrentlyVisibleProgrammingConfiguration()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult maintained = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        File.WriteAllText(fixture.ProgrammingPath, "{ invalid current policy");

        ResolvedRollingCommittedBlock resolved = new RollingCommittedBlockResolver()
            .ResolveManifestBlock(
                maintained.Paths,
                maintained.Manifest,
                1,
                fixture.LibraryRoot);

        Assert.True(resolved.BroadcastPlan.IsReady);
    }

    [Fact]
    public async Task Resolve_LegacyCommittedBlockAfterExternalActivationKeepsAdjacentAuthority()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult maintained = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        Assert.Null(fixture.ReadInput(maintained.Manifest.Blocks!.Single()).AssetMetadataGenerationId);

        var repository = new ExternalAssetMetadataGenerationStore(
            Path.Combine(fixture.Root, "external-metadata"));
        await repository.CreateGenerationAsync("000000000001", 1, [], CancellationToken.None);
        await repository.PublishCurrentAsync("000000000001", 0, CancellationToken.None);
        await repository.CreateGenerationAsync("000000000002", 2, [], CancellationToken.None);
        await repository.PublishCurrentAsync("000000000002", 1, CancellationToken.None);
        var resolver = new RollingCommittedBlockResolver(metadataRepository: repository);

        ResolvedRollingCommittedBlock resolved = resolver.ResolveManifestBlock(
            maintained.Paths,
            maintained.Manifest,
            1,
            fixture.LibraryRoot);

        Assert.True(resolved.BroadcastPlan.IsReady);
        Assert.Null(resolved.InputSnapshot.AssetMetadataGenerationId);
        Assert.Equal("000000000002", repository.ReadCurrent().GenerationId);
    }

    [Fact]
    public async Task Resolve_MissingMediaMountIsClassifiedAsRetryableUnavailability()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult maintained = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        string unavailable = Path.Combine(fixture.Root, "unmounted-library");

        RollingMediaUnavailableException exception = Assert.Throws<RollingMediaUnavailableException>(() =>
            new RollingCommittedBlockResolver().ResolveManifestBlock(
                maintained.Paths,
                maintained.Manifest,
                1,
                unavailable));

        Assert.Contains("unavailable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolve_SelectedMediaIdentityMutationIsPermanentValidationFailure()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult maintained = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        ResolvedRollingCommittedBlock original = new RollingCommittedBlockResolver()
            .ResolveManifestBlock(
                maintained.Paths,
                maintained.Manifest,
                1,
                fixture.LibraryRoot);
        string selectedMedia = original.BroadcastPlan.Items[0].MediaPath;
        File.AppendAllText(selectedMedia, "mutated");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new RollingCommittedBlockResolver().ResolveManifestBlock(
                maintained.Paths,
                maintained.Manifest,
                1,
                fixture.LibraryRoot));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolve_PlaylistHashMutationIsDetectedBeforeExecution()
    {
        using RollingLibraryFixture fixture = await RollingLibraryFixture.CreateAsync(programming: true);
        RollingProgrammingPlanner planner = fixture.CreatePlanner();
        await fixture.InitializeAsync(planner);
        RollingMaintainResult maintained = await planner.MaintainAsync(
            fixture.Root,
            CancellationToken.None,
            committedBlockTarget: 1);
        string playlistPath = RollingPathSafety.ResolveExistingFile(
            maintained.Paths.RollingRoot,
            maintained.Manifest.Blocks![0].PlaylistPath!);
        File.AppendAllText(playlistPath, Environment.NewLine);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new RollingCommittedBlockResolver().ResolveManifestBlock(
                maintained.Paths,
                maintained.Manifest,
                1,
                fixture.LibraryRoot));

        Assert.Contains("hash mismatch", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
