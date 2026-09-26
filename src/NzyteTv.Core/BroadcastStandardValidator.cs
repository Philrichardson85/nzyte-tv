namespace NzyteTv.Core;

public interface IBroadcastStandardValidator
{
    VerificationResult Validate(MediaDescription media);
}

public sealed class BroadcastStandardValidator : IBroadcastStandardValidator
{
    public VerificationResult Validate(MediaDescription media)
    {
        ArgumentNullException.ThrowIfNull(media);

        var checks = new List<VerificationCheck>();
        string[] formats = media.Container.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Add(checks, "Container", "Usable MP4", formats.Any(IsMp4Format), media.Container);

        VideoDescription? video = media.Video;
        Add(checks, "Video", "Video stream", video is not null);
        Add(checks, "Video", "H.264 High", video is not null
            && IsH264(video.Codec)
            && string.Equals(video.Profile, "High", StringComparison.OrdinalIgnoreCase),
            video is null ? "missing" : $"{video.Codec} {video.Profile}".Trim());
        Add(checks, "Video", "1920x1080", video?.Width == BroadcastStandard.Width
            && video.Height == BroadcastStandard.Height,
            video is null ? "missing" : $"{video.Width}x{video.Height}");
        Add(checks, "Video", "30 fps", video is not null
            && RationalNumber.TryParse(video.FrameRate, out double fps)
            && Math.Abs(fps - BroadcastStandard.FramesPerSecond) <= BroadcastStandard.FrameRateTolerance,
            video?.FrameRate ?? "missing");
        Add(checks, "Video", "yuv420p", string.Equals(video?.PixelFormat, "yuv420p", StringComparison.OrdinalIgnoreCase),
            video?.PixelFormat ?? "missing");

        (bool keyframesPass, string keyframeDetail) = ValidateKeyframes(
            media.KeyframeTimestamps,
            media.Duration?.TotalSeconds);
        Add(checks, "Video", "2 sec keyframes", keyframesPass, keyframeDetail);

        AudioDescription? audio = media.Audio;
        Add(checks, "Audio", "Audio stream", audio is not null);
        Add(checks, "Audio", "AAC", string.Equals(audio?.Codec, "aac", StringComparison.OrdinalIgnoreCase),
            audio?.Codec ?? "missing");
        Add(checks, "Audio", "48 kHz", audio?.SampleRate == BroadcastStandard.AudioSampleRate,
            audio?.SampleRate?.ToString() ?? "missing");
        Add(checks, "Audio", "Stereo", audio?.Channels == BroadcastStandard.AudioChannels,
            audio?.Channels?.ToString() ?? "missing");

        return new VerificationResult(checks);
    }

    public static (bool Passed, string Detail) ValidateKeyframes(
        IReadOnlyList<double> timestamps,
        double? durationSeconds)
    {
        if (timestamps.Count == 0)
        {
            return (false, "no keyframes reported");
        }

        double[] ordered = timestamps.Order().ToArray();
        if (Math.Abs(ordered[0]) > BroadcastStandard.KeyframeToleranceSeconds)
        {
            return (false, $"first keyframe at {ordered[0]:0.000}s");
        }

        if (ordered.Length == 1)
        {
            bool shortEnough = durationSeconds is > 0
                && durationSeconds <= BroadcastStandard.KeyframeIntervalSeconds + BroadcastStandard.KeyframeToleranceSeconds;
            return (shortEnough, shortEnough ? "single keyframe in short media" : "no consecutive keyframe interval available");
        }

        double[] intervals = ordered.Zip(ordered.Skip(1), (first, second) => second - first).ToArray();
        double min = intervals.Min();
        double max = intervals.Max();
        bool valid = intervals.All(interval =>
            interval > 0
            && Math.Abs(interval - BroadcastStandard.KeyframeIntervalSeconds) <= BroadcastStandard.KeyframeToleranceSeconds);

        return (valid, $"range {min:0.000}s-{max:0.000}s");
    }

    private static bool IsMp4Format(string value) =>
        value.Equals("mp4", StringComparison.OrdinalIgnoreCase)
        || value.Equals("mov", StringComparison.OrdinalIgnoreCase);

    private static bool IsH264(string value) =>
        value.Equals("h264", StringComparison.OrdinalIgnoreCase)
        || value.Equals("avc", StringComparison.OrdinalIgnoreCase)
        || value.Equals("avc1", StringComparison.OrdinalIgnoreCase);

    private static void Add(
        ICollection<VerificationCheck> checks,
        string section,
        string label,
        bool passed,
        string? detail = null) => checks.Add(new VerificationCheck(section, label, passed, detail));
}
