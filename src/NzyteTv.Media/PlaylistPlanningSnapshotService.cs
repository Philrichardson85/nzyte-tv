using System.Text;
using System.Text.Json;
using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record PlaylistPlanningSnapshotRequest(
    string LibraryRoot,
    string CatalogPath,
    PlaylistHistoryDocument HistoryBefore,
    TimeSpan TargetDuration,
    int Seed,
    DateTimeOffset GeneratedAtUtc,
    long Sequence = 0,
    bool CaptureReadiness = false,
    string? HistoryBeforeHash = null);

public interface IPlaylistPlanningSnapshotService
{
    Task<RollingPlanningInputSnapshot> CaptureAsync(
        PlaylistPlanningSnapshotRequest request,
        CancellationToken cancellationToken);

    PlaylistGenerationResult Generate(RollingPlanningInputSnapshot snapshot);

    Task VerifySelectedReadinessAsync(
        RollingPlanningInputSnapshot snapshot,
        PlaylistDocument playlist,
        string libraryRoot,
        CancellationToken cancellationToken);
}

public sealed class PlaylistPlanningSnapshotService(
    ISongCatalogStore catalogStore,
    IPlaylistLibraryLoader libraryLoader,
    PlaylistGenerator generator,
    PlaylistPolicy? basePolicy = null,
    IProgrammingConfigurationStore? programmingStore = null,
    IAssetMetadataRepository? metadataRepository = null) : IPlaylistPlanningSnapshotService
{
    private static readonly JsonSerializerOptions CatalogReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly PlaylistPolicy _basePolicy = basePolicy ?? new PlaylistPolicy();
    private readonly IProgrammingConfigurationStore _programmingStore =
        programmingStore ?? new ProgrammingConfigurationStore();
    private readonly IAssetMetadataRepository _metadataRepository =
        metadataRepository ?? new AdjacentAssetMetadataRepository();

    public async Task<RollingPlanningInputSnapshot> CaptureAsync(
        PlaylistPlanningSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LibraryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CatalogPath);
        ArgumentNullException.ThrowIfNull(request.HistoryBefore);
        if (request.TargetDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Target duration must be positive.");
        }

        string catalogPath = Path.GetFullPath(request.CatalogPath);
        CapturedJson<SongCatalog> catalogCapture = CaptureStableJson(
            catalogPath,
            () => catalogStore.Load(catalogPath));
        SongCatalog catalog = catalogCapture.Value;
        IAssetMetadataSnapshot metadataSnapshot = _metadataRepository.Pin(
            AssetMetadataTree.Library,
            request.LibraryRoot);
        PlaylistLibrarySnapshot library = await libraryLoader.LoadAsync(
            request.LibraryRoot,
            catalog,
            metadataSnapshot,
            cancellationToken).ConfigureAwait(false);

        string programmingPath = ProgrammingConfigurationStore.GetPathForCatalog(catalogPath);
        CapturedJson<ProgrammingConfiguration>? programmingCapture = CaptureOptionalStableJson(
            programmingPath,
            () => _programmingStore.LoadIfExists(programmingPath));
        ProgrammingConfiguration? programming = programmingCapture?.Value;
        if (programming is not null)
        {
            ValidateProgramming(programming, catalog, library);
        }

        PlaylistAsset[] eligible = library.EligibleAssets
            .OrderBy(asset => asset.RelativePath, StringComparer.Ordinal)
            .ThenBy(asset => asset.AssetId, StringComparer.Ordinal)
            .ToArray();
        PlaylistExclusion[] excluded = library.ExcludedAssets
            .OrderBy(asset => asset.RelativePath, StringComparer.Ordinal)
            .ToArray();
        RollingAssetReadinessSnapshot[] readiness = request.CaptureReadiness
            ? CaptureAssetReadiness(request.LibraryRoot, eligible, metadataSnapshot)
            : [];

        string historyJson = RollingProgrammingJson.SerializeCanonical(request.HistoryBefore);
        DateTimeOffset scheduleStart = request.HistoryBefore.ScheduleEndUtc is DateTimeOffset end
            && end > request.GeneratedAtUtc
                ? end
                : request.GeneratedAtUtc;

        return new RollingPlanningInputSnapshot
        {
            Sequence = request.Sequence,
            Seed = request.Seed,
            TargetDurationSeconds = request.TargetDuration.TotalSeconds,
            GeneratedAtUtc = request.GeneratedAtUtc,
            PlannedScheduleStartUtc = scheduleStart,
            PlannerAlgorithmVersion = RollingProgrammingPolicy.PlannerAlgorithmVersion,
            CatalogJson = catalogCapture.Json,
            CatalogSnapshotHash = catalogCapture.Hash,
            ProgrammingJson = programmingCapture?.Json,
            ProgrammingSnapshotHash = programmingCapture?.Hash
                ?? RollingProgrammingPolicy.LegacyProgrammingSnapshotMarker,
            ProgrammingConfiguration = programming,
            InventorySnapshotHash = CalculateInventorySnapshotHash(eligible, excluded, readiness),
            AssetMetadataGenerationId = metadataSnapshot.Identity.GenerationId,
            AssetMetadataRevision = metadataSnapshot.Identity.Revision,
            HistoryBefore = request.HistoryBefore,
            HistoryBeforeHash = request.HistoryBeforeHash ?? RollingProgrammingJson.Sha256(historyJson),
            EligibleAssets = eligible,
            ExcludedAssets = excluded,
            AssetReadiness = readiness,
        };
    }

    public PlaylistGenerationResult Generate(RollingPlanningInputSnapshot snapshot)
    {
        ValidateSnapshot(snapshot, requireReadiness: false);
        PlaylistPolicy policy = _basePolicy with
        {
            TargetDuration = TimeSpan.FromSeconds(snapshot.TargetDurationSeconds),
        };
        PlaylistGenerationResult result = generator.Generate(
            snapshot.EligibleAssets!,
            snapshot.ExcludedAssets!,
            snapshot.HistoryBefore!,
            policy,
            snapshot.Seed,
            snapshot.GeneratedAtUtc,
            snapshot.ProgrammingConfiguration);
        if (result.Playlist.ScheduleStartUtc != snapshot.PlannedScheduleStartUtc)
        {
            throw new InvalidDataException(
                "The generated playlist schedule start does not match its frozen planning snapshot.");
        }

        return result;
    }

    public Task VerifySelectedReadinessAsync(
        RollingPlanningInputSnapshot snapshot,
        PlaylistDocument playlist,
        string libraryRoot,
        CancellationToken cancellationToken) =>
        VerifyCapturedReadiness(
            snapshot,
            playlist,
            libraryRoot,
            cancellationToken,
            _metadataRepository);

    public static Task VerifyCapturedReadiness(
        RollingPlanningInputSnapshot snapshot,
        PlaylistDocument playlist,
        string libraryRoot,
        CancellationToken cancellationToken,
        IAssetMetadataRepository? metadataRepository = null)
    {
        ValidateSnapshot(snapshot, requireReadiness: true);
        ArgumentNullException.ThrowIfNull(playlist);
        string root = Path.GetFullPath(libraryRoot);
        AssetMetadataSnapshotIdentity metadataIdentity = GetMetadataIdentity(snapshot);
        IAssetMetadataSnapshot metadataSnapshot = (metadataRepository
                ?? new AdjacentAssetMetadataRepository())
            .Pin(AssetMetadataTree.Library, root, metadataIdentity);
        var captured = snapshot.AssetReadiness!.ToDictionary(
            value => (value.AssetId, NormalizeRelativePath(value.RelativePath)),
            value => value,
            EqualityComparer<(string, string)>.Default);

        foreach (PlaylistItem item in playlist.Items
            .DistinctBy(value => (value.AssetId, NormalizeRelativePath(value.RelativePath))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = NormalizeRelativePath(item.RelativePath);
            if (!captured.TryGetValue((item.AssetId, relativePath), out RollingAssetReadinessSnapshot? expected))
            {
                throw new InvalidDataException(
                    $"Selected asset '{item.AssetId}' is absent from the frozen readiness snapshot.");
            }

            string mediaPath = ResolveContainedPath(root, relativePath);
            var file = new FileInfo(mediaPath);
            bool metadataExists = metadataSnapshot.Exists(relativePath);
            byte[]? metadataContent = metadataExists
                ? metadataSnapshot.Read(relativePath).JsonBytes
                : null;
            if (!file.Exists
                || file.Length != expected.MediaLength
                || file.LastWriteTimeUtc != expected.MediaLastWriteUtc.UtcDateTime
                || !File.Exists(SourceManifestStore.GetManifestPath(mediaPath))
                || metadataContent is null
                || !string.Equals(
                    RollingProgrammingJson.Sha256File(SourceManifestStore.GetManifestPath(mediaPath)),
                    expected.TechnicalManifestSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    RollingProgrammingJson.Sha256(metadataContent),
                    expected.ProgrammingMetadataSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Selected asset '{item.AssetId}' changed after its planning input was captured.");
            }
        }

        return Task.CompletedTask;
    }

    public static void ValidateSnapshot(
        RollingPlanningInputSnapshot snapshot,
        bool requireReadiness)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != RollingProgrammingPolicy.ArtifactSchemaVersion
            || snapshot.Sequence < 0
            || !double.IsFinite(snapshot.TargetDurationSeconds)
            || snapshot.TargetDurationSeconds <= 0
            || snapshot.GeneratedAtUtc == default
            || snapshot.PlannedScheduleStartUtc == default
            || string.IsNullOrWhiteSpace(snapshot.PlannerAlgorithmVersion)
            || string.IsNullOrWhiteSpace(snapshot.CatalogJson)
            || !IsSha256(snapshot.CatalogSnapshotHash)
            || string.IsNullOrWhiteSpace(snapshot.ProgrammingSnapshotHash)
            || !IsSha256(snapshot.InventorySnapshotHash)
            || snapshot.HistoryBefore is null
            || !IsSha256(snapshot.HistoryBeforeHash)
            || snapshot.EligibleAssets is null
            || snapshot.ExcludedAssets is null
            || snapshot.AssetReadiness is null
            || (snapshot.AssetMetadataGenerationId is null) != (snapshot.AssetMetadataRevision is null)
            || snapshot.AssetMetadataRevision is <= 0
            || (snapshot.AssetMetadataGenerationId is not null
                && !IsValidGenerationId(snapshot.AssetMetadataGenerationId))
            || (requireReadiness && snapshot.AssetReadiness.Count != snapshot.EligibleAssets.Count))
        {
            throw new InvalidDataException("Rolling planning input snapshot is missing or invalid.");
        }

        if (snapshot.ProgrammingConfiguration is null)
        {
            if (snapshot.ProgrammingJson is not null
                || !string.Equals(
                    snapshot.ProgrammingSnapshotHash,
                    RollingProgrammingPolicy.LegacyProgrammingSnapshotMarker,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Legacy programming snapshot marker is inconsistent.");
            }
        }
        else if (string.IsNullOrWhiteSpace(snapshot.ProgrammingJson)
            || !IsSha256(snapshot.ProgrammingSnapshotHash))
        {
            throw new InvalidDataException("Programming configuration snapshot is inconsistent.");
        }


        ValidateContentIdentity(snapshot);
    }

    public static string CalculateInventorySnapshotHash(
        IReadOnlyList<PlaylistAsset> eligible,
        IReadOnlyList<PlaylistExclusion> excluded,
        IReadOnlyList<RollingAssetReadinessSnapshot> readiness)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(excluded);
        ArgumentNullException.ThrowIfNull(readiness);
        string inventoryJson = RollingProgrammingJson.SerializeCanonical(new
        {
            eligible,
            excluded,
            readiness = readiness.Select(value => new
            {
                value.AssetId,
                value.RelativePath,
                value.MediaLength,
                value.TechnicalManifestSha256,
                value.ProgrammingMetadataSha256,
            }),
        });
        return RollingProgrammingJson.Sha256(inventoryJson);
    }

    private static void ValidateContentIdentity(RollingPlanningInputSnapshot snapshot)
    {
        if (!string.Equals(
                RollingProgrammingJson.Sha256(snapshot.CatalogJson!),
                snapshot.CatalogSnapshotHash,
                StringComparison.Ordinal)
            || !string.Equals(
                CalculateInventorySnapshotHash(
                    snapshot.EligibleAssets!,
                    snapshot.ExcludedAssets!,
                    snapshot.AssetReadiness!),
                snapshot.InventorySnapshotHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Rolling planning input snapshot content hash is inconsistent.");
        }

        try
        {
            SongCatalog? catalog = JsonSerializer.Deserialize<SongCatalog>(
                snapshot.CatalogJson!,
                CatalogReadOptions);
            SongCatalogValidator.Validate(catalog);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Malformed rolling catalog snapshot: {exception.Message}",
                exception);
        }
        if (snapshot.ProgrammingConfiguration is null)
        {
            return;
        }

        if (!string.Equals(
            RollingProgrammingJson.Sha256(snapshot.ProgrammingJson!),
            snapshot.ProgrammingSnapshotHash,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException("Rolling programming configuration snapshot hash is inconsistent.");
        }

        ProgrammingConfiguration raw = RollingProgrammingJson.Deserialize<ProgrammingConfiguration>(
            snapshot.ProgrammingJson!,
            "rolling programming configuration snapshot");
        ProgrammingConfigurationValidator.ValidateStructure(raw);
        if (!string.Equals(
            RollingProgrammingJson.SerializeCanonical(NormalizeProgramming(raw)),
            RollingProgrammingJson.SerializeCanonical(NormalizeProgramming(snapshot.ProgrammingConfiguration)),
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Rolling programming configuration content does not match its parsed snapshot.");
        }
    }

    private static ProgrammingConfiguration NormalizeProgramming(ProgrammingConfiguration configuration) =>
        configuration with
        {
            AssetOverrides = (configuration.AssetOverrides
                    ?? new Dictionary<string, AssetEditorialOverride>(StringComparer.Ordinal))
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
        };

    private static void ValidateProgramming(
        ProgrammingConfiguration programming,
        SongCatalog catalog,
        PlaylistLibrarySnapshot library)
    {
        var inventory = library.KnownAssetIds
            .ToDictionary(
                assetId => assetId,
                assetId => new ProgrammingAssetInventoryEntry(
                    assetId,
                    ContentGroupId: null,
                    AssetTypes.Special,
                    IsTechnicallyPlaylistEligible: false),
                StringComparer.Ordinal);
        foreach (PlaylistAsset asset in library.EligibleAssets)
        {
            inventory[asset.AssetId] = new ProgrammingAssetInventoryEntry(
                asset.AssetId,
                asset.ContentGroupId,
                asset.Type,
                IsTechnicallyPlaylistEligible: true);
        }

        ProgrammingConfigurationValidator.Validate(programming, catalog, inventory.Values.ToArray());
    }

    private static RollingAssetReadinessSnapshot[] CaptureAssetReadiness(
        string libraryRoot,
        IReadOnlyList<PlaylistAsset> assets,
        IAssetMetadataSnapshot metadataSnapshot)
    {
        string root = Path.GetFullPath(libraryRoot);
        var results = new List<RollingAssetReadinessSnapshot>(assets.Count);
        foreach (PlaylistAsset asset in assets)
        {
            string relativePath = NormalizeRelativePath(asset.RelativePath);
            string mediaPath = ResolveContainedPath(root, relativePath);
            var media = new FileInfo(mediaPath);
            string manifestPath = SourceManifestStore.GetManifestPath(mediaPath);
            if (!media.Exists || !File.Exists(manifestPath) || !metadataSnapshot.Exists(relativePath))
            {
                throw new InvalidDataException(
                    $"Eligible asset '{asset.AssetId}' is missing media or required readiness sidecars.");
            }

            AssetMetadataDocument document = metadataSnapshot.Read(relativePath);
            AssetMetadata metadata = document.Metadata;
            if (!string.Equals(metadata.AssetId, asset.AssetId, StringComparison.Ordinal)
                || !string.Equals(metadata.ContentGroupId, asset.ContentGroupId, StringComparison.Ordinal)
                || !string.Equals(metadata.Title, asset.Title, StringComparison.Ordinal)
                || !string.Equals(metadata.Artist, asset.Artist, StringComparison.Ordinal)
                || !string.Equals(metadata.Type, asset.Type, StringComparison.Ordinal)
                || !string.Equals(metadata.Subtype, asset.Subtype, StringComparison.Ordinal)
                || metadata.RotationStartDate != asset.RotationStartDate)
            {
                throw new IOException(
                    $"Programming metadata changed while planning asset '{asset.AssetId}' was captured.");
            }

            results.Add(new RollingAssetReadinessSnapshot(
                asset.AssetId,
                relativePath,
                media.Length,
                media.LastWriteTimeUtc,
                RollingProgrammingJson.Sha256File(manifestPath),
                RollingProgrammingJson.Sha256(document.JsonBytes)));
        }

        return results
            .OrderBy(value => value.RelativePath, StringComparer.Ordinal)
            .ThenBy(value => value.AssetId, StringComparer.Ordinal)
            .ToArray();
    }

    private static CapturedJson<T> CaptureStableJson<T>(string path, Func<T> loader)
        where T : class
    {
        if (!File.Exists(path))
        {
            T fallback = loader();
            string canonical = RollingProgrammingJson.SerializeCanonical(fallback);
            return new CapturedJson<T>(fallback, canonical, RollingProgrammingJson.Sha256(canonical));
        }

        byte[] before = File.ReadAllBytes(path);
        T value = loader();
        byte[] after = File.ReadAllBytes(path);
        if (!before.AsSpan().SequenceEqual(after))
        {
            throw new IOException($"Planning input changed while it was being captured: {path}");
        }

        return new CapturedJson<T>(
            value,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(before),
            RollingProgrammingJson.Sha256(before));
    }

    private static CapturedJson<T>? CaptureOptionalStableJson<T>(string path, Func<T?> loader)
        where T : class
    {
        if (!File.Exists(path))
        {
            T? value = loader();
            if (value is null)
            {
                return null;
            }

            if (File.Exists(path))
            {
                return CaptureStableJson(path, () => loader()
                    ?? throw new IOException($"Optional planning input disappeared while it was being captured: {path}"));
            }

            string canonical = RollingProgrammingJson.SerializeCanonical(value);
            return new CapturedJson<T>(value, canonical, RollingProgrammingJson.Sha256(canonical));
        }

        return CaptureStableJson(path, () => loader()
            ?? throw new IOException($"Optional planning input disappeared while it was being captured: {path}"));
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Planning asset path must be relative: {relativePath}");
        }

        string path = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", GetPathComparison())
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", GetPathComparison()))
        {
            throw new InvalidDataException($"Planning asset path escapes the library root: {relativePath}");
        }

        return path;
    }

    private static string NormalizeRelativePath(string value) => value.Replace('\\', '/');

    private static AssetMetadataSnapshotIdentity GetMetadataIdentity(RollingPlanningInputSnapshot snapshot) =>
        snapshot.AssetMetadataGenerationId is null
            ? new AssetMetadataSnapshotIdentity(AssetMetadataStorageMode.Adjacent)
            : new AssetMetadataSnapshotIdentity(
                AssetMetadataStorageMode.ExternalGeneration,
                snapshot.AssetMetadataRevision,
                snapshot.AssetMetadataGenerationId);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsValidGenerationId(string value) =>
        value.Length == 12
        && value.Any(character => character != '0')
        && value.All(character => character is >= '0' and <= '9');

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record CapturedJson<T>(T Value, string Json, string Hash);
}
