using NzyteTv.Core;
using NzyteTv.Media;
using NzyteTv.Operations.Configuration;
using NzyteTv.Operations.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace NzyteTv.Operations.Spotlight;

public sealed class SpotlightOperationsService(
    OperationsOptions options,
    IProgrammingConfigurationStore configurationStore,
    ISongCatalogStore catalogStore,
    ProgrammingService programmingService,
    ILogger<SpotlightOperationsService>? logger = null) : ISpotlightOperationsService
{
    private readonly ILogger<SpotlightOperationsService> _logger =
        logger ?? NullLogger<SpotlightOperationsService>.Instance;

    public SpotlightStateResponse Get()
    {
        ProgrammingPaths paths = ProgrammingPaths.FromMediaRoot(options.MediaRoot);
        ProgrammingConfiguration configuration = configurationStore.Load(paths.ConfigurationPath);
        SongCatalog catalog = catalogStore.Load(paths.CatalogPath);
        ProgrammingConfigurationValidator.ValidateStructure(configuration);
        return Map(configuration, catalog);
    }

    public async Task<SpotlightStateResponse> SetAsync(SetSpotlightRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedRevision < 1 || string.IsNullOrWhiteSpace(request.ContentGroupId)
            || request.ContentGroupId.Length > 160)
        {
            throw new OperationsValidationException();
        }
        try
        {
            ProgrammingMutationResult result = await programmingService.SetCampaignAsync(
                options.MediaRoot, request.ContentGroupId, request.WeightMultiplier,
                request.ExpectedRevision, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Spotlight updated from revision {ExpectedRevision} to {Revision}.",
                request.ExpectedRevision,
                result.Configuration.Revision);
            return Map(result.Configuration, catalogStore.Load(result.Paths.CatalogPath));
        }
        catch (Exception exception) when (exception is ArgumentException or ProgrammingConfigurationValidationException)
        {
            throw new OperationsValidationException(exception);
        }
    }

    public async Task<SpotlightStateResponse> DisableAsync(DisableSpotlightRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedRevision < 1)
        {
            throw new OperationsValidationException();
        }
        try
        {
            ProgrammingMutationResult result = await programmingService.ClearCampaignAsync(
                options.MediaRoot, request.ExpectedRevision, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Spotlight disabled from revision {ExpectedRevision} to {Revision}.",
                request.ExpectedRevision,
                result.Configuration.Revision);
            return Map(result.Configuration, catalogStore.Load(result.Paths.CatalogPath));
        }
        catch (ProgrammingConfigurationValidationException exception)
        {
            throw new OperationsValidationException(exception);
        }
    }

    private static SpotlightStateResponse Map(ProgrammingConfiguration configuration, SongCatalog catalog)
    {
        SongCatalogValidator.Validate(catalog);
        ActiveCampaign campaign = configuration.ActiveCampaign!;
        SongCatalogEntry? current = campaign.Enabled ? catalog.FindByContentGroupId(campaign.ContentGroupId) : null;
        if (campaign.Enabled && current is null)
        {
            throw new OperationsValidationException();
        }
        SpotlightCatalogOption[] catalogOptions = catalog.Songs!
            .Select(song => new SpotlightCatalogOption(
                song.ContentGroupId!, OperationsTextSanitizer.Required(song.Title),
                OperationsTextSanitizer.Required(song.Artist)))
            .OrderBy(song => song.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(song => song.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new SpotlightStateResponse(
            OperationsProtocol.SchemaVersion, configuration.Revision, campaign.Enabled,
            campaign.Enabled ? campaign.ContentGroupId : null,
            current is null ? null : OperationsTextSanitizer.Required(current.Title),
            current is null ? null : OperationsTextSanitizer.Required(current.Artist),
            campaign.WeightMultiplier, catalogOptions);
    }
}

public sealed class OperationsValidationException : InvalidOperationException
{
    public OperationsValidationException() : base("The operation request is invalid.") { }
    public OperationsValidationException(Exception innerException)
        : base("The operation request is invalid.", innerException) { }
}
