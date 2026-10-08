using System.Text.Json.Serialization;

namespace NzyteTv.Operations.Contracts;

public static class OperationsProtocol
{
    public const int SchemaVersion = 1;
    public const double DefaultSpotlightMultiplier = 2.0;
    public const int MaximumRequestBytes = 4096;
}

public static class OperationsErrorCodes
{
    public const string StaleRevision = "staleRevision";
    public const string ValidationFailed = "validationFailed";
    public const string MalformedRequest = "malformedRequest";
    public const string RequestTooLarge = "requestTooLarge";
    public const string UnsupportedContentType = "unsupportedContentType";
    public const string HelperUnavailable = "helperUnavailable";
    public const string InternalError = "internalError";
}

public sealed record SpotlightCatalogOption(
    string ContentGroupId,
    string Title,
    string Artist);

public sealed record SpotlightStateResponse(
    int SchemaVersion,
    long Revision,
    bool Enabled,
    string? ContentGroupId,
    string? Title,
    string? Artist,
    double WeightMultiplier,
    IReadOnlyList<SpotlightCatalogOption> CatalogOptions);

public sealed record SetSpotlightRequest
{
    [JsonRequired]
    public long ExpectedRevision { get; init; }

    [JsonRequired]
    public string? ContentGroupId { get; init; }

    [JsonRequired]
    public double WeightMultiplier { get; init; }
}

public sealed record DisableSpotlightRequest
{
    [JsonRequired]
    public long ExpectedRevision { get; init; }
}

public sealed record OperationsErrorResponse(int SchemaVersion, string Code);
