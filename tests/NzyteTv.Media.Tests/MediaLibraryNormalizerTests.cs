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
        var manifestStore = new SourceManifestStore();
        await WriteManifestAsync(fixture, file, manifestStore);
        var service = CreateService([file], normalizer, verifier, manifestStore);

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
    public async Task NormalizeAsync_UnresolvedProgrammingMetadata_DoesNotBlockEncodingOrVerification()
    {
        using var fixture = new LibraryFixture();
        LibraryMediaFile file = fixture.AddFile("Performance Videos", "Unknown Performance.mp4");
        var metadata = new AssetMetadata
        {
            AssetId = "unknown-performance",
            ContentGroupId = null,
            Title = "Unknown Performance",
            Artist = null,
            Type = AssetTypes.Performance,
            Enabled = true,
            Tags = [],
        };
        await new AssetMetadataStore().WriteAsync(file.SourcePath, metadata, CancellationToken.None);
        var normalizer = new RecordingNormalizer();
        var service = CreateService([file], normalizer, new ConfigurableVerifier());

        LibraryNormalizationResult result = await service.NormalizeAsync(
            fixture.SourceRoot,
            fixture.DestinationRoot,
            overwrite: false,
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.Normalized);
        Assert.Equal(1, result.VerifiedReady);
        Assert.Equal(0, result.Failed);
        Assert.Single(normalizer.Calls);
        Assert.True(File.Exists(file.DestinationPath));
        Assert.Null(new AssetMetadataStore().Read(file.SourcePath).ContentGroupId);
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
        var manifestStore = new SourceManifestStore();
        await WriteManifestAsync(fixture, skipped, manifestStore);
        var service = CreateService([failed, normalized, skipped], normalizer, verifier, manifestStore);

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
    public async Task NormalizeAsync_InvalidExistingDestination_IsRenormalizedAndLaterFileContinues()
    {
        using var fixture = new LibraryFixture();
        LibraryMediaFile invalid = fixture.AddFile("Music Videos", "Invalid.mp4", destinationExists: true);
        LibraryMediaFile later = fixture.AddFile("Vlog Episodes", "Later.mp4");
        var normalizer = new RecordingNormalizer();
        var verifier = new ConfigurableVerifier();
        verifier.SetError(invalid.DestinationPath, new MediaProbeException("simulated invalid media"));
        var manifestStore = new SourceManifestStore();
        await WriteManifestAsync(fixture, invalid, manifestStore);
        var service = CreateService([invalid, later], normalizer, verifier, manifestStore);

        LibraryNormalizationResult result = await service.NormalizeAsync(
            fixture.SourceRoot,
            fixture.DestinationRoot,
            overwrite: false,
            progress: null,
            CancellationToken.None);

        Assert.Equal(0, result.Failed);
        Assert.Equal(2, result.Normalized);
        Assert.Equal(0, result.SkippedExisting);
        Assert.Contains(normalizer.Calls, call => call.Source == invalid.SourcePath && call.Overwrite);
        Assert.Equal("normalized", File.ReadAllText(invalid.DestinationPath));
        Assert.True(File.Exists(later.DestinationPath));
    }

    [Fact]
    public async Task NormalizeAsync_ChangedSourceWithSameName_IsRenormalized()
    {
        using var fixture = new LibraryFixture();
        LibraryMediaFile file = fixture.AddFile("Music Videos", "Revised.mp4", destinationExists: true);
        var manifestStore = new SourceManifestStore();
        await WriteManifestAsync(fixture, file, manifestStore);
        File.AppendAllText(file.SourcePath, " revised content");
        var normalizer = new RecordingNormalizer();
        var verifier = new ConfigurableVerifier();
        var service = CreateService([file], normalizer, verifier, manifestStore);

        LibraryNormalizationResult result = await service.NormalizeAsync(
            fixture.SourceRoot,
            fixture.DestinationRoot,
            overwrite: false,
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.Normalized);
        Assert.Equal(0, result.SkippedExisting);
        Assert.Single(normalizer.Calls);
        Assert.True(normalizer.Calls[0].Overwrite);
        Assert.Empty(verifier.Calls);
        SourceFingerprint current = manifestStore.CreateFingerprint(fixture.SourceRoot, file.SourcePath);
        Assert.True(manifestStore.Evaluate(file.DestinationPath, current).IsMatch);
    }

    [Fact]
    public async Task NormalizeAsync_BroadcastProfileChange_IsRenormalized()
    {
        using var fixture = new LibraryFixture();
        LibraryMediaFile file = fixture.AddFile("Music Videos", "Profile.mp4", destinationExists: true);
        var oldManifestStore = new SourceManifestStore("old-profile");
        await WriteManifestAsync(fixture, file, oldManifestStore);
        var newManifestStore = new SourceManifestStore("new-profile");
        var normalizer = new RecordingNormalizer();
        var verifier = new ConfigurableVerifier();
        var service = CreateService([file], normalizer, verifier, newManifestStore);

        LibraryNormalizationResult result = await service.NormalizeAsync(
            fixture.SourceRoot,
            fixture.DestinationRoot,
            overwrite: false,
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.Normalized);
        Assert.Empty(verifier.Calls);
        SourceFingerprint current = newManifestStore.CreateFingerprint(fixture.SourceRoot, file.SourcePath);
        Assert.True(newManifestStore.Evaluate(file.DestinationPath, current).IsMatch);
    }

    [Fact]
    public async Task NormalizeAsync_VerticalLayoutChangeInvalidatesManifestAndFlowsToNormalizer()
    {
        using var fixture = new LibraryFixture();
        LibraryMediaFile file = fixture.AddFile("Short Form/Nested", "Portrait.mp4", destinationExists: true);
        var manifestStore = new SourceManifestStore();
        await WriteManifestAsync(fixture, file, manifestStore);
        var normalizer = new RecordingNormalizer();
        var service = CreateService([file], normalizer, new ConfigurableVerifier(), manifestStore);
        var options = new NormalizationOptions
        {
            VerticalLayout = VerticalLayoutMode.BlurredBackground,
        };

        LibraryNormalizationResult result = await service.NormalizeAsync(
            fixture.SourceRoot,
            fixture.DestinationRoot,
            overwrite: false,
            progress: null,
            CancellationToken.None,
            options);

        Assert.Equal(1, result.Normalized);
        var call = Assert.Single(normalizer.Calls);
        Assert.Equal(VerticalLayoutMode.BlurredBackground, call.Options.VerticalLayout);
        string normalizedDestination = call.Destination.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);
        Assert.EndsWith(
            Path.Combine("Short Form", "Nested", "Portrait.mp4"),
            normalizedDestination,
            StringComparison.Ordinal);
        SourceFingerprint current = manifestStore.CreateFingerprint(
            fixture.SourceRoot,
            file.SourcePath,
            options);
        Assert.True(manifestStore.Evaluate(file.DestinationPath, current).IsMatch);
    }

    [Fact]
    public async Task NormalizeAsync_CorruptManifest_IsRenormalizedAndRepaired()
    {
        using var fixture = new LibraryFixture();
        LibraryMediaFile file = fixture.AddFile("Vlog Episodes", "Corrupt.mp4", destinationExists: true);
        File.WriteAllText(SourceManifestStore.GetManifestPath(file.DestinationPath), "not-json");
        var manifestStore = new SourceManifestStore();
        var normalizer = new RecordingNormalizer();
        var service = CreateService([file], normalizer, new ConfigurableVerifier(), manifestStore);

        LibraryNormalizationResult result = await service.NormalizeAsync(
            fixture.SourceRoot,
            fixture.DestinationRoot,
            overwrite: false,
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.Normalized);
        Assert.Single(normalizer.Calls);
        SourceFingerprint current = manifestStore.CreateFingerprint(fixture.SourceRoot, file.SourcePath);
        Assert.True(manifestStore.Evaluate(file.DestinationPath, current).IsMatch);
    }

    [Fact]
    public async Task NormalizeAsync_PreManifestDestination_IsRenormalizedForSafeMigration()
    {
        using var fixture = new LibraryFixture();
        LibraryMediaFile file = fixture.AddFile("Specials", "Legacy.mp4", destinationExists: true);
        var normalizer = new RecordingNormalizer();
        var verifier = new ConfigurableVerifier();
        var service = CreateService([file], normalizer, verifier);

        LibraryNormalizationResult result = await service.NormalizeAsync(
            fixture.SourceRoot,
            fixture.DestinationRoot,
            overwrite: false,
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.Normalized);
        Assert.Empty(verifier.Calls);
        Assert.True(File.Exists(SourceManifestStore.GetManifestPath(file.DestinationPath)));
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
        IMediaVerifier verifier,
        ISourceManifestStore? manifestStore = null) => new(
            new StaticDiscovery(files),
            normalizer,
            verifier,
            manifestStore ?? new SourceManifestStore());

    private static Task WriteManifestAsync(
        LibraryFixture fixture,
        LibraryMediaFile file,
        SourceManifestStore manifestStore) => manifestStore.WriteAsync(
            file.DestinationPath,
            manifestStore.CreateFingerprint(fixture.SourceRoot, file.SourcePath),
            CancellationToken.None);

    private sealed class StaticDiscovery(IReadOnlyList<LibraryMediaFile> files) : IMediaLibraryDiscovery
    {
        public IReadOnlyList<LibraryMediaFile> Discover(string sourceRoot, string destinationRoot) => files;
    }

    private sealed class RecordingNormalizer : IMediaNormalizer
    {
        public List<(
            string Source,
            string Destination,
            bool Overwrite,
            NormalizationOptions Options)> Calls
        { get; } = [];

        public HashSet<string> FailSources { get; } = new(StringComparer.Ordinal);

        public Task<NormalizationResult> NormalizeAsync(
            string inputPath,
            string outputPath,
            bool overwrite,
            IProgress<NormalizationProgress>? progress,
            CancellationToken cancellationToken,
            NormalizationOptions? options = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((inputPath, outputPath, overwrite, options ?? NormalizationOptions.Default));
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
        private readonly Dictionary<string, Exception> _errors = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        public void SetReady(string path, bool isReady) => _results[path] = isReady;

        public void SetError(string path, Exception exception) => _errors[path] = exception;

        public Task<MediaVerification> VerifyAsync(string filePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(filePath);
            if (_errors.TryGetValue(filePath, out Exception? error))
            {
                throw error;
            }

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
