using NzyteTv.Operations.Contracts;

namespace NzyteTv.Operations.Spotlight;

public interface ISpotlightOperationsService
{
    SpotlightStateResponse Get();
    Task<SpotlightStateResponse> SetAsync(SetSpotlightRequest request, CancellationToken cancellationToken);
    Task<SpotlightStateResponse> DisableAsync(DisableSpotlightRequest request, CancellationToken cancellationToken);
}
