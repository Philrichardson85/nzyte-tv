using System.Buffers.Binary;
using System.Security.Cryptography;
using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record RollingInitializationRequest(
    string MediaRoot,
    string? HistoryPath = null,
    int? BaseSeed = null);

public sealed record RollingInitializationResult(
    RollingProgrammingPaths Paths,
    RollingProgrammingManifest Manifest,
    bool Created,
    bool ImportedHistory);

public sealed record RollingMaintainResult(
    RollingProgrammingPaths Paths,
    RollingProgrammingManifest Manifest,
    int GeneratedBlockCount,
    int AdoptedBlockCount,
    bool TargetSatisfied);

public sealed record RollingValidationResult(
    RollingProgrammingPaths Paths,
    RollingProgrammingManifest? Manifest,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Manifest is not null && Errors.Count == 0;
}

public sealed record RollingBlockStatus(
    long Sequence,
    string BlockId,
    double ActualDurationSeconds,
    int? ProgrammingRevision);

public sealed record RollingProgrammingStatus(
    RollingProgrammingPaths Paths,
    RollingProgrammingManifest? Manifest,
    IReadOnlyList<RollingBlockStatus> Blocks,
    int? LatestProgrammingRevision,
    int StagingEntryCount,
    int OrphanedEntryCount,
    double PreparedActualDurationSeconds,
    RollingValidationResult Validation);

public enum RollingPlannerCheckpoint
{
    BeforeIntent,
    AfterIntent,
    AfterPlaylistStaged,
    AfterArtifactsPublished,
    BeforeManifestCommit,
    AfterManifestCommit,
}

public interface IRollingPlannerFaultInjector
{
    void Reach(RollingPlannerCheckpoint checkpoint, long sequence);
}

public sealed class NoOpRollingPlannerFaultInjector : IRollingPlannerFaultInjector
{
    public static NoOpRollingPlannerFaultInjector Instance { get; } = new();

    public void Reach(RollingPlannerCheckpoint checkpoint, long sequence)
    {
    }
}

public interface IRollingProgrammingPlanner
{
    Task<RollingInitializationResult> InitializeAsync(
        RollingInitializationRequest request,
        CancellationToken cancellationToken);

    Task<RollingMaintainResult> MaintainAsync(
        string mediaRoot,
        CancellationToken cancellationToken,
        int? committedBlockTarget = null);

    RollingValidationResult Validate(string mediaRoot);

    RollingProgrammingStatus GetStatus(string mediaRoot);
}

public sealed class RollingProgrammingPlanner : IRollingProgrammingPlanner
{
    private readonly IRollingPlanStore _store;
    private readonly IPlaylistHistoryStore _historyStore;
    private readonly IPlaylistPlanningSnapshotService? _snapshotService;
    private readonly IBroadcastPlanner _broadcastPlanner;
    private readonly IRollingPlannerLockProvider _lockProvider;
    private readonly IRollingPlannerFaultInjector _faultInjector;
    private readonly IProgrammingConfigurationStore _programmingStore;
    private readonly IRollingCommittedBlockResolver _committedBlockResolver;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _idFactory;
    private readonly Func<int> _baseSeedFactory;

    public RollingProgrammingPlanner(
        IRollingPlanStore? store = null,
        IPlaylistHistoryStore? historyStore = null,
        IPlaylistPlanningSnapshotService? snapshotService = null,
        IBroadcastPlanner? broadcastPlanner = null,
        IRollingPlannerLockProvider? lockProvider = null,
        IRollingPlannerFaultInjector? faultInjector = null,
        IProgrammingConfigurationStore? programmingStore = null,
        IRollingCommittedBlockResolver? committedBlockResolver = null,
        TimeProvider? timeProvider = null,
        Func<string>? idFactory = null,
        Func<int>? baseSeedFactory = null)
    {
        _store = store ?? new RollingPlanStore();
        _historyStore = historyStore ?? new PlaylistHistoryStore();
        _snapshotService = snapshotService;
        _broadcastPlanner = broadcastPlanner ?? new BroadcastPlanner();
        _lockProvider = lockProvider ?? new RollingPlannerLockProvider();
        _faultInjector = faultInjector ?? NoOpRollingPlannerFaultInjector.Instance;
        _programmingStore = programmingStore ?? new ProgrammingConfigurationStore();
        _committedBlockResolver = committedBlockResolver ?? new RollingCommittedBlockResolver(
            _store,
            _historyStore,
            _broadcastPlanner);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _idFactory = idFactory ?? (() => Guid.NewGuid().ToString("N"));
        _baseSeedFactory = baseSeedFactory ?? CreateRandomSeed;
    }

    public async Task<RollingInitializationResult> InitializeAsync(
        RollingInitializationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(request.MediaRoot);
        if (!Directory.Exists(paths.MediaRoot))
        {
            throw new DirectoryNotFoundException($"Media root not found: {paths.MediaRoot}");
        }

        paths.EnsureDirectories();
        ValidateManagedDirectories(paths);
        using IRollingPlannerLock plannerLock = _lockProvider.Acquire(paths.PlannerLockPath);
        PlaylistHistoryDocument genesis = LoadRequestedGenesis(request.HistoryPath);
        string genesisContent = RollingProgrammingJson.Serialize(genesis);
        EnsureNoSecretMaterial(genesisContent, "genesis history");
        string genesisHash = RollingProgrammingJson.Sha256(genesisContent);
        var genesisReference = new RollingArtifactReference(
            $"history/{genesisHash}.json",
            genesisHash);

        RollingProgrammingManifest? existing = _store.LoadManifestIfExists(paths.ManifestPath);
        if (existing is not null)
        {
            if (request.BaseSeed is int requestedSeed && existing.BaseSeed != requestedSeed)
            {
                throw new InvalidOperationException(
                    "Rolling programming is already initialized with a different base seed; the manifest was not changed.");
            }

            if (existing.GenesisHistory != genesisReference)
            {
                throw new InvalidOperationException(
                    "Rolling programming is already initialized with a different genesis history; the manifest was not changed.");
            }

            VerifyArtifact(paths, existing.GenesisHistory);
            return new RollingInitializationResult(
                paths,
                existing,
                Created: false,
                ImportedHistory: request.HistoryPath is not null);
        }

        string historyPath = RollingPathSafety.Resolve(paths.RollingRoot, genesisReference.RelativePath);
        await _store.WriteImmutableAsync(historyPath, genesis, cancellationToken).ConfigureAwait(false);
        var manifest = new RollingProgrammingManifest
        {
            PlannerId = _idFactory(),
            BaseSeed = request.BaseSeed ?? _baseSeedFactory(),
            TargetBlockDurationSeconds = RollingProgrammingPolicy.DefaultTargetBlockDurationSeconds,
            TargetPreparedBlockCount = RollingProgrammingPolicy.DefaultTargetPreparedBlockCount,
            NextSequence = 1,
            GenesisHistory = genesisReference,
            HistoryHead = genesisReference,
            Blocks = [],
            InitializedAtUtc = _timeProvider.GetUtcNow(),
        };
        await _store.WriteManifestAsync(paths.ManifestPath, manifest, cancellationToken)
            .ConfigureAwait(false);
        return new RollingInitializationResult(
            paths,
            manifest,
            Created: true,
            ImportedHistory: request.HistoryPath is not null);
    }

    public async Task<RollingMaintainResult> MaintainAsync(
        string mediaRoot,
        CancellationToken cancellationToken,
        int? committedBlockTarget = null)
    {
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(mediaRoot);
        if (!File.Exists(paths.ManifestPath))
        {
            throw new FileNotFoundException(
                $"Rolling programming is not initialized: {paths.ManifestPath}",
                paths.ManifestPath);
        }

        paths.EnsureDirectories();
        ValidateManagedDirectories(paths);
        using IRollingPlannerLock plannerLock = _lockProvider.Acquire(paths.PlannerLockPath);
        RollingProgrammingManifest manifest = _store.LoadManifest(paths.ManifestPath);
        int target = committedBlockTarget ?? manifest.TargetPreparedBlockCount;
        if (target < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(committedBlockTarget),
                "A maintenance target cannot be negative.");
        }

        int adopted = await ReconcileAsync(paths, manifest, cancellationToken).ConfigureAwait(false);
        manifest = _store.LoadManifest(paths.ManifestPath);
        ValidateOrThrow(paths, manifest);
        int generated = 0;
        while (manifest.Blocks!.Count < target)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_snapshotService is null)
            {
                throw new InvalidOperationException(
                    "Rolling block generation requires a playlist planning snapshot service.");
            }

            manifest = await GenerateNextBlockAsync(paths, manifest, cancellationToken)
                .ConfigureAwait(false);
            generated++;
        }

        return new RollingMaintainResult(
            paths,
            manifest,
            generated,
            adopted,
            TargetSatisfied: manifest.Blocks.Count >= target);
    }

    public RollingValidationResult Validate(string mediaRoot)
    {
        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(mediaRoot);
        var errors = new List<string>();
        var warnings = new List<string>();
        RollingProgrammingManifest? manifest = null;
        try
        {
            manifest = _store.LoadManifest(paths.ManifestPath);
            ValidateOrThrow(paths, manifest);
            ProgrammingPaths currentInputs = ProgrammingPaths.FromMediaRoot(paths.MediaRoot);
            if (!Directory.Exists(currentInputs.LibraryRoot))
            {
                throw new DirectoryNotFoundException(
                    $"Normalized library root not found: {currentInputs.LibraryRoot}");
            }

            _ = new SongCatalogStore().Load(currentInputs.CatalogPath);
            _ = _programmingStore.LoadIfExists(currentInputs.ConfigurationPath);
            InspectUnreferencedBlocks(paths, manifest, warnings);
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException or InvalidDataException or
            IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            errors.Add(StationSecretRedactor.RedactRtmpUrls(exception.Message) ?? "Rolling validation failed.");
        }

        if (Directory.Exists(paths.StagingDirectory)
            && Directory.EnumerateFileSystemEntries(paths.StagingDirectory).Any())
        {
            warnings.Add("Uncommitted staging work exists; run rolling maintain to reconcile it.");
        }

        if (Directory.Exists(paths.OrphanedDirectory)
            && Directory.EnumerateFileSystemEntries(paths.OrphanedDirectory).Any())
        {
            warnings.Add("Quarantined rolling artifacts require operator review.");
        }

        return new RollingValidationResult(paths, manifest, errors, warnings);
    }

    public RollingProgrammingStatus GetStatus(string mediaRoot)
    {
        RollingValidationResult validation = Validate(mediaRoot);
        RollingProgrammingPaths paths = validation.Paths;
        RollingProgrammingManifest? manifest = validation.Manifest;
        int? latestRevision = null;
        try
        {
            ProgrammingPaths programmingPaths = ProgrammingPaths.FromMediaRoot(mediaRoot);
            latestRevision = _programmingStore.LoadIfExists(programmingPaths.ConfigurationPath)?.Revision;
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or InvalidDataException or UnauthorizedAccessException or IOException or
            ProgrammingConfigurationValidationException)
        {
            // Validation already reports committed-artifact health. The visible policy is informational.
        }

        RollingBlockStatus[] blocks = manifest?.Blocks?
            .Select(block => new RollingBlockStatus(
                block.Sequence,
                block.BlockId!,
                block.ActualDurationSeconds,
                block.ProgrammingRevision))
            .ToArray() ?? [];
        return new RollingProgrammingStatus(
            paths,
            manifest,
            blocks,
            latestRevision,
            CountEntries(paths.StagingDirectory),
            CountEntries(paths.OrphanedDirectory)
                + (validation.IsValid ? CountUnreferencedBlockDirectories(paths, manifest!) : 0),
            blocks.Sum(block => block.ActualDurationSeconds),
            validation);
    }

    private async Task<RollingProgrammingManifest> GenerateNextBlockAsync(
        RollingProgrammingPaths paths,
        RollingProgrammingManifest manifest,
        CancellationToken cancellationToken)
    {
        long sequence = manifest.NextSequence;
        string stageDirectory = Path.Combine(paths.StagingDirectory, sequence.ToString("D12"));
        string intentPath = Path.Combine(stageDirectory, "intent.json");
        RollingGenerationIntent intent;
        RollingPlanningInputSnapshot snapshot;
        if (File.Exists(intentPath))
        {
            intent = _store.Read<RollingGenerationIntent>(intentPath, $"rolling generation intent {sequence}");
            ValidateIntent(intent, manifest);
            string inputPath = RollingPathSafety.ResolveExistingFile(paths.RollingRoot, intent.InputSnapshotPath!);
            string inputContent = _store.ReadText(inputPath);
            if (!string.Equals(
                RollingProgrammingJson.Sha256(inputContent),
                intent.InputSnapshotSha256,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Frozen rolling input snapshot hash mismatch for sequence {sequence}.");
            }

            snapshot = RollingProgrammingJson.Deserialize<RollingPlanningInputSnapshot>(
                inputContent,
                $"rolling input snapshot {sequence}");
            ValidateSnapshotAgainstIntent(snapshot, intent);
        }
        else
        {
            ResetUncommittedStageWithoutIntent(stageDirectory);
            Directory.CreateDirectory(stageDirectory);
            PlaylistHistoryDocument historyBefore = LoadHistory(paths, manifest.HistoryHead!);
            int seed = RollingProgrammingSeed.Derive(manifest.PlannerId!, manifest.BaseSeed, sequence);
            DateTimeOffset generatedAt = _timeProvider.GetUtcNow();
            ProgrammingPaths programmingPaths = ProgrammingPaths.FromMediaRoot(paths.MediaRoot);
            snapshot = await _snapshotService!.CaptureAsync(
                new PlaylistPlanningSnapshotRequest(
                    programmingPaths.LibraryRoot,
                    programmingPaths.CatalogPath,
                    historyBefore,
                    TimeSpan.FromSeconds(manifest.TargetBlockDurationSeconds),
                    seed,
                    generatedAt,
                    sequence,
                    CaptureReadiness: true,
                    HistoryBeforeHash: manifest.HistoryHead!.Sha256),
                cancellationToken).ConfigureAwait(false);
            EnsureNoSecretMaterial(
                RollingProgrammingJson.Serialize(snapshot),
                $"captured input snapshot {sequence}");
            string inputPath = Path.Combine(stageDirectory, "input.json");
            await _store.WriteStagedAsync(inputPath, snapshot, cancellationToken).ConfigureAwait(false);
            string inputContent = _store.ReadText(inputPath);
            string inputHash = RollingProgrammingJson.Sha256(inputContent);
            intent = new RollingGenerationIntent
            {
                IntentId = _idFactory(),
                Sequence = sequence,
                Seed = seed,
                ParentBlockId = manifest.Blocks!.LastOrDefault()?.BlockId,
                HistoryBefore = manifest.HistoryHead,
                InputSnapshotPath = RollingPathSafety.ToPortableRelative(paths.RollingRoot, inputPath),
                InputSnapshotSha256 = inputHash,
                PlannerAlgorithmVersion = RollingProgrammingPolicy.PlannerAlgorithmVersion,
                GeneratedAtUtc = generatedAt,
                PlannedScheduleStartUtc = snapshot.PlannedScheduleStartUtc,
                TargetDurationSeconds = manifest.TargetBlockDurationSeconds,
            };
            _faultInjector.Reach(RollingPlannerCheckpoint.BeforeIntent, sequence);
            await _store.WriteStagedAsync(intentPath, intent, cancellationToken).ConfigureAwait(false);
            _faultInjector.Reach(RollingPlannerCheckpoint.AfterIntent, sequence);
        }

        EnsureNoSecretMaterial(
            RollingProgrammingJson.Serialize(snapshot),
            $"frozen input snapshot {sequence}");
        PlaylistGenerationResult generation = _snapshotService!.Generate(snapshot);
        EnsureNoSecretMaterial(
            RollingProgrammingJson.Serialize(generation.Playlist),
            $"generated playlist {sequence}");
        EnsureNoSecretMaterial(
            RollingProgrammingJson.Serialize(generation.UpdatedHistory),
            $"generated planned history {sequence}");
        string stagedPlaylistPath = Path.Combine(stageDirectory, "playlist.json");
        await _store.WriteStagedAsync(stagedPlaylistPath, generation.Playlist, cancellationToken)
            .ConfigureAwait(false);
        _faultInjector.Reach(RollingPlannerCheckpoint.AfterPlaylistStaged, sequence);

        string stagedHistoryPath = Path.Combine(stageDirectory, "history-after.json");
        await _store.WriteStagedAsync(stagedHistoryPath, generation.UpdatedHistory, cancellationToken)
            .ConfigureAwait(false);
        string historyAfterContent = _store.ReadText(stagedHistoryPath);
        string historyAfterHash = RollingProgrammingJson.Sha256(historyAfterContent);
        var historyAfterReference = new RollingArtifactReference(
            $"history/{historyAfterHash}.json",
            historyAfterHash);

        ProgrammingPaths currentPaths = ProgrammingPaths.FromMediaRoot(paths.MediaRoot);
        await PlaylistPlanningSnapshotService.VerifyCapturedReadiness(
            snapshot,
            generation.Playlist,
            currentPaths.LibraryRoot,
            cancellationToken).ConfigureAwait(false);
        EnsureBroadcastReady(stagedPlaylistPath, currentPaths.LibraryRoot);

        string blockId = RollingBlockIdentity.Calculate(new RollingBlockIdentityInput(
            manifest.PlannerId!,
            sequence,
            intent.ParentBlockId,
            intent.Seed,
            intent.TargetDurationSeconds,
            generation.Playlist,
            intent.HistoryBefore!.Sha256,
            historyAfterHash,
            snapshot.CatalogSnapshotHash!,
            snapshot.ProgrammingSnapshotHash!,
            snapshot.InventorySnapshotHash!,
            intent.PlannerAlgorithmVersion!));
        string blockDirectoryName = $"{sequence:D12}-{blockId}";
        string playlistRelativePath = $"blocks/{blockDirectoryName}/playlist.json";
        string descriptorRelativePath = $"blocks/{blockDirectoryName}/block.json";
        string inputRelativePath = $"blocks/{blockDirectoryName}/input.json";
        string playlistContent = _store.ReadText(stagedPlaylistPath);
        string playlistHash = RollingProgrammingJson.Sha256(playlistContent);
        string inputContentForPublication = RollingProgrammingJson.Serialize(snapshot);
        string inputHashForPublication = RollingProgrammingJson.Sha256(inputContentForPublication);
        DateTimeOffset scheduleEnd = generation.UpdatedHistory.ScheduleEndUtc
            ?? generation.Playlist.ScheduleStartUtc.AddSeconds(generation.Playlist.ActualDurationSeconds);
        var descriptorBlock = new RollingCommittedBlock
        {
            Sequence = sequence,
            BlockId = blockId,
            ParentBlockId = intent.ParentBlockId,
            Seed = intent.Seed,
            PlaylistPath = playlistRelativePath,
            PlaylistSha256 = playlistHash,
            DescriptorPath = descriptorRelativePath,
            DescriptorSha256 = null,
            InputSnapshotPath = inputRelativePath,
            InputSnapshotSha256 = inputHashForPublication,
            TargetDurationSeconds = generation.Playlist.TargetDurationSeconds,
            ActualDurationSeconds = generation.Playlist.ActualDurationSeconds,
            ScheduleStartUtc = generation.Playlist.ScheduleStartUtc,
            ScheduleEndUtc = scheduleEnd,
            ItemCount = generation.Playlist.Items.Count,
            HistoryBefore = intent.HistoryBefore,
            HistoryAfter = historyAfterReference,
            CatalogSnapshotHash = snapshot.CatalogSnapshotHash,
            ProgrammingSnapshotHash = snapshot.ProgrammingSnapshotHash,
            InventorySnapshotHash = snapshot.InventorySnapshotHash,
            ProgrammingSchemaVersion = snapshot.ProgrammingConfiguration?.SchemaVersion,
            ProgrammingRevision = snapshot.ProgrammingConfiguration?.Revision,
            PlannerAlgorithmVersion = intent.PlannerAlgorithmVersion,
            GeneratedAtUtc = intent.GeneratedAtUtc,
        };
        RollingManifestValidator.ValidateBlock(descriptorBlock, descriptor: true);
        var descriptor = new RollingBlockDescriptor { Block = descriptorBlock };
        string descriptorContent = RollingProgrammingJson.Serialize(descriptor);
        string descriptorHash = RollingProgrammingJson.Sha256(descriptorContent);
        RollingCommittedBlock committedBlock = descriptorBlock with { DescriptorSha256 = descriptorHash };
        RollingManifestValidator.ValidateBlock(committedBlock);

        string publishDirectory = Path.Combine(stageDirectory, "publish");
        Directory.CreateDirectory(publishDirectory);
        await _store.WriteStagedAsync(
            Path.Combine(publishDirectory, "playlist.json"),
            generation.Playlist,
            cancellationToken).ConfigureAwait(false);
        await _store.WriteStagedAsync(
            Path.Combine(publishDirectory, "input.json"),
            snapshot,
            cancellationToken).ConfigureAwait(false);
        await _store.WriteStagedAsync(
            Path.Combine(publishDirectory, "block.json"),
            descriptor,
            cancellationToken).ConfigureAwait(false);

        string finalHistoryPath = RollingPathSafety.Resolve(paths.RollingRoot, historyAfterReference.RelativePath);
        await _store.WriteImmutableAsync(
            finalHistoryPath,
            generation.UpdatedHistory,
            cancellationToken).ConfigureAwait(false);
        string finalBlockDirectory = Path.Combine(paths.BlocksDirectory, blockDirectoryName);
        PublishImmutableDirectory(publishDirectory, finalBlockDirectory);
        _faultInjector.Reach(RollingPlannerCheckpoint.AfterArtifactsPublished, sequence);

        ValidateCommittedBlock(paths, committedBlock);
        RollingProgrammingManifest updated = AppendBlock(manifest, committedBlock);
        _faultInjector.Reach(RollingPlannerCheckpoint.BeforeManifestCommit, sequence);
        await _store.WriteManifestAsync(paths.ManifestPath, updated, cancellationToken)
            .ConfigureAwait(false);
        _faultInjector.Reach(RollingPlannerCheckpoint.AfterManifestCommit, sequence);
        DeleteDirectoryIfExists(stageDirectory);
        return updated;
    }

    private async Task<int> ReconcileAsync(
        RollingProgrammingPaths paths,
        RollingProgrammingManifest manifest,
        CancellationToken cancellationToken)
    {
        DeletePartialFiles(paths.StagingDirectory);
        int adopted = 0;
        while (true)
        {
            var referencedDescriptors = manifest.Blocks!
                .Select(block => Path.GetFullPath(RollingPathSafety.Resolve(paths.RollingRoot, block.DescriptorPath!)))
                .ToHashSet(GetPathComparer());
            var plausible = new List<RollingCommittedBlock>();
            foreach (string directory in Directory.EnumerateDirectories(paths.BlocksDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string descriptorPath = Path.Combine(directory, "block.json");
                if (referencedDescriptors.Contains(Path.GetFullPath(descriptorPath)))
                {
                    continue;
                }

                try
                {
                    RollingBlockDescriptor descriptor = _store.Read<RollingBlockDescriptor>(
                        descriptorPath,
                        $"uncommitted rolling block descriptor '{descriptorPath}'");
                    if (descriptor.SchemaVersion != RollingProgrammingPolicy.ArtifactSchemaVersion
                        || descriptor.Block is null)
                    {
                        throw new InvalidDataException("Uncommitted rolling block descriptor is invalid.");
                    }

                    RollingManifestValidator.ValidateBlock(descriptor.Block, descriptor: true);
                    string descriptorHash = RollingProgrammingJson.Sha256(_store.ReadText(descriptorPath));
                    RollingCommittedBlock candidate = descriptor.Block with
                    {
                        DescriptorSha256 = descriptorHash,
                    };
                    ValidateCommittedBlock(paths, candidate);
                    bool exactNext = candidate.Sequence == manifest.NextSequence
                        && string.Equals(
                            candidate.ParentBlockId,
                            manifest.Blocks!.LastOrDefault()?.BlockId,
                            StringComparison.Ordinal)
                        && candidate.HistoryBefore == manifest.HistoryHead;
                    if (exactNext)
                    {
                        plausible.Add(candidate);
                    }
                    else
                    {
                        QuarantineDirectory(paths, directory, "unexpected-chain");
                        throw new InvalidDataException(
                            $"Uncommitted block {candidate.Sequence} does not match the exact next manifest chain position.");
                    }
                }
                catch (Exception exception) when (exception is
                    FileNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    if (Directory.Exists(directory))
                    {
                        QuarantineDirectory(paths, directory, "corrupt");
                    }

                    throw new InvalidDataException(
                        "A corrupt or inconsistent uncommitted rolling block was quarantined; automatic adoption was refused.",
                        exception);
                }
            }

            if (plausible.Count > 1)
            {
                throw new InvalidDataException(
                    "Multiple plausible next rolling blocks exist. Automatic reconciliation refused to guess.");
            }

            if (plausible.Count == 0)
            {
                break;
            }

            manifest = AppendBlock(manifest, plausible[0]);
            await _store.WriteManifestAsync(paths.ManifestPath, manifest, cancellationToken)
                .ConfigureAwait(false);
            adopted++;
        }

        CleanCommittedStaging(paths, manifest);
        return adopted;
    }

    private void ValidateOrThrow(RollingProgrammingPaths paths, RollingProgrammingManifest manifest)
    {
        RollingManifestValidator.Validate(manifest);
        VerifyArtifact(paths, manifest.GenesisHistory!);
        string? expectedParent = null;
        RollingArtifactReference expectedHistory = manifest.GenesisHistory!;
        foreach (RollingCommittedBlock block in manifest.Blocks!)
        {
            if (!string.Equals(block.ParentBlockId, expectedParent, StringComparison.Ordinal)
                || block.HistoryBefore != expectedHistory)
            {
                throw new InvalidDataException($"Rolling block chain breaks at sequence {block.Sequence}.");
            }

            ValidateCommittedBlock(paths, block);
            expectedParent = block.BlockId;
            expectedHistory = block.HistoryAfter!;
        }

        VerifyArtifact(paths, manifest.HistoryHead!);
        EnsureNoSecretMaterial(_store.ReadText(paths.ManifestPath), "manifest");
    }

    private void ValidateCommittedBlock(RollingProgrammingPaths paths, RollingCommittedBlock block)
    {
        ProgrammingPaths programmingPaths = ProgrammingPaths.FromMediaRoot(paths.MediaRoot);
        _ = _committedBlockResolver.VerifyBlock(
            paths,
            GetPlannerId(paths),
            block,
            programmingPaths.LibraryRoot);
    }

    private string GetPlannerId(RollingProgrammingPaths paths) =>
        _store.LoadManifest(paths.ManifestPath).PlannerId!;

    private static RollingProgrammingManifest AppendBlock(
        RollingProgrammingManifest manifest,
        RollingCommittedBlock block)
    {
        var blocks = manifest.Blocks!.ToList();
        blocks.Add(block);
        var updated = new RollingProgrammingManifest
        {
            SchemaVersion = manifest.SchemaVersion,
            PlannerId = manifest.PlannerId,
            BaseSeed = manifest.BaseSeed,
            TargetBlockDurationSeconds = manifest.TargetBlockDurationSeconds,
            TargetPreparedBlockCount = manifest.TargetPreparedBlockCount,
            NextSequence = checked(block.Sequence + 1),
            GenesisHistory = manifest.GenesisHistory,
            HistoryHead = block.HistoryAfter,
            Blocks = blocks,
            InitializedAtUtc = manifest.InitializedAtUtc,
        };
        RollingManifestValidator.Validate(updated);
        return updated;
    }

    private static void ValidateIntent(
        RollingGenerationIntent intent,
        RollingProgrammingManifest manifest)
    {
        if (intent.SchemaVersion != RollingProgrammingPolicy.ArtifactSchemaVersion
            || !Guid.TryParseExact(intent.IntentId, "N", out _)
            || intent.Sequence != manifest.NextSequence
            || intent.Seed != RollingProgrammingSeed.Derive(
                manifest.PlannerId!,
                manifest.BaseSeed,
                manifest.NextSequence)
            || !string.Equals(
                intent.ParentBlockId,
                manifest.Blocks!.LastOrDefault()?.BlockId,
                StringComparison.Ordinal)
            || intent.HistoryBefore != manifest.HistoryHead
            || string.IsNullOrWhiteSpace(intent.InputSnapshotPath)
            || !RollingManifestValidator.IsSha256(intent.InputSnapshotSha256)
            || !string.Equals(
                intent.PlannerAlgorithmVersion,
                RollingProgrammingPolicy.PlannerAlgorithmVersion,
                StringComparison.Ordinal)
            || intent.GeneratedAtUtc == default
            || intent.PlannedScheduleStartUtc == default
            || intent.TargetDurationSeconds != manifest.TargetBlockDurationSeconds)
        {
            throw new InvalidDataException(
                $"Rolling generation intent for sequence {manifest.NextSequence} is inconsistent with the manifest.");
        }
    }

    private static void ValidateSnapshotAgainstIntent(
        RollingPlanningInputSnapshot snapshot,
        RollingGenerationIntent intent)
    {
        PlaylistPlanningSnapshotService.ValidateSnapshot(snapshot, requireReadiness: true);
        if (snapshot.Sequence != intent.Sequence
            || snapshot.Seed != intent.Seed
            || snapshot.GeneratedAtUtc != intent.GeneratedAtUtc
            || snapshot.PlannedScheduleStartUtc != intent.PlannedScheduleStartUtc
            || snapshot.TargetDurationSeconds != intent.TargetDurationSeconds
            || snapshot.HistoryBeforeHash != intent.HistoryBefore!.Sha256
            || !string.Equals(
                snapshot.PlannerAlgorithmVersion,
                intent.PlannerAlgorithmVersion,
                StringComparison.Ordinal)
            || RollingProgrammingJson.Sha256(snapshot.CatalogJson!) != snapshot.CatalogSnapshotHash
            || (snapshot.ProgrammingJson is not null
                && RollingProgrammingJson.Sha256(snapshot.ProgrammingJson) != snapshot.ProgrammingSnapshotHash))
        {
            throw new InvalidDataException(
                $"Frozen rolling input snapshot for sequence {intent.Sequence} is inconsistent with its intent.");
        }
    }

    private PlaylistHistoryDocument LoadRequestedGenesis(string? historyPath)
    {
        if (historyPath is null)
        {
            return PlaylistHistoryDocument.Empty;
        }

        string path = Path.GetFullPath(historyPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Planned history import not found: {path}", path);
        }

        return _historyStore.Load(path);
    }

    private PlaylistHistoryDocument LoadHistory(
        RollingProgrammingPaths paths,
        RollingArtifactReference reference)
    {
        string path = VerifyArtifact(paths, reference);
        return _historyStore.Load(path);
    }

    private string VerifyArtifact(
        RollingProgrammingPaths paths,
        RollingArtifactReference reference)
    {
        RollingManifestValidator.ValidateArtifactReference(reference, "artifact");
        string path = RollingPathSafety.ResolveExistingFile(paths.RollingRoot, reference.RelativePath);
        string actual = RollingProgrammingJson.Sha256File(path);
        if (!string.Equals(actual, reference.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Rolling artifact hash mismatch: {reference.RelativePath}");
        }

        EnsureNoSecretMaterial(_store.ReadText(path), reference.RelativePath);
        return path;
    }

    private void EnsureBroadcastReady(string playlistPath, string libraryRoot)
    {
        BroadcastPlan plan = _broadcastPlanner.CreatePlan([playlistPath], libraryRoot);
        if (!plan.IsReady)
        {
            string details = string.Join(
                "; ",
                plan.Issues.Select(issue => issue.Detail));
            throw new InvalidDataException(
                $"Generated rolling playlist failed broadcast readiness validation: {details}");
        }
    }

    private static void PublishImmutableDirectory(string source, string destination)
    {
        if (Directory.Exists(destination))
        {
            foreach (string name in new[] { "playlist.json", "input.json", "block.json" })
            {
                string expected = Path.Combine(source, name);
                string actual = Path.Combine(destination, name);
                if (!File.Exists(expected)
                    || !File.Exists(actual)
                    || !File.ReadAllBytes(expected).AsSpan().SequenceEqual(File.ReadAllBytes(actual)))
                {
                    throw new InvalidDataException(
                        $"Immutable rolling block directory already exists with different content: {destination}");
                }
            }

            DeleteDirectoryIfExists(source);
            return;
        }

        Directory.Move(source, destination);
    }

    private static void ResetUncommittedStageWithoutIntent(string stageDirectory)
    {
        if (Directory.Exists(stageDirectory))
        {
            EnsureDirectoryIsNotLink(stageDirectory);
            Directory.Delete(stageDirectory, recursive: true);
        }
    }

    private static void DeletePartialFiles(string stagingDirectory)
    {
        if (!Directory.Exists(stagingDirectory))
        {
            return;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (string file in Directory.EnumerateFiles(stagingDirectory, "*.partial", options))
        {
            File.Delete(file);
        }
    }

    private static void CleanCommittedStaging(
        RollingProgrammingPaths paths,
        RollingProgrammingManifest manifest)
    {
        if (!Directory.Exists(paths.StagingDirectory))
        {
            return;
        }

        foreach (string directory in Directory.EnumerateDirectories(paths.StagingDirectory))
        {
            if (long.TryParse(Path.GetFileName(directory), out long sequence)
                && sequence < manifest.NextSequence)
            {
                DeleteDirectoryIfExists(directory);
            }
        }
    }

    private static void QuarantineDirectory(
        RollingProgrammingPaths paths,
        string directory,
        string reason)
    {
        Directory.CreateDirectory(paths.OrphanedDirectory);
        string destination = Path.Combine(
            paths.OrphanedDirectory,
            $"{Path.GetFileName(directory)}-{reason}-{Guid.NewGuid():N}");
        Directory.Move(directory, destination);
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            EnsureDirectoryIsNotLink(path);
            Directory.Delete(path, recursive: true);
        }
    }

    private static void EnsureDirectoryIsNotLink(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null)
        {
            throw new InvalidDataException(
                $"Refusing to modify linked rolling staging directory: {path}");
        }
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

    private static int CountEntries(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFileSystemEntries(directory).Count()
        : 0;

    private static void ValidateManagedDirectories(RollingProgrammingPaths paths)
    {
        foreach (string relative in new[] { ".locks", ".staging", "blocks", "history", "orphaned" })
        {
            _ = RollingPathSafety.Resolve(paths.RollingRoot, relative);
        }
    }

    private static int CountUnreferencedBlockDirectories(
        RollingProgrammingPaths paths,
        RollingProgrammingManifest manifest) =>
        GetUnreferencedBlockDirectories(paths, manifest).Length;

    private void InspectUnreferencedBlocks(
        RollingProgrammingPaths paths,
        RollingProgrammingManifest manifest,
        ICollection<string> warnings)
    {
        string[] directories = GetUnreferencedBlockDirectories(paths, manifest);
        if (directories.Length == 0)
        {
            return;
        }

        int plausible = 0;
        foreach (string directory in directories)
        {
            string descriptorPath = Path.Combine(directory, "block.json");
            RollingBlockDescriptor descriptor = _store.Read<RollingBlockDescriptor>(
                descriptorPath,
                $"uncommitted rolling block descriptor '{descriptorPath}'");
            if (descriptor.SchemaVersion != RollingProgrammingPolicy.ArtifactSchemaVersion
                || descriptor.Block is null)
            {
                throw new InvalidDataException(
                    $"Uncommitted rolling block descriptor is invalid: {descriptorPath}");
            }

            RollingManifestValidator.ValidateBlock(descriptor.Block, descriptor: true);
            RollingCommittedBlock candidate = descriptor.Block with
            {
                DescriptorSha256 = RollingProgrammingJson.Sha256(_store.ReadText(descriptorPath)),
            };
            ValidateCommittedBlock(paths, candidate);
            bool exactNext = candidate.Sequence == manifest.NextSequence
                && string.Equals(
                    candidate.ParentBlockId,
                    manifest.Blocks!.LastOrDefault()?.BlockId,
                    StringComparison.Ordinal)
                && candidate.HistoryBefore == manifest.HistoryHead;
            if (!exactNext)
            {
                throw new InvalidDataException(
                    $"Uncommitted block {candidate.Sequence} does not match the exact next manifest chain position.");
            }

            plausible++;
        }

        if (plausible > 1)
        {
            throw new InvalidDataException(
                "Multiple plausible next rolling blocks exist. Automatic reconciliation must refuse to guess.");
        }

        warnings.Add("One exact uncommitted next block exists; run rolling maintain to verify and adopt it.");
    }

    private static string[] GetUnreferencedBlockDirectories(
        RollingProgrammingPaths paths,
        RollingProgrammingManifest manifest)
    {
        if (!Directory.Exists(paths.BlocksDirectory))
        {
            return [];
        }

        var referenced = manifest.Blocks!
            .Select(block => Path.GetDirectoryName(RollingPathSafety.Resolve(
                paths.RollingRoot,
                block.DescriptorPath!))!)
            .Select(Path.GetFullPath)
            .ToHashSet(GetPathComparer());
        return Directory.EnumerateDirectories(paths.BlocksDirectory)
            .Where(directory => !referenced.Contains(Path.GetFullPath(directory)))
            .ToArray();
    }

    private static int CreateRandomSeed()
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadInt32BigEndian(bytes);
    }

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
