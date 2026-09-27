using NzyteTv.Core;

namespace NzyteTv.Media;

public static class FfmpegArgumentBuilder
{
    public const string StandardVideoFilter =
        "scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2," +
        "pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1";

    public const string BlurredBackgroundVideoFilter =
        "[0:v:0]split=2[bgsrc][fgsrc];" +
        "[bgsrc]scale=1920:1080:force_original_aspect_ratio=increase," +
        "crop=1920:1080,boxblur=30:15,eq=brightness=-0.18:saturation=0.70[bg];" +
        "[fgsrc]scale=-2:1080[fg];" +
        "[bg][fg]overlay=(W-w)/2:(H-h)/2,setsar=1[outv]";

    public static IReadOnlyList<string> BuildNormalizeArguments(
        string inputPath,
        string outputPath,
        bool sourceHasAudio,
        VerticalLayoutMode appliedVerticalLayout = VerticalLayoutMode.None)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-nostdin",
            "-y",
            "-i", Path.GetFullPath(inputPath),
        };

        if (!sourceHasAudio)
        {
            arguments.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
        }

        if (appliedVerticalLayout == VerticalLayoutMode.BlurredBackground)
        {
            arguments.AddRange([
                "-filter_complex", BlurredBackgroundVideoFilter,
                "-map", "[outv]",
                "-metadata:s:v:0", "rotate=0",
            ]);
        }
        else if (appliedVerticalLayout == VerticalLayoutMode.None)
        {
            arguments.AddRange([
                "-map", "0:v:0",
                "-vf", StandardVideoFilter,
            ]);
        }
        else
        {
            throw new ArgumentOutOfRangeException(
                nameof(appliedVerticalLayout),
                appliedVerticalLayout,
                "Unsupported vertical layout mode.");
        }

        arguments.AddRange([
            "-map", sourceHasAudio ? "0:a:0" : "1:a:0",
            "-c:v", "libx264",
            "-profile:v", "high",
            "-pix_fmt", "yuv420p",
            "-r", "30",
            "-fps_mode", "cfr",
            "-g", "60",
            "-keyint_min", "60",
            "-sc_threshold", "0",
            "-b:v", "6000k",
            "-maxrate", "6000k",
            "-bufsize", "12000k",
            "-c:a", "aac",
            "-ar", "48000",
            "-ac", "2",
            "-b:a", "192k",
            "-movflags", "+faststart",
        ]);

        if (!sourceHasAudio)
        {
            arguments.Add("-shortest");
        }

        arguments.AddRange(["-progress", "pipe:1", "-nostats", Path.GetFullPath(outputPath)]);
        return arguments;
    }
}
