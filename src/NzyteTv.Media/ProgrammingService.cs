using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record ProgrammingPaths(
    string MediaRoot,
    string LibraryRoot,
    string CatalogPath,
    string ConfigurationPath)
{
    public static ProgrammingPaths FromMediaRoot(string mediaRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaRoot);
        string root = Path.GetFullPath(mediaRoot);
        string catalogDirectory = Path.Combine(root, "catalog");
        return new ProgrammingPaths(
            root,
            Path.Combine(root, "library"),
            Path.Combine(catalogDirectory, "song-catalog.json"),
            Path.Combine(catalogDirectory, ProgrammingConfigurationStore.FileName));
    }
}

public sealed record ProgrammingInventory(
    IReadOnlyList<ProgrammingAssetInventoryEntry> Assets,
    IReadOnlyList<string> Diagnostics);

public interface IProgrammingInventoryLoader
{
    ProgrammingInventory Load(string libraryRoot, SongCatalog catalog);
}

public sealed class ProgrammingInventoryLoader(
    IAssetMetadataStore? metadataStore = null,
    IAssetMetadataRepository? metadataRepository = null) : IProgrammingInventoryLoader
{
    private readonly IAssetMetadataRepository _metadataRepository = metadataRepository
        ?? new AdjacentAssetMetadataRepository(metadataStore);

    public ProgrammingInventory Load(string libraryRoot, SongCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(catalog);
        SongCatalogValidator.Validate(catalog);
        string root = Path.GetFullPath(libraryRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Normalized library root not found: {root}");
        }

        IAssetMetadataSnapshot metadataSnapshot = _metadataRepository.Pin(
            AssetMetadataTree.Library,
            root);
        var assets = new List<ProgrammingAssetInventoryEntry>();
        var diagnostics = new List<string>();
        foreach (string mediaPath in PlaylistLibraryLoader.DiscoverCandidateMediaPaths(
            root,
            metadataSnapshot.DiscoverRelativeMediaPaths(),
            includeAdjacentMetadataCandidates:
                metadataSnapshot.Identity.Mode == AssetMetadataStorageMode.Adjacent))
        {
            string relativePath = Path.GetRelativePath(root, mediaPath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            if (!metadataSnapshot.Exists(relativePath))
            {
                continue;
            }

            try
            {
                AssetMetadata metadata = metadataSnapshot.Read(relativePath).Metadata;
                AssetEligibilityResult eligibility = AssetEligibilityEvaluator.Evaluate(
                    metadata,
                    catalog,
                    File.Exists(mediaPath),
                    File.Exists(SourceManifestStore.GetManifestPath(mediaPath)),
                    programmingMetadataExists: true);
                assets.Add(new ProgrammingAssetInventoryEntry(
                    metadata.AssetId!,
                    metadata.ContentGroupId,
                    metadata.Type!,
                    eligibility.IsPlaylistEligible));
            }
            catch (Exception exception) when (exception is
                InvalidDataException or AssetMetadataValidationException or IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(exception.Message);
            }
        }

        return new ProgrammingInventory(
            assets.OrderBy(asset => asset.AssetId, StringComparer.Ordinal).ToArray(),
            diagnostics.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
}

public sealed record ProgrammingValidationResult(
    ProgrammingPaths Paths,
    ProgrammingConfiguration? Configuration,
    ProgrammingInventory? Inventory,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0 && Configuration is not null;
}

public sealed record ProgrammingMutationResult(
    ProgrammingPaths Paths,
    ProgrammingConfiguration Configuration,
    bool Changed,
    string Description);

public sealed class ProgrammingService(
    IProgrammingConfigurationStore? configurationStore = null,
    ISongCatalogStore? catalogStore = null,
    IProgrammingInventoryLoader? inventoryLoader = null,
    IProgrammingConfigurationMutationLock? mutationLock = null)
{
    private readonly IProgrammingConfigurationStore _configurationStore =
        configurationStore ?? new ProgrammingConfigurationStore();
    private readonly ISongCatalogStore _catalogStore = catalogStore ?? new SongCatalogStore();
    private readonly IProgrammingInventoryLoader _inventoryLoader =
        inventoryLoader ?? new ProgrammingInventoryLoader();
    private readonly IProgrammingConfigurationMutationLock _mutationLock =
        mutationLock ?? new ProgrammingConfigurationMutationLock();

    public async Task<ProgrammingConfigurationInitializationResult> InitializeAsync(
        string mediaRoot,
        CancellationToken cancellationToken)
    {
        ProgrammingPaths paths = ProgrammingPaths.FromMediaRoot(mediaRoot);
        if (!Directory.Exists(paths.MediaRoot))
        {
            throw new DirectoryNotFoundException($"Media root not found: {paths.MediaRoot}");
        }

        return await _configurationStore.InitializeAsync(paths.ConfigurationPath, cancellationToken)
            .ConfigureAwait(false);
    }

    public ProgrammingValidationResult Validate(string mediaRoot)
    {
        ProgrammingPaths paths = ProgrammingPaths.FromMediaRoot(mediaRoot);
        var errors = new List<string>();
        var warnings = new List<string>();
        ProgrammingConfiguration? configuration = null;
        ProgrammingInventory? inventory = null;

        try
        {
            configuration = _configurationStore.Load(paths.ConfigurationPath);
            SongCatalog catalog = _catalogStore.Load(paths.CatalogPath);
            inventory = _inventoryLoader.Load(paths.LibraryRoot, catalog);
            errors.AddRange(ProgrammingConfigurationValidator.GetReferenceErrors(
                configuration,
                catalog,
                inventory.Assets));

            foreach (IGrouping<string, ProgrammingAssetInventoryEntry> duplicate in inventory.Assets
                .GroupBy(asset => asset.AssetId, StringComparer.Ordinal)
                .Where(group => group.Count() > 1))
            {
                errors.Add($"Programming inventory contains duplicate assetId '{duplicate.Key}'.");
            }

            if (inventory.Diagnostics.Count > 0)
            {
                warnings.AddRange(inventory.Diagnostics.Select(value => $"Inventory metadata: {value}"));
            }

            ProgrammingAssetInventoryEntry[] eligibleSubstantial = inventory.Assets
                .Where(asset => asset.IsTechnicallyPlaylistEligible
                    && ProgrammingContentClassifier.IsSubstantial(asset.Type)
                    && !configuration.GetAssetOverride(asset.AssetId).DoNotAir)
                .ToArray();
            if (eligibleSubstantial.Length == 0
                && !errors.Any(error => error.Contains("exclude every", StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add("No technically eligible substantial programming assets are currently available.");
            }

            ActiveCampaign campaign = configuration.ActiveCampaign!;
            if (campaign.Enabled
                && !inventory.Assets.Any(asset => asset.IsTechnicallyPlaylistEligible
                    && string.Equals(asset.ContentGroupId, campaign.ContentGroupId, StringComparison.Ordinal)
                    && !configuration.GetAssetOverride(asset.AssetId).DoNotAir))
            {
                warnings.Add("The active campaign currently has no eligible on-air presentation.");
            }
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException or InvalidDataException or
            UnauthorizedAccessException or IOException or CatalogValidationException or
            ProgrammingConfigurationValidationException)
        {
            errors.Add(exception.Message);
        }

        return new ProgrammingValidationResult(paths, configuration, inventory, errors, warnings);
    }

    public async Task<ProgrammingMutationResult> SetCampaignAsync(
        string mediaRoot,
        string contentGroupId,
        double? weightMultiplier,
        CancellationToken cancellationToken) => await SetCampaignAsync(
            mediaRoot,
            contentGroupId,
            weightMultiplier,
            expectedRevision: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<ProgrammingMutationResult> SetCampaignAsync(
        string mediaRoot,
        string contentGroupId,
        double? weightMultiplier,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentGroupId);
        ProgrammingPaths paths = ProgrammingPaths.FromMediaRoot(mediaRoot);
        await using IAsyncDisposable lease = await _mutationLock.AcquireAsync(
            paths.ConfigurationPath,
            cancellationToken).ConfigureAwait(false);
        LoadedProgramming loaded = LoadForMutation(mediaRoot);
        VerifyExpectedRevision(loaded.Configuration, expectedRevision);
        double weight = weightMultiplier ?? ProgrammingConfiguration.DefaultCampaignMultiplier;
        var campaign = new ActiveCampaign
        {
            Enabled = true,
            ContentGroupId = contentGroupId,
            WeightMultiplier = weight,
        };
        ProgrammingConfiguration current = loaded.Configuration;
        if (current.ActiveCampaign == campaign && expectedRevision is null)
        {
            return new ProgrammingMutationResult(
                loaded.Paths,
                current,
                Changed: false,
                $"Active campaign already targets '{contentGroupId}'.");
        }

        ProgrammingConfiguration updated = current with
        {
            Revision = checked(current.Revision + 1),
            ActiveCampaign = campaign,
        };
        await ValidateAndWriteAsync(loaded, updated, cancellationToken).ConfigureAwait(false);
        return new ProgrammingMutationResult(
            loaded.Paths,
            updated,
            Changed: true,
            $"Active campaign set to '{contentGroupId}'.");
    }

    public async Task<ProgrammingMutationResult> ClearCampaignAsync(
        string mediaRoot,
        CancellationToken cancellationToken) => await ClearCampaignAsync(
            mediaRoot,
            expectedRevision: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<ProgrammingMutationResult> ClearCampaignAsync(
        string mediaRoot,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ProgrammingPaths paths = ProgrammingPaths.FromMediaRoot(mediaRoot);
        await using IAsyncDisposable lease = await _mutationLock.AcquireAsync(
            paths.ConfigurationPath,
            cancellationToken).ConfigureAwait(false);
        LoadedProgramming loaded = LoadStructurallyValidConfiguration(mediaRoot);
        ProgrammingConfiguration current = loaded.Configuration;
        VerifyExpectedRevision(current, expectedRevision);
        var cleared = new ActiveCampaign();
        if (current.ActiveCampaign == cleared && expectedRevision is null)
        {
            return new ProgrammingMutationResult(
                loaded.Paths,
                current,
                Changed: false,
                "Active campaign is already clear.");
        }

        ProgrammingConfiguration updated = current with
        {
            Revision = checked(current.Revision + 1),
            ActiveCampaign = cleared,
        };
        await ValidateAndWriteAsync(loaded, updated, cancellationToken).ConfigureAwait(false);
        return new ProgrammingMutationResult(
            loaded.Paths,
            updated,
            Changed: true,
            "Active campaign cleared.");
    }

    public async Task<ProgrammingMutationResult> SetAssetOverrideAsync(
        string mediaRoot,
        string assetId,
        bool? doNotAir,
        double? weightMultiplier,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        if (doNotAir is null && weightMultiplier is null)
        {
            throw new ArgumentException("At least one asset editorial value must be supplied.", nameof(doNotAir));
        }

        ProgrammingPaths paths = ProgrammingPaths.FromMediaRoot(mediaRoot);
        await using IAsyncDisposable lease = await _mutationLock.AcquireAsync(
            paths.ConfigurationPath,
            cancellationToken).ConfigureAwait(false);
        LoadedProgramming loaded = LoadForMutation(mediaRoot);
        if (!loaded.Inventory.Assets.Any(asset => string.Equals(asset.AssetId, assetId, StringComparison.Ordinal)))
        {
            throw new ProgrammingConfigurationValidationException(
                [$"Asset override references unknown assetId '{assetId}'."]);
        }

        ProgrammingConfiguration current = loaded.Configuration;
        AssetEditorialOverride existing = current.GetAssetOverride(assetId);
        var replacement = existing with
        {
            DoNotAir = doNotAir ?? existing.DoNotAir,
            WeightMultiplier = weightMultiplier ?? existing.WeightMultiplier,
        };
        var overrides = new Dictionary<string, AssetEditorialOverride>(
            current.AssetOverrides!,
            StringComparer.Ordinal);
        if (!replacement.DoNotAir && replacement.WeightMultiplier == 1.0)
        {
            overrides.Remove(assetId);
        }
        else
        {
            overrides[assetId] = replacement;
        }

        bool changed = !OverridesEqual(current.AssetOverrides!, overrides);
        if (!changed)
        {
            return new ProgrammingMutationResult(
                loaded.Paths,
                current,
                Changed: false,
                $"Asset override for '{assetId}' is unchanged.");
        }

        ProgrammingConfiguration updated = current with
        {
            Revision = checked(current.Revision + 1),
            AssetOverrides = overrides,
        };
        await ValidateAndWriteAsync(loaded, updated, cancellationToken).ConfigureAwait(false);
        return new ProgrammingMutationResult(
            loaded.Paths,
            updated,
            Changed: true,
            $"Asset override for '{assetId}' updated.");
    }

    public async Task<ProgrammingMutationResult> ResetAssetOverrideAsync(
        string mediaRoot,
        string assetId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ProgrammingPaths paths = ProgrammingPaths.FromMediaRoot(mediaRoot);
        await using IAsyncDisposable lease = await _mutationLock.AcquireAsync(
            paths.ConfigurationPath,
            cancellationToken).ConfigureAwait(false);
        LoadedProgramming loaded = LoadStructurallyValidConfiguration(mediaRoot);
        ProgrammingConfiguration current = loaded.Configuration;
        var overrides = new Dictionary<string, AssetEditorialOverride>(
            current.AssetOverrides!,
            StringComparer.Ordinal);
        if (!overrides.Remove(assetId))
        {
            return new ProgrammingMutationResult(
                loaded.Paths,
                current,
                Changed: false,
                $"Asset '{assetId}' already uses programming defaults.");
        }

        ProgrammingConfiguration updated = current with
        {
            Revision = checked(current.Revision + 1),
            AssetOverrides = overrides,
        };
        await ValidateAndWriteAsync(loaded, updated, cancellationToken).ConfigureAwait(false);
        return new ProgrammingMutationResult(
            loaded.Paths,
            updated,
            Changed: true,
            $"Asset override for '{assetId}' reset.");
    }

    private LoadedProgramming LoadForMutation(string mediaRoot)
    {
        LoadedProgramming loaded = LoadStructurallyValidConfiguration(mediaRoot);
        ProgrammingConfigurationValidator.Validate(
            loaded.Configuration,
            loaded.Catalog,
            loaded.Inventory.Assets);
        return loaded;
    }

    private LoadedProgramming LoadStructurallyValidConfiguration(string mediaRoot)
    {
        ProgrammingPaths paths = ProgrammingPaths.FromMediaRoot(mediaRoot);
        ProgrammingConfiguration configuration = _configurationStore.Load(paths.ConfigurationPath);
        SongCatalog catalog = _catalogStore.Load(paths.CatalogPath);
        ProgrammingInventory inventory = _inventoryLoader.Load(paths.LibraryRoot, catalog);
        string[] duplicateAssetIds = inventory.Assets
            .GroupBy(asset => asset.AssetId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (duplicateAssetIds.Length > 0)
        {
            throw new ProgrammingConfigurationValidationException(
                duplicateAssetIds.Select(assetId =>
                    $"Programming inventory contains duplicate assetId '{assetId}'."));
        }

        return new LoadedProgramming(paths, configuration, catalog, inventory);
    }

    private async Task ValidateAndWriteAsync(
        LoadedProgramming loaded,
        ProgrammingConfiguration updated,
        CancellationToken cancellationToken)
    {
        ProgrammingConfigurationValidator.Validate(updated, loaded.Catalog, loaded.Inventory.Assets);
        await _configurationStore.WriteAsync(
            loaded.Paths.ConfigurationPath,
            updated,
            cancellationToken).ConfigureAwait(false);
    }

    private static void VerifyExpectedRevision(
        ProgrammingConfiguration configuration,
        long? expectedRevision)
    {
        if (expectedRevision is long expected && configuration.Revision != expected)
        {
            throw new ProgrammingConfigurationConflictException(expected, configuration.Revision);
        }
    }

    private static bool OverridesEqual(
        IReadOnlyDictionary<string, AssetEditorialOverride> first,
        IReadOnlyDictionary<string, AssetEditorialOverride> second) =>
        first.Count == second.Count
        && first.All(item => second.TryGetValue(item.Key, out AssetEditorialOverride? value)
            && value == item.Value);

    private sealed record LoadedProgramming(
        ProgrammingPaths Paths,
        ProgrammingConfiguration Configuration,
        SongCatalog Catalog,
        ProgrammingInventory Inventory);
}
