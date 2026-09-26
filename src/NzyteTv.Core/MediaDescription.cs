namespace NzyteTv.Core;

public sealed record VideoDescription(
    string Codec,
    string? Profile,
    int Width,
    int Height,
    string PixelFormat,
    string? FrameRate,
    long? BitRate);

public sealed record AudioDescription(
    string Codec,
    int? SampleRate,
    int? Channels,
    long? BitRate);

public sealed record MediaDescription(
    string FilePath,
    TimeSpan? Duration,
    string Container,
    long FileSize,
    VideoDescription? Video,
    AudioDescription? Audio,
    IReadOnlyList<double> KeyframeTimestamps);
