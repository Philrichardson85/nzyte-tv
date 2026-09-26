using System.Globalization;
using System.Text.Json;
using NzyteTv.Core;

namespace NzyteTv.Media;

public static class FfprobeJsonParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    public static MediaDescription ParseMedia(string json, string filePath)
    {
        FfprobeDocument document = Deserialize(json);
        FfprobeStream? video = document.Streams.FirstOrDefault(stream => stream.CodecType == "video");
        FfprobeStream? audio = document.Streams.FirstOrDefault(stream => stream.CodecType == "audio");
        FfprobeFormat? format = document.Format;

        return new MediaDescription(
            Path.GetFullPath(filePath),
            ParseDuration(format?.Duration),
            format?.FormatName ?? "unknown",
            ParseLong(format?.Size) ?? (File.Exists(filePath) ? new FileInfo(filePath).Length : 0),
            video is null
                ? null
                : new VideoDescription(
                    video.CodecName ?? "unknown",
                    video.Profile,
                    video.Width,
                    video.Height,
                    video.PixelFormat ?? "unknown",
                    SelectFrameRate(video),
                    ParseLong(video.BitRate)),
            audio is null
                ? null
                : new AudioDescription(
                    audio.CodecName ?? "unknown",
                    ParseInt(audio.SampleRate),
                    audio.Channels,
                    ParseLong(audio.BitRate)),
            ParseKeyframes(document));
    }

    public static IReadOnlyList<double> ParseKeyframes(string json) => ParseKeyframes(Deserialize(json));

    private static FfprobeDocument Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<FfprobeDocument>(json, SerializerOptions)
                ?? throw new FfprobeDataException("FFprobe returned an empty JSON document.");
        }
        catch (JsonException exception)
        {
            throw new FfprobeDataException("FFprobe returned invalid JSON.", exception);
        }
    }

    private static IReadOnlyList<double> ParseKeyframes(FfprobeDocument document) => document.Frames
        .Select(frame => frame.BestEffortTimestampTime ?? frame.PresentationTimestampTime)
        .Select(ParseDouble)
        .Where(value => value.HasValue)
        .Select(value => value!.Value)
        .Order()
        .ToArray();

    private static string? SelectFrameRate(FfprobeStream stream) =>
        stream.AverageFrameRate is not null and not "0/0"
            ? stream.AverageFrameRate
            : stream.RealFrameRate;

    private static TimeSpan? ParseDuration(string? value)
    {
        double? seconds = ParseDouble(value);
        return seconds is >= 0 ? TimeSpan.FromSeconds(seconds.Value) : null;
    }

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            && double.IsFinite(result)
                ? result
                : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
            ? result
            : null;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : null;
}

public sealed class FfprobeDataException : Exception
{
    public FfprobeDataException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
