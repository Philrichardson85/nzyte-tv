namespace NzyteTv.Core;

public sealed record VideoDescription(
    string Codec,
    string? Profile,
    int Width,
    int Height,
    string PixelFormat,
    string? FrameRate,
    long? BitRate,
    string? SampleAspectRatio = null,
    int? RotationDegrees = 0);

public sealed record VideoDisplayDimensions(double Width, double Height)
{
    public bool IsPortrait => Height > Width;
}

public static class VideoDisplayGeometry
{
    public static bool TryGetDimensions(
        VideoDescription video,
        out VideoDisplayDimensions? dimensions,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(video);
        dimensions = null;
        error = null;
        if (video.Width <= 0 || video.Height <= 0)
        {
            error = "encoded video dimensions are missing or invalid";
            return false;
        }

        if (video.RotationDegrees is not int rotation
            || rotation is not (0 or 90 or 180 or 270))
        {
            error = "display rotation metadata is missing, conflicting, or unsupported";
            return false;
        }

        if (!TryGetSampleAspectRatio(video.SampleAspectRatio, out double sampleAspectRatio))
        {
            error = "sample aspect ratio metadata is invalid";
            return false;
        }

        double width = video.Width * sampleAspectRatio;
        double height = video.Height;
        dimensions = rotation is 90 or 270
            ? new VideoDisplayDimensions(height, width)
            : new VideoDisplayDimensions(width, height);
        return true;
    }

    private static bool TryGetSampleAspectRatio(string? value, out double ratio)
    {
        ratio = 1;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string normalized = value.Replace(':', '/');
        return RationalNumber.TryParse(normalized, out ratio) && ratio > 0;
    }
}

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
