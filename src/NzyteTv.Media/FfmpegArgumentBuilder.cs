namespace NzyteTv.Media;

public static class FfmpegArgumentBuilder
{
    public static IReadOnlyList<string> BuildNormalizeArguments(
        string inputPath,
        string outputPath,
        bool sourceHasAudio)
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

        arguments.AddRange([
            "-map", "0:v:0",
            "-map", sourceHasAudio ? "0:a:0" : "1:a:0",
            "-vf", "scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1",
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
