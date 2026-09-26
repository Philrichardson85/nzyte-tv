using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class FfprobeJsonParserTests
{
    private const string ProbeJson = """
        {
          "streams": [
            {
              "codec_name": "h264",
              "profile": "High",
              "codec_type": "video",
              "width": 1920,
              "height": 1080,
              "pix_fmt": "yuv420p",
              "r_frame_rate": "30/1",
              "avg_frame_rate": "30000/1000",
              "bit_rate": "6000000"
            },
            {
              "codec_name": "aac",
              "codec_type": "audio",
              "sample_rate": "48000",
              "channels": 2,
              "bit_rate": "192000"
            }
          ],
          "format": {
            "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
            "duration": "12.500000",
            "size": "1234567"
          }
        }
        """;

    [Fact]
    public void ParseMedia_MapsTypedProbeData()
    {
        NzyteTv.Core.MediaDescription media = FfprobeJsonParser.ParseMedia(ProbeJson, "sample.mp4");

        Assert.Equal(TimeSpan.FromSeconds(12.5), media.Duration);
        Assert.Equal(1_234_567, media.FileSize);
        Assert.Equal("h264", media.Video!.Codec);
        Assert.Equal("High", media.Video.Profile);
        Assert.Equal("30000/1000", media.Video.FrameRate);
        Assert.Equal(6_000_000, media.Video.BitRate);
        Assert.Equal("aac", media.Audio!.Codec);
        Assert.Equal(48_000, media.Audio.SampleRate);
        Assert.Equal(2, media.Audio.Channels);
    }

    [Fact]
    public void ParseKeyframes_PrefersBestEffortTimestampAndSorts()
    {
        const string json = """
            { "frames": [
              { "best_effort_timestamp_time": "4.000000" },
              { "pts_time": "0.000000" },
              { "best_effort_timestamp_time": "2.000000" }
            ] }
            """;

        IReadOnlyList<double> timestamps = FfprobeJsonParser.ParseKeyframes(json);

        Assert.Equal([0, 2, 4], timestamps);
    }

    [Fact]
    public void ParseMedia_InvalidJson_ThrowsClearException()
    {
        Assert.Throws<FfprobeDataException>(() => FfprobeJsonParser.ParseMedia("not-json", "sample.mp4"));
    }
}
