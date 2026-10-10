using NzyteTv.Core;

namespace NzyteTv.Media;

public interface IRollingBlockMaintainer
{
    Task<RollingMaintainResult> EnsureCommittedThroughAsync(
        string mediaRoot,
        long requiredHighestSequence,
        CancellationToken cancellationToken);
}

public sealed class RollingPlanningBlockedException : Exception
{
    public RollingPlanningBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class RollingPlanningSafetyException : Exception
{
    public RollingPlanningSafetyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class RollingBlockMaintainer : IRollingBlockMaintainer
{
    private readonly IRollingPlanStore _store;
    private readonly IMediaToolLocator _mediaToolLocator;
    private readonly Func<IRollingProgrammingPlanner> _reconciliationPlannerFactory;
    private readonly Func<IPlaylistPlanningSnapshotService, IRollingProgrammingPlanner>
        _generationPlannerFactory;
    private readonly Func<string, IPlaylistPlanningSnapshotService> _snapshotServiceFactory;

    public RollingBlockMaintainer(
        IRollingPlanStore? store = null,
        IMediaToolLocator? mediaToolLocator = null,
        Func<IRollingProgrammingPlanner>? reconciliationPlannerFactory = null,
        Func<IPlaylistPlanningSnapshotService, IRollingProgrammingPlanner>? generationPlannerFactory = null,
        Func<string, IPlaylistPlanningSnapshotService>? snapshotServiceFactory = null,
        IAssetMetadataRepository? metadataRepository = null)
    {
        _store = store ?? new RollingPlanStore();
        _mediaToolLocator = mediaToolLocator ?? new MediaToolLocator();
        IAssetMetadataRepository repository = metadataRepository
            ?? new AdjacentAssetMetadataRepository();
        var committedBlockResolver = new RollingCommittedBlockResolver(
            _store,
            metadataRepository: repository);
        _reconciliationPlannerFactory = reconciliationPlannerFactory
            ?? (() => new RollingProgrammingPlanner(
                store: _store,
                committedBlockResolver: committedBlockResolver));
        _generationPlannerFactory = generationPlannerFactory
            ?? (snapshot => new RollingProgrammingPlanner(
                store: _store,
                snapshotService: snapshot,
                committedBlockResolver: committedBlockResolver));
        _snapshotServiceFactory = snapshotServiceFactory
            ?? (ffprobe => CreateSnapshotService(ffprobe, repository));
    }

    public async Task<RollingMaintainResult> EnsureCommittedThroughAsync(
        string mediaRoot,
        long requiredHighestSequence,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaRoot);
        if (requiredHighestSequence is < 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredHighestSequence),
                "The required rolling sequence is outside the supported manifest range.");
        }

        RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(mediaRoot);
        RollingProgrammingManifest before = _store.LoadManifest(paths.ManifestPath);
        RollingMaintainResult reconciled = await _reconciliationPlannerFactory()
            .MaintainAsync(
                mediaRoot,
                cancellationToken,
                committedBlockTarget: before.Blocks!.Count)
            .ConfigureAwait(false);
        if (reconciled.Manifest.Blocks!.Count >= requiredHighestSequence)
        {
            return reconciled with { TargetSatisfied = true };
        }

        // Recheck before initializing FFprobe because another serialized planner may have
        // satisfied the target after reconciliation released the writer lock.
        RollingProgrammingManifest current = _store.LoadManifest(paths.ManifestPath);
        if (current.Blocks!.Count >= requiredHighestSequence)
        {
            return reconciled with
            {
                Manifest = current,
                TargetSatisfied = true,
            };
        }

        string ffprobe = await _mediaToolLocator.LocateFfprobeAsync(cancellationToken)
            .ConfigureAwait(false);
        IPlaylistPlanningSnapshotService snapshotService = _snapshotServiceFactory(ffprobe);
        IRollingProgrammingPlanner planner = _generationPlannerFactory(snapshotService);
        try
        {
            return await planner.MaintainAsync(
                mediaRoot,
                cancellationToken,
                committedBlockTarget: checked((int)requiredHighestSequence))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RollingPlannerLockUnavailableException)
        {
            throw;
        }
        catch (ProgrammingConfigurationValidationException exception)
        {
            throw PlanningBlocked(exception);
        }
        catch (InvalidOperationException exception)
        {
            throw PlanningBlocked(exception);
        }
        catch (InvalidDataException exception)
        {
            await ConfirmCommittedPrefixStillSafeAsync(mediaRoot, cancellationToken, exception)
                .ConfigureAwait(false);
            throw PlanningBlocked(exception);
        }
    }

    private async Task ConfirmCommittedPrefixStillSafeAsync(
        string mediaRoot,
        CancellationToken cancellationToken,
        InvalidDataException planningException)
    {
        try
        {
            RollingProgrammingPaths paths = RollingProgrammingPaths.FromMediaRoot(mediaRoot);
            RollingProgrammingManifest manifest = _store.LoadManifest(paths.ManifestPath);
            _ = await _reconciliationPlannerFactory().MaintainAsync(
                mediaRoot,
                cancellationToken,
                committedBlockTarget: manifest.Blocks!.Count).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RollingPlannerLockUnavailableException)
        {
            throw;
        }
        catch (Exception verificationException) when (verificationException is
            FileNotFoundException or DirectoryNotFoundException or InvalidDataException or
            IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new RollingPlanningSafetyException(
                "Rolling planning safety validation failed after a generation error; " +
                "the committed chain was not extended.",
                new AggregateException(planningException, verificationException));
        }
    }

    private static RollingPlanningBlockedException PlanningBlocked(Exception exception) => new(
        StationSecretRedactor.RedactRtmpUrls(exception.Message)
            ?? "Future rolling programming generation is blocked.",
        exception);

    private static IPlaylistPlanningSnapshotService CreateSnapshotService(
        string ffprobe,
        IAssetMetadataRepository metadataRepository) =>
        new PlaylistPlanningSnapshotService(
            new SongCatalogStore(),
            new PlaylistLibraryLoader(
                new MediaAnalyzer(ffprobe, new ProcessRunner()),
                metadataRepository: metadataRepository),
            new PlaylistGenerator(),
            metadataRepository: metadataRepository);
}
