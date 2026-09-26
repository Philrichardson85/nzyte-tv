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

    private sealed class EncodingProcessRunner : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            string outputPath = request.Arguments[^1];
            await File.WriteAllTextAsync(outputPath, "encoded", cancellationToken);
            return new ProcessResult(0, "progress=end", string.Empty);
        }
    }

    private sealed class StubAnalyzer(string sourcePath) : IMediaAnalyzer
    {
        private readonly MediaDescription _media = new(
            sourcePath,
            TimeSpan.FromSeconds(4),
            "mov,mp4",
            100,
            new VideoDescription("h264", "High", 1920, 1080, "yuv420p", "30/1", 6_000_000),
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
