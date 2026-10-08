using NzyteTv.Operations.Contracts;

namespace NzyteTv.Dashboard.Operations;

public interface IOperationsHelperClient
{
    Task<SpotlightStateResponse> GetSpotlightAsync(CancellationToken cancellationToken);
    Task<SpotlightStateResponse> SetSpotlightAsync(SetSpotlightRequest request, CancellationToken cancellationToken);
    Task<SpotlightStateResponse> DisableSpotlightAsync(DisableSpotlightRequest request, CancellationToken cancellationToken);
}

public sealed class OperationsHelperException(
    string code,
    int? statusCode = null,
    Exception? innerException = null) : InvalidOperationException("The operations helper request failed.", innerException)
{
    public string Code { get; } = code;
    public int? StatusCode { get; } = statusCode;
}
