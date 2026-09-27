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
                    ParseLong(video.BitRate),
                    video.SampleAspectRatio,
                    ParseRotation(video)),
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

    private static int? ParseRotation(FfprobeStream stream)
    {
        var rotations = new List<double>();
        foreach (FfprobeSideData sideData in (stream.SideDataList ?? []).Where(item =>
            string.Equals(item.SideDataType, "Display Matrix", StringComparison.OrdinalIgnoreCase)
            || item.Rotation.ValueKind is not JsonValueKind.Undefined))
        {
            if (!TryParseRotationValue(sideData.Rotation, out double rotation))
            {
                return null;
            }

            rotations.Add(rotation);
        }

        KeyValuePair<string, JsonElement> rotationTag = (stream.Tags ?? []).FirstOrDefault(item =>
            string.Equals(item.Key, "rotate", StringComparison.OrdinalIgnoreCase));
        if (rotationTag.Key is not null)
        {
            if (!TryParseRotationValue(rotationTag.Value, out double taggedRotation))
            {
                return null;
            }

            rotations.Add(taggedRotation);
        }

        if (rotations.Count == 0)
        {
            return 0;
        }

        int? normalized = NormalizeRotation(rotations[0]);
        if (normalized is null
            || rotations.Skip(1).Any(rotation => NormalizeRotation(rotation) != normalized))
        {
            return null;
        }

        return normalized;
    }

    private static bool TryParseRotationValue(JsonElement value, out double rotation)
    {
        rotation = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out rotation) && double.IsFinite(rotation),
            JsonValueKind.String => ParseDouble(value.GetString()) is double parsed && Assign(parsed, out rotation),
            _ => false,
        };
    }

    private static bool Assign(double value, out double result)
    {
        result = value;
        return true;
    }

    private static int? NormalizeRotation(double value)
    {
        double nearestRightAngle = Math.Round(value / 90d) * 90d;
        if (Math.Abs(value - nearestRightAngle) > 0.01)
        {
            return null;
        }

        int normalized = (int)nearestRightAngle % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

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
