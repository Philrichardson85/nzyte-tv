using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (MediaToolLocator.FindOnPath("ffmpeg") is null || MediaToolLocator.FindOnPath("ffprobe") is null)
        {
            Skip = "FFmpeg and FFprobe are not installed on PATH.";
        }
    }
}
