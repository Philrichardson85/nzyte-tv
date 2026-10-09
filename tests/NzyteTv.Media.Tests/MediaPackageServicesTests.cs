using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class MediaPackageServicesTests
{
    [Fact]
    public async Task Prepare_DerivesLibraryPathHashesBoundedMembersAndPublishesReadyLast()
    {
        using var fixture = new MediaPackageTestFixture();
        (string source, string library) = await fixture.AddMembersAsync();
        string sourceBefore = await File.ReadAllTextAsync(source);
        string libraryBefore = await File.ReadAllTextAsync(library);

        ReadyPackagePreparationResult result = await fixture.PrepareAsync();

        Assert.Equal("Music Videos/Test Song.mp4", result.Manifest.LibraryRelativePath);
        Assert.True(File.Exists(Path.Combine(fixture.InboxRoot, result.ReadyFileName)));
        Assert.Empty(Directory.EnumerateFiles(fixture.InboxRoot, "*.partial"));
        Assert.Equal(sourceBefore, await File.ReadAllTextAsync(source));
        Assert.Equal(libraryBefore, await File.ReadAllTextAsync(library));
        Assert.True(ReadyMediaPackageValidator.IsSha256(result.Manifest.Source!.Sha256));
        Assert.True(ReadyMediaPackageValidator.IsSha256(result.Manifest.Library!.Sha256));
        Assert.True(ReadyMediaPackageValidator.IsSha256(result.Manifest.TechnicalManifest!.Sha256));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("library")]
    [InlineData("technical")]
    public async Task Prepare_RequiresAllFixedPackageMembers(string missing)
    {
        using var fixture = new MediaPackageTestFixture();
        (string source, string library) = await fixture.AddMembersAsync();
        File.Delete(missing switch
        {
            "source" => source,
            "library" => library,
            _ => SourceManifestStore.GetManifestPath(library),
        });

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.PrepareAsync());
        Assert.Empty(Directory.EnumerateFiles(fixture.InboxRoot));
    }

    [Fact]
    public async Task Prepare_ValidatesOptionalMetadataAndRefusesReadyOverwrite()
    {
        using var fixture = new MediaPackageTestFixture();
        (string source, _) = await fixture.AddMembersAsync(
            sourceMetadata: MediaPackageTestFixture.Metadata(),
            libraryMetadata: MediaPackageTestFixture.Metadata());
        const string packageId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        ReadyPackagePreparationResult first = await fixture.PrepareAsync(packageId: packageId);

        await Assert.ThrowsAsync<IOException>(() => fixture.PrepareAsync(packageId: packageId));
        await File.WriteAllTextAsync(AssetMetadataStore.GetMetadataPath(source), "{ malformed }");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PrepareAsync(
            packageId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        Assert.True(File.Exists(Path.Combine(fixture.InboxRoot, first.ReadyFileName)));
    }

    [Fact]
    public async Task Verify_AcceptsValidPackageAndUsesSequentialMemberHashing()
    {
        using var fixture = new MediaPackageTestFixture();
        await fixture.AddMembersAsync(
            sourceMetadata: MediaPackageTestFixture.Metadata(),
            libraryMetadata: MediaPackageTestFixture.Metadata());
        ReadyPackagePreparationResult prepared = await fixture.PrepareAsync();
        var hasher = new CountingHasher();
        var verifier = new ReadyMediaPackageVerifier(hasher);

        VerifiedReadyMediaPackage verified = await verifier.VerifyAsync(
            fixture.PackageOptions,
            prepared.Manifest,
            CancellationToken.None);

        Assert.NotNull(verified.SourceMetadata);
        Assert.NotNull(verified.LibraryMetadata);
        Assert.Equal(5, hasher.Calls);
        Assert.Equal(1, hasher.MaximumConcurrentCalls);
    }

    [Fact]
    public async Task Verify_RejectsMissingTruncatedAndHashMismatchedMembers()
    {
        using var fixture = new MediaPackageTestFixture();
        (string _, string library) = await fixture.AddMembersAsync();
        ReadyPackagePreparationResult prepared = await fixture.PrepareAsync();
        var verifier = new ReadyMediaPackageVerifier();

        File.Delete(library);
        await Assert.ThrowsAsync<FileNotFoundException>(() => verifier.VerifyAsync(
            fixture.PackageOptions,
            prepared.Manifest,
            CancellationToken.None));

        await File.WriteAllTextAsync(library, "x");
        MediaPackageVerificationException mismatch = await Assert.ThrowsAsync<MediaPackageVerificationException>(
            () => verifier.VerifyAsync(
            fixture.PackageOptions,
            prepared.Manifest,
            CancellationToken.None));
        Assert.Equal(MediaLibraryRefreshIssueCode.PackageMemberMismatch, mismatch.Code);
    }

    [Fact]
    public async Task Verify_RejectsMemberChangedDuringHash()
    {
        using var fixture = new MediaPackageTestFixture();
        await fixture.AddMembersAsync();
        ReadyPackagePreparationResult prepared = await fixture.PrepareAsync();
        var verifier = new ReadyMediaPackageVerifier(new ChangedHasher());

        await Assert.ThrowsAsync<PackageMemberChangedException>(() => verifier.VerifyAsync(
            fixture.PackageOptions,
            prepared.Manifest,
            CancellationToken.None));
    }

    [Fact]
    public async Task Verify_RejectsMalformedTechnicalManifestEvenWhenDeclaredHashMatches()
    {
        using var fixture = new MediaPackageTestFixture();
        (string _, string library) = await fixture.AddMembersAsync();
        ReadyPackagePreparationResult prepared = await fixture.PrepareAsync();
        string technicalPath = SourceManifestStore.GetManifestPath(library);
        await File.WriteAllTextAsync(technicalPath, "{ malformed }");
        PackageFileHash technical = await new PackageFileHasher().HashStableAsync(
            technicalPath,
            1024 * 1024,
            CancellationToken.None);
        ReadyMediaPackageManifest changed = prepared.Manifest with
        {
            TechnicalManifest = new ReadyPackageMember
            {
                Length = technical.Length,
                Sha256 = technical.Sha256,
            },
        };

        MediaPackageVerificationException exception =
            await Assert.ThrowsAsync<MediaPackageVerificationException>(() =>
                new ReadyMediaPackageVerifier().VerifyAsync(
            fixture.PackageOptions,
            changed,
            CancellationToken.None));
        Assert.Equal(MediaLibraryRefreshIssueCode.InvalidTechnicalManifest, exception.Code);
    }

    [Fact]
    public async Task Verify_RejectsMalformedProgrammingMetadataEvenWhenDeclaredHashMatches()
    {
        using var fixture = new MediaPackageTestFixture();
        (string source, _) = await fixture.AddMembersAsync(
            sourceMetadata: MediaPackageTestFixture.Metadata());
        ReadyPackagePreparationResult prepared = await fixture.PrepareAsync();
        string metadataPath = AssetMetadataStore.GetMetadataPath(source);
        await File.WriteAllTextAsync(metadataPath, "{ malformed }");
        PackageFileHash metadata = await new PackageFileHasher().HashStableAsync(
            metadataPath,
            1024 * 1024,
            CancellationToken.None);
        ReadyMediaPackageManifest changed = prepared.Manifest with
        {
            SourceProgrammingMetadata = new ReadyPackageOptionalMember
            {
                Present = true,
                Length = metadata.Length,
                Sha256 = metadata.Sha256,
            },
        };

        MediaPackageVerificationException exception =
            await Assert.ThrowsAsync<MediaPackageVerificationException>(() =>
                new ReadyMediaPackageVerifier().VerifyAsync(
            fixture.PackageOptions,
            changed,
            CancellationToken.None));
        Assert.Equal(MediaLibraryRefreshIssueCode.InvalidProgrammingMetadata, exception.Code);
    }

    private sealed class CountingHasher : IPackageFileHasher
    {
        private readonly PackageFileHasher _inner = new();
        private int _active;
        public int Calls { get; private set; }
        public int MaximumConcurrentCalls { get; private set; }

        public async Task<PackageFileHash> HashStableAsync(
            string path,
            int maximumCapturedBytes,
            CancellationToken cancellationToken)
        {
            Calls++;
            int active = Interlocked.Increment(ref _active);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, active);
            try
            {
                return await _inner.HashStableAsync(path, maximumCapturedBytes, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class ChangedHasher : IPackageFileHasher
    {
        public Task<PackageFileHash> HashStableAsync(
            string path,
            int maximumCapturedBytes,
            CancellationToken cancellationToken) =>
            throw new PackageMemberChangedException();
    }
}
