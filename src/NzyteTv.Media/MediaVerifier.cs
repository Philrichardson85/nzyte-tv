using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record MediaVerification(MediaDescription Media, VerificationResult Result);

public interface IMediaVerifier
{
    Task<MediaVerification> VerifyAsync(string filePath, CancellationToken cancellationToken);
}

public sealed class MediaVerifier : IMediaVerifier
{
    private readonly IMediaAnalyzer _analyzer;
    private readonly IBroadcastStandardValidator _validator;

    public MediaVerifier(IMediaAnalyzer analyzer, IBroadcastStandardValidator validator)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<MediaVerification> VerifyAsync(string filePath, CancellationToken cancellationToken)
    {
        MediaDescription media = await _analyzer.AnalyzeForVerificationAsync(filePath, cancellationToken).ConfigureAwait(false);
        return new MediaVerification(media, _validator.Validate(media));
    }
}
