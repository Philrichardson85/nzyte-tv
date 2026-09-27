using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class MediaNormalizerTests
{
    [Fact]
    public async Task NormalizeAsync_VerifiesTemporaryFileBeforePublishingDestination()
    {
        string directory = Directory.CreateTempSubdirectory("nzytetv-normalizer-").FullName;
        try
        {
            string source = Path.Combine(directory, "source.mp4");
            string destination = Path.Combine(directory, "ready", "source.mp4");
            await File.WriteAllTextAsync(source, "source");
            var verifier = new RecordingVerifier(isReady: true);
            var normalizer = new MediaNormalizer(
                "ffmpeg",
                new EncodingProcessRunner(),
                new StubAnalyzer(source),
                verifier);

            NormalizationResult result = await normalizer.NormalizeAsync(
                source, destination, overwrite: false, progress: null, CancellationToken.None);

            Assert.Equal(destination, result.OutputPath);
            Assert.True(File.Exists(destination));
            Assert.NotNull(verifier.VerifiedPath);
            Assert.Contains(".partial.mp4", verifier.VerifiedPath, StringComparison.Ordinal);
            Assert.NotEqual(destination, verifier.VerifiedPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NormalizeAsync_FailedVerification_DoesNotPublishDestinationOrLeavePartialFile()
    {
        string directory = Directory.CreateTempSubdirectory("nzytetv-normalizer-").FullName;
        try
        {
            string source = Path.Combine(directory, "source.mp4");
            string destination = Path.Combine(directory, "ready", "source.mp4");
            await File.WriteAllTextAsync(source, "source");
            var normalizer = new MediaNormalizer(
                "ffmpeg",
                new EncodingProcessRunner(),
                new StubAnalyzer(source),
                new RecordingVerifier(isReady: false));

            await Assert.ThrowsAsync<NormalizationVerificationException>(() => normalizer.NormalizeAsync(
                source, destination, overwrite: false, progress: null, CancellationToken.None));

            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.partial.mp4"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NormalizeAsync_PortraitInputSelectsSingleEncodeBlurredBackgroundPath()
    {
        string directory = Directory.CreateTempSubdirectory("nzytetv-portrait-").FullName;
        try
        {
            string source = Path.Combine(directory, "portrait.mp4");
            string destination = Path.Combine(directory, "ready", "portrait.mp4");
            await File.WriteAllTextAsync(source, "source");
            var runner = new EncodingProcessRunner();
            var normalizer = new MediaNormalizer(
                "ffmpeg",
                runner,
                new StubAnalyzer(source, Video(width: 1080, height: 1920)),
                new RecordingVerifier(isReady: true));

            await normalizer.NormalizeAsync(
                source,
                destination,
                overwrite: false,
                progress: null,
                CancellationToken.None,
                BlurredBackgroundOptions);

            ProcessRequest request = Assert.Single(runner.Requests);
            Assert.Contains("-filter_complex", request.Arguments);
            Assert.Contains(FfmpegArgumentBuilder.BlurredBackgroundVideoFilter, request.Arguments);
            Assert.EndsWith(".partial.mp4", request.Arguments[^1], StringComparison.Ordinal);
            Assert.False(File.Exists(request.Arguments[^1]));
            Assert.True(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NormalizeAsync_LandscapeInputWithOptionUsesStandardFilterPath()
    {
        string directory = Directory.CreateTempSubdirectory("nzytetv-landscape-").FullName;
        try
        {
            string source = Path.Combine(directory, "landscape.mp4");
            string destination = Path.Combine(directory, "ready", "landscape.mp4");
            await File.WriteAllTextAsync(source, "source");
            var runner = new EncodingProcessRunner();
            var normalizer = new MediaNormalizer(
                "ffmpeg",
                runner,
                new StubAnalyzer(source, Video(width: 1920, height: 1080)),
                new RecordingVerifier(isReady: true));

            await normalizer.NormalizeAsync(
                source,
                destination,
                overwrite: false,
                progress: null,
                CancellationToken.None,
                BlurredBackgroundOptions);

            ProcessRequest request = Assert.Single(runner.Requests);
            Assert.DoesNotContain("-filter_complex", request.Arguments);
            Assert.Contains("-vf", request.Arguments);
            Assert.Contains(FfmpegArgumentBuilder.StandardVideoFilter, request.Arguments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NormalizeAsync_RotatedPortraitUsesDisplayOrientationMetadata()
    {
        string directory = Directory.CreateTempSubdirectory("nzytetv-rotated-").FullName;
        try
        {
            string source = Path.Combine(directory, "rotated.mp4");
            string destination = Path.Combine(directory, "ready", "rotated.mp4");
            await File.WriteAllTextAsync(source, "source");
            var runner = new EncodingProcessRunner();
            var normalizer = new MediaNormalizer(
                "ffmpeg",
                runner,
                new StubAnalyzer(source, Video(width: 1920, height: 1080, rotation: 90)),
                new RecordingVerifier(isReady: true));

            await normalizer.NormalizeAsync(
                source,
                destination,
                overwrite: false,
                progress: null,
                CancellationToken.None,
                BlurredBackgroundOptions);

            Assert.Contains("-filter_complex", Assert.Single(runner.Requests).Arguments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NormalizeAsync_UnknownOrientationFailsBeforeEncoding()
    {
        string directory = Directory.CreateTempSubdirectory("nzytetv-orientation-").FullName;
        try
        {
            string source = Path.Combine(directory, "unknown.mp4");
            string destination = Path.Combine(directory, "ready", "unknown.mp4");
            await File.WriteAllTextAsync(source, "source");
            var runner = new EncodingProcessRunner();
            var normalizer = new MediaNormalizer(
                "ffmpeg",
                runner,
                new StubAnalyzer(source, Video(width: 1080, height: 1920, rotation: null)),
                new RecordingVerifier(isReady: true));

            MediaNormalizationException exception = await Assert.ThrowsAsync<MediaNormalizationException>(() =>
                normalizer.NormalizeAsync(
                    source,
                    destination,
                    overwrite: false,
                    progress: null,
                    CancellationToken.None,
                    BlurredBackgroundOptions));

            Assert.Contains("orientation could not be safely determined", exception.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Requests);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static NormalizationOptions BlurredBackgroundOptions => new()
    {
        VerticalLayout = VerticalLayoutMode.BlurredBackground,
    };

    private static VideoDescription Video(int width, int height, int? rotation = 0) => new(
        "h264",
        "High",
        width,
        height,
        "yuv420p",
        "30/1",
        6_000_000,
        "1:1",
        rotation);

    private sealed class EncodingProcessRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            string outputPath = request.Arguments[^1];
            await File.WriteAllTextAsync(outputPath, "encoded", cancellationToken);
            return new ProcessResult(0, "progress=end", string.Empty);
        }
    }

    private sealed class StubAnalyzer(string sourcePath, VideoDescription? video = null) : IMediaAnalyzer
    {
        private readonly MediaDescription _media = new(
            sourcePath,
            TimeSpan.FromSeconds(4),
            "mov,mp4",
            100,
            video ?? new VideoDescription("h264", "High", 1920, 1080, "yuv420p", "30/1", 6_000_000),
            new AudioDescription("aac", 48_000, 2, 192_000),
            []);

        public Task<MediaDescription> InspectAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(_media);

        public Task<MediaDescription> AnalyzeForVerificationAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(_media);
    }

    private sealed class RecordingVerifier(bool isReady) : IMediaVerifier
    {
        public string? VerifiedPath { get; private set; }

        public Task<MediaVerification> VerifyAsync(string filePath, CancellationToken cancellationToken)
        {
            VerifiedPath = filePath;
            var media = new MediaDescription(filePath, TimeSpan.FromSeconds(1), "mov,mp4", 100, null, null, []);
            var result = new VerificationResult([new VerificationCheck("Test", "Ready", isReady)]);
            return Task.FromResult(new MediaVerification(media, result));
        }
    }
}
