using System.Text.Json;
using System.Text.Json.Serialization;

namespace NzyteTv.Media;

public sealed class FfprobeDocument
{
    [JsonPropertyName("streams")]
    public List<FfprobeStream> Streams { get; init; } = [];

    [JsonPropertyName("format")]
    public FfprobeFormat? Format { get; init; }

    [JsonPropertyName("frames")]
    public List<FfprobeFrame> Frames { get; init; } = [];
}

public sealed class FfprobeStream
{
    [JsonPropertyName("codec_name")]
    public string? CodecName { get; init; }

    [JsonPropertyName("codec_type")]
    public string? CodecType { get; init; }

    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("pix_fmt")]
    public string? PixelFormat { get; init; }

    [JsonPropertyName("avg_frame_rate")]
    public string? AverageFrameRate { get; init; }

    [JsonPropertyName("r_frame_rate")]
    public string? RealFrameRate { get; init; }

    [JsonPropertyName("bit_rate")]
    public string? BitRate { get; init; }

    [JsonPropertyName("sample_aspect_ratio")]
    public string? SampleAspectRatio { get; init; }

    [JsonPropertyName("sample_rate")]
    public string? SampleRate { get; init; }

    [JsonPropertyName("channels")]
    public int? Channels { get; init; }

    [JsonPropertyName("tags")]
    public Dictionary<string, JsonElement>? Tags { get; init; } = [];

    [JsonPropertyName("side_data_list")]
    public List<FfprobeSideData>? SideDataList { get; init; } = [];
}

public sealed class FfprobeSideData
{
    [JsonPropertyName("side_data_type")]
    public string? SideDataType { get; init; }

    [JsonPropertyName("rotation")]
    public JsonElement Rotation { get; init; }
}

public sealed class FfprobeFormat
{
    [JsonPropertyName("format_name")]
    public string? FormatName { get; init; }

    [JsonPropertyName("duration")]
    public string? Duration { get; init; }

    [JsonPropertyName("size")]
    public string? Size { get; init; }
}

public sealed class FfprobeFrame
{
    [JsonPropertyName("best_effort_timestamp_time")]
    public string? BestEffortTimestampTime { get; init; }

    [JsonPropertyName("pts_time")]
    public string? PresentationTimestampTime { get; init; }
}
