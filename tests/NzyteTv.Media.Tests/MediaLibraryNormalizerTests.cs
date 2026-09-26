using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class MediaLibraryNormalizerTests
{
    [Fact]
    public async Task NormalizeAsync_EmptyLibrary_CreatesDestinationRoot()
    {
        using var fixture = new LibraryFixture();
        Directory.CreateDirectory(fixture.SourceRoot);
        var service = CreateService([], new RecordingNormalizer(), new ConfigurableVerifier());

        LibraryNormalizationResult result = await service.NormalizeAsync(
            fixture.SourceRoot,
            fixture.DestinationRoot,
            overwrite: false,
            progress: null,
            CancellationToken.None);

        Assert.True(Directory.Exists(fixture.DestinationRoot));
        Assert.Equal(0, result.DiscoveredVideoFiles);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task NormalizeAsync_ExistingValidDestination_IsVerifiedAndSkipped()
    {
        using var fixture = new LibraryFixture();
        LibraryMediaFile file = fixture.AddFile("Music Videos", "Existing.mp4", destinationExists: true);
        var normalizer = new RecordingNormalizer();
        var verifier = new ConfigurableVerifier();
        verifier.SetReady(file.DestinationPath, isReady: true);
        var service = CreateService([file], normalizer, verifier);

        LibraryNormalizationResult result = await service.NormalizeAsync(
            fixture.SourceRoot,
            fixture.DestinationRoot,
            overwrite: false,
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.SkippedExisting);
        Assert.Equal(1, result.VerifiedReady);
        Assert.Equal(0, result.Failed);
        Assert.Empty(normalizer.Calls);
        Assert.Contains(file.DestinationPath, verifier.Calls);
    }

    [Fact]
    public async Task NormalizeAsync_Overwrite_ReplacesDestinationThroughNormalizer()
    {
        using var fixture = new LibraryFixture();
        LibraryMediaFile file = fixture.AddFile("Promos", "Existing.mp4", destinationExists: true);
        var normalizer = new RecordingNormalizer();
        var verifier = new ConfigurableVerifier();
        var service = CreateService([file], normalizer, verifier);

        LibraryNormalizationResult result = await service.NormalizeAsync(
            fixture.SourceRoot,
            fixture.DestinationRoot,
            overwrite: true,
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.Normalized);
        Assert.Single(normalizer.Calls);
        Assert.True(normalizer.Calls[0].Overwrite);
        Assert.Empty(verifier.Calls);
        Assert.Equal("normalized", File.ReadAllText(file.DestinationPath));
    }

    [Fact]
    public async Task NormalizeAsync_FailedFile_DoesNotAbortLaterFilesAndSummaryIsAccurate()
    {
        using var fixture = new LibraryFixture();
        LibraryMediaFile failed = fixture.AddFile("Advertisements", "Bad.mp4");
        LibraryMediaFile normalized = fixture.AddFile("Bumpers", "Good.mp4");
        LibraryMediaFile skipped = fixture.AddFile("Specials", "Ready.mp4", destinationExists: true);
        var normalizer = new RecordingNormalizer();
        normalizer.FailSources.Add(failed.SourcePath);
        var verifier = new ConfigurableVerifier();
        verifier.SetReady(skipped.DestinationPath, isReady: true);
        var service = CreateService([failed, normalized, skipped], normalizer, verifier);

        LibraryNormalizationResult result = await service.NormalizeAsync(
            fixture.SourceRoot,
            fixture.DestinationRoot,
            overwrite: false,
            progress: null,
            CancellationToken.None);

        Assert.Equal(3, result.DiscoveredVideoFiles);
        Assert.Equal(1, result.Normalized);
        Assert.Equal(1, result.SkippedExisting);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, result.VerifiedReady);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(2, normalizer.Calls.Count);
        Assert.True(File.Exists(normalized.DestinationPath));
        Assert.Contains(result.Files, item => item.SourcePath == failed.SourcePath
            && item.FailureReason!.Contains("simulated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NormalizeAsync_InvalidExistingDestination_IsFailureAndLaterFileContinues()
    {
        using var fixture = new LibraryFixture();
        LibraryMediaFile invalid = fixture.AddFile("Music Videos", "Invalid.mp4", destinationExists: true);
        LibraryMediaFile later = fixture.AddFile("Vlog Episodes", "Later.mp4");
        var normalizer = new RecordingNormalizer();
        var verifier = new ConfigurableVerifier();
        verifier.SetReady(invalid.DestinationPath, isReady: false);
        var service = CreateService([invalid, later], normalizer, verifier);

        LibraryNormalizationResult result = await service.NormalizeAsync(
            fixture.SourceRoot,
            fixture.DestinationRoot,
            overwrite: false,
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Normalized);
        Assert.Equal(0, result.SkippedExisting);
        Assert.Contains(result.Files, item => item.SourcePath == invalid.SourcePath
            && item.FailureReason!.Contains("failed verification", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(later.DestinationPath));
    }

    [Fact]
    public async Task NormalizeAsync_DestinationCollision_FailsCollidingFilesWithoutEncoding()
    {
        using var fixture = new LibraryFixture();
        LibraryMediaFile first = fixture.AddFile("Specials", "Same.mov");
        LibraryMediaFile second = fixture.AddFile("Specials", "Same.mkv");
        Assert.Equal(first.DestinationPath, second.DestinationPath);
        var normalizer = new RecordingNormalizer();
        var service = CreateService([first, second], normalizer, new ConfigurableVerifier());

        LibraryNormalizationResult result = await service.NormalizeAsync(
            fixture.SourceRoot,
            fixture.DestinationRoot,
            overwrite: false,
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, result.Failed);
        Assert.Empty(normalizer.Calls);
        Assert.All(result.Files, item => Assert.Contains("same MP4 destination", item.FailureReason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NormalizeAsync_CancelledBeforeFirstFile_StopsCleanly()
    {
        using var fixture = new LibraryFixture();
        LibraryMediaFile file = fixture.AddFile("Music Videos", "Cancelled.mp4");
        var normalizer = new RecordingNormalizer();
        var service = CreateService([file], normalizer, new ConfigurableVerifier());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.NormalizeAsync(
            fixture.SourceRoot,
            fixture.DestinationRoot,
            overwrite: false,
            progress: null,
            cancellation.Token));

        Assert.Empty(normalizer.Calls);
        Assert.False(File.Exists(file.DestinationPath));
    }

    private static MediaLibraryNormalizer CreateService(
        IReadOnlyList<LibraryMediaFile> files,
        IMediaNormalizer normalizer,
        IMediaVerifier verifier) => new(new StaticDiscovery(files), normalizer, verifier);

    private sealed class StaticDiscovery(IReadOnlyList<LibraryMediaFile> files) : IMediaLibraryDiscovery
    {
        public IReadOnlyList<LibraryMediaFile> Discover(string sourceRoot, string destinationRoot) => files;
    }

    private sealed class RecordingNormalizer : IMediaNormalizer
    {
        public List<(string Source, string Destination, bool Overwrite)> Calls { get; } = [];

        public HashSet<string> FailSources { get; } = new(StringComparer.Ordinal);

        public Task<NormalizationResult> NormalizeAsync(
            string inputPath,
            string outputPath,
            bool overwrite,
            IProgress<NormalizationProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((inputPath, outputPath, overwrite));
            if (FailSources.Contains(inputPath))
            {
                throw new MediaNormalizationException("simulated normalization failure");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, "normalized");
            MediaVerification verification = CreateVerification(outputPath, isReady: true);
            return Task.FromResult(new NormalizationResult(outputPath, TimeSpan.FromSeconds(1), verification));
        }
    }

    private sealed class ConfigurableVerifier : IMediaVerifier
    {
        private readonly Dictionary<string, bool> _results = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        public void SetReady(string path, bool isReady) => _results[path] = isReady;

        public Task<MediaVerification> VerifyAsync(string filePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(filePath);
            bool isReady = _results.GetValueOrDefault(filePath, true);
            return Task.FromResult(CreateVerification(filePath, isReady));
        }
    }

    private sealed class LibraryFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("nzytetv-library-").FullName;

        public string SourceRoot => Path.Combine(_root, "Source Media");

        public string DestinationRoot => Path.Combine(_root, "Broadcast Ready");

        public LibraryMediaFile AddFile(string category, string name, bool destinationExists = false)
        {
            string source = Path.Combine(SourceRoot, category, name);
            string destination = Path.Combine(DestinationRoot, category, Path.ChangeExtension(name, ".mp4"));
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "source");
            if (destinationExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(destination, "existing");
            }

            return new LibraryMediaFile(source, destination);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private static MediaVerification CreateVerification(string path, bool isReady)
    {
        var media = new MediaDescription(path, TimeSpan.FromSeconds(1), "mov,mp4", 100, null, null, []);
        var result = new VerificationResult([new VerificationCheck("Test", "Broadcast ready", isReady, "simulated")]);
        return new MediaVerification(media, result);
    }
}
