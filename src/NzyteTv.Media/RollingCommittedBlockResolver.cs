using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record ResolvedRollingCommittedBlock(
    RollingCommittedBlock Block,
    string PlaylistPath,
    PlaylistDocument Playlist,
    RollingPlanningInputSnapshot InputSnapshot,
    BroadcastPlan BroadcastPlan,
    string QueueId);

public interface IRollingCommittedBlockResolver
{
    ResolvedRollingCommittedBlock ResolveManifestBlock(
        RollingProgrammingPaths paths,
        RollingProgrammingManifest manifest,
        long sequence,
        string libraryRoot);

    ResolvedRollingCommittedBlock VerifyBlock(
        RollingProgrammingPaths paths,
        string plannerId,
        RollingCommittedBlock block,
        string libraryRoot);
}

public sealed class RollingCommittedBlockResolver : IRollingCommittedBlockResolver
{
    private readonly IRollingPlanStore _store;
    private readonly IPlaylistHistoryStore _historyStore;
    private readonly IBroadcastPlanner _broadcastPlanner;

    public RollingCommittedBlockResolver(
        IRollingPlanStore? store = null,
        IPlaylistHistoryStore? historyStore = null,
        IBroadcastPlanner? broadcastPlanner = null)
    {
        _store = store ?? new RollingPlanStore();
        _historyStore = historyStore ?? new PlaylistHistoryStore();
        _broadcastPlanner = broadcastPlanner ?? new BroadcastPlanner();
    }

    public ResolvedRollingCommittedBlock ResolveManifestBlock(
        RollingProgrammingPaths paths,
        RollingProgrammingManifest manifest,
        long sequence,
        string libraryRoot)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifest);
        RollingManifestValidator.Validate(manifest);
        if (sequence < 1 || sequence > manifest.Blocks!.Count)
        {
            throw new RollingBlockNotAvailableException(sequence);
        }

        RollingCommittedBlock block = manifest.Blocks[checked((int)sequence - 1)];
        if (block.Sequence != sequence)
        {
            throw new InvalidDataException(
                $"Rolling manifest does not contain the expected block sequence {sequence}.");
        }

        return VerifyBlock(paths, manifest.PlannerId!, block, libraryRoot);
    }

    public ResolvedRollingCommittedBlock VerifyBlock(
        RollingProgrammingPaths paths,
        string plannerId,
        RollingCommittedBlock block,
        string libraryRoot)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(plannerId);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        if (!Guid.TryParseExact(plannerId, "N", out _))
        {
            throw new InvalidDataException("Rolling planner lineage ID is invalid.");
        }

        RollingManifestValidator.ValidateBlock(block);
        string playlistPath = VerifyArtifact(paths, new RollingArtifactReference(
            block.PlaylistPath!,
            block.PlaylistSha256!));
        string inputPath = VerifyArtifact(paths, new RollingArtifactReference(
            block.InputSnapshotPath!,
            block.InputSnapshotSha256!));
        string descriptorPath = VerifyArtifact(paths, new RollingArtifactReference(
            block.DescriptorPath!,
            block.DescriptorSha256!));
        string blockDirectory = Path.GetDirectoryName(descriptorPath)!;
        if (!string.Equals(Path.GetDirectoryName(playlistPath), blockDirectory, GetPathComparison())
            || !string.Equals(Path.GetDirectoryName(inputPath), blockDirectory, GetPathComparison()))
        {
            throw new InvalidDataException(
                $"Rolling block {block.Sequence} artifacts are not contained in one immutable block directory.");
        }

        VerifyArtifact(paths, block.HistoryBefore!);
        string historyAfterPath = VerifyArtifact(paths, block.HistoryAfter!);

        RollingBlockDescriptor descriptor = _store.Read<RollingBlockDescriptor>(
            descriptorPath,
            $"rolling block {block.Sequence} descriptor");
        if (descriptor.SchemaVersion != RollingProgrammingPolicy.ArtifactSchemaVersion
            || descriptor.Block is null)
        {
            throw new InvalidDataException($"Rolling block {block.Sequence} descriptor is invalid.");
        }

        RollingManifestValidator.ValidateBlock(descriptor.Block, descriptor: true);
        if (descriptor.Block != block with { DescriptorSha256 = null })
        {
            throw new InvalidDataException(
                $"Rolling block {block.Sequence} descriptor does not match the manifest.");
        }

        RollingPlanningInputSnapshot input = _store.Read<RollingPlanningInputSnapshot>(
            inputPath,
            $"rolling block {block.Sequence} input snapshot");
        PlaylistPlanningSnapshotService.ValidateSnapshot(input, requireReadiness: true);
        PlaylistDocument playlist = _store.Read<PlaylistDocument>(
            playlistPath,
            $"rolling block {block.Sequence} playlist");
        PlaylistHistoryDocument historyAfter = _historyStore.Load(historyAfterPath);
        if (input.Sequence != block.Sequence
            || input.Seed != block.Seed
            || input.HistoryBeforeHash != block.HistoryBefore!.Sha256
            || input.CatalogSnapshotHash != block.CatalogSnapshotHash
            || input.ProgrammingSnapshotHash != block.ProgrammingSnapshotHash
            || input.InventorySnapshotHash != block.InventorySnapshotHash
            || playlist.Seed != block.Seed
            || playlist.Items.Count != block.ItemCount
            || playlist.ScheduleStartUtc != block.ScheduleStartUtc
            || playlist.ActualDurationSeconds != block.ActualDurationSeconds
            || historyAfter.ScheduleEndUtc != block.ScheduleEndUtc)
        {
            throw new InvalidDataException(
                $"Rolling block {block.Sequence} metadata does not match its immutable artifacts.");
        }

        string calculatedBlockId = RollingBlockIdentity.Calculate(new RollingBlockIdentityInput(
            plannerId,
            block.Sequence,
            block.ParentBlockId,
            block.Seed,
            block.TargetDurationSeconds,
            playlist,
            block.HistoryBefore.Sha256,
            block.HistoryAfter!.Sha256,
            block.CatalogSnapshotHash!,
            block.ProgrammingSnapshotHash!,
            block.InventorySnapshotHash!,
            block.PlannerAlgorithmVersion!));
        if (!string.Equals(calculatedBlockId, block.BlockId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Rolling block {block.Sequence} identity mismatch.");
        }

        string normalizedLibraryRoot = Path.GetFullPath(libraryRoot);
        if (!Directory.Exists(normalizedLibraryRoot))
        {
            throw new RollingMediaUnavailableException(
                "The rolling block media library is unavailable. The media mount may be offline.");
        }

        PlaylistPlanningSnapshotService.VerifyCapturedReadiness(
            input,
            playlist,
            normalizedLibraryRoot,
            CancellationToken.None).GetAwaiter().GetResult();
        BroadcastPlan plan = _broadcastPlanner.CreatePlan([playlistPath], normalizedLibraryRoot);
        if (!plan.IsReady)
        {
            string details = string.Join("; ", plan.Issues.Select(issue => issue.Detail));
            throw new InvalidDataException(
                $"Generated rolling playlist failed broadcast readiness validation: {details}");
        }

        if (plan.Items.Count != block.ItemCount)
        {
            throw new InvalidDataException(
                $"Rolling block {block.Sequence} broadcast item count does not match its manifest entry.");
        }

        EnsureNoSecretMaterial(_store.ReadText(playlistPath), $"block {block.Sequence} playlist");
        EnsureNoSecretMaterial(_store.ReadText(inputPath), $"block {block.Sequence} input snapshot");
        EnsureNoSecretMaterial(_store.ReadText(descriptorPath), $"block {block.Sequence} descriptor");
        return new ResolvedRollingCommittedBlock(
            block,
            playlistPath,
            playlist,
            input,
            plan,
            BroadcastQueueIdentity.Create(plan));
    }

    private string VerifyArtifact(
        RollingProgrammingPaths paths,
        RollingArtifactReference reference)
    {
        RollingManifestValidator.ValidateArtifactReference(reference, "artifact");
        string path;
        try
        {
            path = RollingPathSafety.ResolveExistingFile(paths.RollingRoot, reference.RelativePath);
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidDataException(
                $"Committed rolling artifact is missing: {reference.RelativePath}",
                exception);
        }

        string actual = RollingProgrammingJson.Sha256File(path);
        if (!string.Equals(actual, reference.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Rolling artifact hash mismatch: {reference.RelativePath}");
        }

        EnsureNoSecretMaterial(_store.ReadText(path), reference.RelativePath);
        return path;
    }

    private static void EnsureNoSecretMaterial(string content, string description)
    {
        if (!string.Equals(content, StationSecretRedactor.RedactRtmpUrls(content), StringComparison.Ordinal)
            || content.Contains("NZYTE_TV_RTMP_URL", StringComparison.Ordinal)
            || content.Contains("\"stationPid\"", StringComparison.OrdinalIgnoreCase)
            || content.Contains("\"ffmpegPid\"", StringComparison.OrdinalIgnoreCase)
            || content.Contains("\"resumeGlobalIndex\"", StringComparison.OrdinalIgnoreCase)
            || content.Contains("\"lastHeartbeatUtc\"", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Rolling {description} contains forbidden secret or runtime-state material.");
        }
    }

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

public sealed class RollingBlockNotAvailableException(long sequence)
    : Exception($"Rolling block sequence {sequence} is not committed in the manifest.")
{
    public long Sequence { get; } = sequence;
}

public sealed class RollingMediaUnavailableException(string message, Exception? innerException = null)
    : IOException(message, innerException);
