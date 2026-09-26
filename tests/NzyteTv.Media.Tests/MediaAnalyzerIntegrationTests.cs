using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class MediaAnalyzerIntegrationTests
{
    [FfmpegFact]
    public async Task InspectAsync_RuntimeGeneratedClip_ReturnsVideoAndAudioMetadata()
    {
        string directory = Directory.CreateTempSubdirectory("nzytetv-integration-").FullName;
        string clip = Path.Combine(directory, "tiny clip.mp4");
        try
        {
            MediaToolPaths tools = await new MediaToolLocator().LocateAsync(CancellationToken.None);
            var runner = new ProcessRunner();
            ProcessResult generated = await runner.RunAsync(
                new ProcessRequest(tools.Ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "testsrc=size=160x90:rate=30",
                    "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
                    "-t", "0.5",
                    "-c:v", "mpeg4",
                    "-c:a", "aac",
                    clip,
                ]),
                CancellationToken.None);
            Assert.True(generated.ExitCode == 0, generated.StandardError);

            var analyzer = new MediaAnalyzer(tools.Ffprobe, runner);
            NzyteTv.Core.MediaDescription media = await analyzer.InspectAsync(clip, CancellationToken.None);

            Assert.NotNull(media.Video);
            Assert.NotNull(media.Audio);
            Assert.Equal(160, media.Video.Width);
            Assert.Equal(90, media.Video.Height);
            Assert.Equal("aac", media.Audio.Codec);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
