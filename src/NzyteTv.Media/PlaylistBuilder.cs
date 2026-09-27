using System.Globalization;
using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record PlaylistBuildRequest(
    string LibraryRoot,
    string CatalogPath,
    string OutputPath,
    TimeSpan TargetDuration,
    int? Seed,
    string? HistoryPath,
    bool DryRun);

public sealed record PlaylistBuildResult(
    PlaylistGenerationResult Generation,
    string OutputPath,
    string? HistoryPath,
    bool DryRun);

public sealed class PlaylistBuilder(
    ISongCatalogStore catalogStore,
    IPlaylistLibraryLoader libraryLoader,
    IPlaylistStore playlistStore,
    IPlaylistHistoryStore historyStore,
    PlaylistGenerator generator,
    PlaylistPolicy? basePolicy = null,
    TimeProvider? timeProvider = null)
{
    private readonly PlaylistPolicy _basePolicy = basePolicy ?? new PlaylistPolicy();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<PlaylistBuildResult> BuildAsync(
        PlaylistBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string outputPath = Path.GetFullPath(request.OutputPath);
        string? historyPath = string.IsNullOrWhiteSpace(request.HistoryPath)
            ? null
            : Path.GetFullPath(request.HistoryPath);
        if (historyPath is not null
            && string.Equals(outputPath, historyPath, GetPathComparison()))
        {
            throw new InvalidOperationException("Playlist output and history paths must be different.");
        }

        SongCatalog catalog = catalogStore.Load(request.CatalogPath);
        PlaylistLibrarySnapshot snapshot = await libraryLoader.LoadAsync(
            request.LibraryRoot,
            catalog,
            cancellationToken).ConfigureAwait(false);
        PlaylistHistoryDocument history = historyPath is null
            ? PlaylistHistoryDocument.Empty
            : historyStore.Load(historyPath);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        int seed = request.Seed ?? CreateDailySeed(now);
        PlaylistPolicy policy = _basePolicy with { TargetDuration = request.TargetDuration };
        PlaylistGenerationResult generation = generator.Generate(
            snapshot.EligibleAssets,
            snapshot.ExcludedAssets,
            history,
            policy,
            seed,
            now);
        if (!request.DryRun)
        {
            await playlistStore.WriteAsync(outputPath, generation.Playlist, cancellationToken)
                .ConfigureAwait(false);
            if (historyPath is not null)
            {
                await historyStore.WriteAsync(historyPath, generation.UpdatedHistory, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new PlaylistBuildResult(generation, outputPath, historyPath, request.DryRun);
    }

    private static int CreateDailySeed(DateTimeOffset value) =>
        int.Parse(value.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
