using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class FfmpegArgumentBuilderTests
{
    [Fact]
    public void BuildNormalizeArguments_ContainsRequiredBroadcastSettings()
    {
        IReadOnlyList<string> arguments = FfmpegArgumentBuilder.BuildNormalizeArguments(
            "input clip.mov",
            "output clip.mp4",
            sourceHasAudio: true);

        AssertOption(arguments, "-c:v", "libx264");
        AssertOption(arguments, "-profile:v", "high");
        AssertOption(arguments, "-pix_fmt", "yuv420p");
        AssertOption(arguments, "-r", "30");
        AssertOption(arguments, "-fps_mode", "cfr");
        AssertOption(arguments, "-g", "60");
        AssertOption(arguments, "-keyint_min", "60");
        AssertOption(arguments, "-sc_threshold", "0");
        AssertOption(arguments, "-b:v", "6000k");
        AssertOption(arguments, "-maxrate", "6000k");
        AssertOption(arguments, "-bufsize", "12000k");
        AssertOption(arguments, "-c:a", "aac");
        AssertOption(arguments, "-ar", "48000");
        AssertOption(arguments, "-ac", "2");
        AssertOption(arguments, "-b:a", "192k");
        AssertOption(arguments, "-movflags", "+faststart");
        Assert.Contains("force_original_aspect_ratio=decrease", arguments.Single(value => value.StartsWith("scale=", StringComparison.Ordinal)));
    }

    [Fact]
    public void BuildNormalizeArguments_NoSourceAudio_AddsSilentStereoAndShortest()
    {
        IReadOnlyList<string> arguments = FfmpegArgumentBuilder.BuildNormalizeArguments(
            "video-only.mp4",
            "normalized.mp4",
            sourceHasAudio: false);

        Assert.Contains("anullsrc=channel_layout=stereo:sample_rate=48000", arguments);
        Assert.Contains("-shortest", arguments);
        AssertOption(arguments, "-map", "1:a:0", occurrence: 2);
    }

    [Fact]
    public void BuildNormalizeArguments_BlurredBackgroundUsesReferenceVisualRecipeAndBroadcastSettings()
    {
        IReadOnlyList<string> arguments = FfmpegArgumentBuilder.BuildNormalizeArguments(
            "portrait source.mp4",
            "normalized.mp4",
            sourceHasAudio: true,
            VerticalLayoutMode.BlurredBackground);

        string filter = GetOption(arguments, "-filter_complex");
        Assert.Contains("split=2[bgsrc][fgsrc]", filter, StringComparison.Ordinal);
        Assert.Contains(
            "scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080",
            filter,
            StringComparison.Ordinal);
        Assert.Contains("boxblur=30:15", filter, StringComparison.Ordinal);
        Assert.Contains("eq=brightness=-0.18:saturation=0.70", filter, StringComparison.Ordinal);
        Assert.Contains("[fgsrc]scale=-2:1080[fg]", filter, StringComparison.Ordinal);
        Assert.Contains("overlay=(W-w)/2:(H-h)/2", filter, StringComparison.Ordinal);
        AssertOption(arguments, "-map", "[outv]");
        AssertOption(arguments, "-metadata:s:v:0", "rotate=0");
        Assert.DoesNotContain("-vf", arguments);
        AssertOption(arguments, "-c:v", "libx264");
        AssertOption(arguments, "-profile:v", "high");
        AssertOption(arguments, "-pix_fmt", "yuv420p");
        AssertOption(arguments, "-r", "30");
        AssertOption(arguments, "-fps_mode", "cfr");
        AssertOption(arguments, "-g", "60");
        AssertOption(arguments, "-keyint_min", "60");
        AssertOption(arguments, "-sc_threshold", "0");
        AssertOption(arguments, "-b:v", "6000k");
        AssertOption(arguments, "-maxrate", "6000k");
        AssertOption(arguments, "-bufsize", "12000k");
        AssertOption(arguments, "-c:a", "aac");
        AssertOption(arguments, "-ar", "48000");
        AssertOption(arguments, "-ac", "2");
        AssertOption(arguments, "-b:a", "192k");
        AssertOption(arguments, "-movflags", "+faststart");
    }

    private static void AssertOption(
        IReadOnlyList<string> arguments,
        string option,
        string expectedValue,
        int occurrence = 1)
    {
        int index = -1;
        for (int count = 0; count < occurrence; count++)
        {
            index = arguments.ToList().FindIndex(index + 1, value => value == option);
        }

        Assert.True(index >= 0 && index + 1 < arguments.Count, $"Missing option {option} occurrence {occurrence}.");
        Assert.Equal(expectedValue, arguments[index + 1]);
    }

    private static string GetOption(IReadOnlyList<string> arguments, string option)
    {
        int index = arguments.ToList().FindIndex(value => value == option);
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"Missing option {option}.");
        return arguments[index + 1];
    }
}
