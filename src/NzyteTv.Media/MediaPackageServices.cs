using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record MediaPackageStorageOptions(
    string MediaRoot,
    string? InboxRoot = null)
{
    public string SourceRoot => Path.Combine(MediaRoot, "source");

    public string LibraryRoot => Path.Combine(MediaRoot, "library");

    public string EffectiveInboxRoot => InboxRoot ?? Path.Combine(MediaRoot, "inbox");

    public MediaPackageStorageOptions Validate(bool requireInbox)
    {
        if (!Path.IsPathFullyQualified(MediaRoot)
            || (InboxRoot is not null && !Path.IsPathFullyQualified(InboxRoot)))
        {
            throw new InvalidOperationException("Media-package roots must be absolute trusted paths.");
        }

        string mediaRoot = Path.GetFullPath(MediaRoot);
        string? inboxRoot = InboxRoot is null ? null : Path.GetFullPath(InboxRoot);
        if (!Directory.Exists(mediaRoot)
            || !Directory.Exists(Path.Combine(mediaRoot, "source"))
            || !Directory.Exists(Path.Combine(mediaRoot, "library")))
        {
            throw new DirectoryNotFoundException("The configured media package roots are unavailable.");
        }

        MetadataPathSafety.EnsureNoReparsePoint(mediaRoot, mediaRoot);
        MetadataPathSafety.EnsureNoReparsePoint(mediaRoot, Path.Combine(mediaRoot, "source"));
        MetadataPathSafety.EnsureNoReparsePoint(mediaRoot, Path.Combine(mediaRoot, "library"));
        string effectiveInbox = inboxRoot ?? Path.Combine(mediaRoot, "inbox");
        if (requireInbox && !Directory.Exists(effectiveInbox))
        {
            throw new DirectoryNotFoundException("The configured READY inbox is unavailable.");
        }

        if (Directory.Exists(effectiveInbox))
        {
            MetadataPathSafety.EnsureNoReparsePoint(effectiveInbox, effectiveInbox);
        }

        return this with
        {
            MediaRoot = mediaRoot,
            InboxRoot = inboxRoot,
        };
    }
}

public sealed record PackageFileHash(
    long Length,
    string Sha256,
    DateTime LastWriteTimeUtc,
    byte[]? Content);

public interface IPackageFileHasher
{
    Task<PackageFileHash> HashStableAsync(
        string path,
        int maximumCapturedBytes,
        CancellationToken cancellationToken);
}

public sealed class PackageFileHasher : IPackageFileHasher
{
    private const int BufferSize = 128 * 1024;

    public async Task<PackageFileHash> HashStableAsync(
        string path,
        int maximumCapturedBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        var before = new FileInfo(fullPath);
        if (!before.Exists)
        {
            throw new FileNotFoundException("A declared package member is missing.", fullPath);
        }

        long length = before.Length;
        DateTime lastWrite = before.LastWriteTimeUtc;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[]? captured = length <= maximumCapturedBytes ? new byte[checked((int)length)] : null;
        int capturedOffset = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                if (captured is not null)
                {
                    Buffer.BlockCopy(buffer, 0, captured, capturedOffset, read);
                    capturedOffset += read;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var after = new FileInfo(fullPath);
        if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != lastWrite)
        {
            throw new PackageMemberChangedException();
        }

        return new PackageFileHash(
            length,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            lastWrite,
            captured);
    }
}

public sealed class PackageMemberChangedException : IOException
{
    public PackageMemberChangedException()
        : base("A package member changed during verification.")
    {
    }
}

public sealed class MediaPackageVerificationException(
    MediaLibraryRefreshIssueCode code) : IOException("A READY package member is invalid.")
{
    public MediaLibraryRefreshIssueCode Code { get; } = code;
}

internal static class PackageFileStability
{
    public static void EnsureUnchanged(string path, PackageFileHash observed)
    {
        var current = new FileInfo(path);
        if (!current.Exists
            || current.Length != observed.Length
            || current.LastWriteTimeUtc != observed.LastWriteTimeUtc)
        {
            throw new PackageMemberChangedException();
        }
    }
}

public sealed record ReadyPackagePreparationResult(
    string PackageId,
    string ReadyFileName,
    string ManifestDigest,
    ReadyMediaPackageManifest Manifest);

public sealed class MediaPackagePreparer
{
    private const int MaximumSidecarBytes = 1024 * 1024;
    private readonly IPackageFileHasher _hasher;
    private readonly ISourceManifestStore _sourceManifestStore;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _packageIdFactory;

    public MediaPackagePreparer(
        IPackageFileHasher? hasher = null,
        ISourceManifestStore? sourceManifestStore = null,
        TimeProvider? timeProvider = null,
        Func<string>? packageIdFactory = null)
    {
        _hasher = hasher ?? new PackageFileHasher();
        _sourceManifestStore = sourceManifestStore ?? new SourceManifestStore();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _packageIdFactory = packageIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    }

    public async Task<ReadyPackagePreparationResult> PrepareAsync(
        MediaPackageStorageOptions options,
        string sourceRelativePath,
        CancellationToken cancellationToken)
    {
        MediaPackageStorageOptions validated = options.Validate(requireInbox: false);
        string sourceRelative = MetadataPathSafety.NormalizeRelativeMediaPath(sourceRelativePath);
        string sourcePath = MetadataPathSafety.ResolveRelativeMediaPath(validated.SourceRoot, sourceRelative);
        string libraryPath = LibraryPathPolicy.GetDestinationPath(
            validated.SourceRoot,
            validated.LibraryRoot,
            sourcePath);
        string libraryRelative = Path.GetRelativePath(validated.LibraryRoot, libraryPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        ReadyMediaPackageValidator.Validate(new ReadyMediaPackageManifest
        {
            PackageId = "00000000000000000000000000000001",
            PackageCreatedAt = _timeProvider.GetUtcNow(),
            SourceRelativePath = sourceRelative,
            LibraryRelativePath = libraryRelative,
            Source = Placeholder(),
            Library = Placeholder(),
            TechnicalManifest = Placeholder(),
            SourceProgrammingMetadata = MissingOptional(),
            LibraryProgrammingMetadata = MissingOptional(),
        });

        string technicalManifestPath = SourceManifestStore.GetManifestPath(libraryPath);
        RequireRegularContainedFile(validated.SourceRoot, sourcePath);
        RequireRegularContainedFile(validated.LibraryRoot, libraryPath);
        RequireRegularContainedFile(validated.LibraryRoot, technicalManifestPath);
        string sourceMetadataPath = AssetMetadataStore.GetMetadataPath(sourcePath);
        string libraryMetadataPath = AssetMetadataStore.GetMetadataPath(libraryPath);
        bool sourceMetadataExists = File.Exists(sourceMetadataPath);
        bool libraryMetadataExists = File.Exists(libraryMetadataPath);
        PackageFileHash source = await _hasher.HashStableAsync(sourcePath, 0, cancellationToken)
            .ConfigureAwait(false);
        PackageFileHash library = await _hasher.HashStableAsync(libraryPath, 0, cancellationToken)
            .ConfigureAwait(false);
        PackageFileHash technical = await _hasher.HashStableAsync(
            technicalManifestPath,
            MaximumSidecarBytes,
            cancellationToken).ConfigureAwait(false);
        PackageFileHash? sourceMetadata = await HashOptionalMetadataAsync(
            validated.SourceRoot,
            sourceMetadataPath,
            sourceMetadataExists,
            cancellationToken).ConfigureAwait(false);
        PackageFileHash? libraryMetadata = await HashOptionalMetadataAsync(
            validated.LibraryRoot,
            libraryMetadataPath,
            libraryMetadataExists,
            cancellationToken).ConfigureAwait(false);
        SourceFingerprint expected = _sourceManifestStore.CreateFingerprint(validated.SourceRoot, sourcePath);
        ManifestMatchResult manifestMatch = _sourceManifestStore.Evaluate(libraryPath, expected);
        if (!manifestMatch.IsMatch)
        {
            throw new InvalidDataException("The technical manifest does not match the source package member.");
        }

        PackageFileStability.EnsureUnchanged(sourcePath, source);
        PackageFileStability.EnsureUnchanged(libraryPath, library);
        PackageFileStability.EnsureUnchanged(technicalManifestPath, technical);
        EnsureOptionalUnchanged(sourceMetadataPath, sourceMetadataExists, sourceMetadata);
        EnsureOptionalUnchanged(libraryMetadataPath, libraryMetadataExists, libraryMetadata);

        string packageId = _packageIdFactory();
        var manifest = new ReadyMediaPackageManifest
        {
            PackageId = packageId,
            PackageCreatedAt = _timeProvider.GetUtcNow().ToUniversalTime(),
            SourceRelativePath = sourceRelative,
            LibraryRelativePath = libraryRelative,
            Source = Required(source),
            Library = Required(library),
            TechnicalManifest = Required(technical),
            SourceProgrammingMetadata = Optional(sourceMetadata),
            LibraryProgrammingMetadata = Optional(libraryMetadata),
        };
        string json = ReadyMediaPackageSerializer.Serialize(manifest);
        string readyFileName = packageId + ReadyMediaPackageManifest.FileSuffix;
        Directory.CreateDirectory(validated.EffectiveInboxRoot);
        MetadataPathSafety.EnsureNoReparsePoint(validated.EffectiveInboxRoot, validated.EffectiveInboxRoot);
        string finalPath = Path.Combine(validated.EffectiveInboxRoot, readyFileName);
        if (File.Exists(finalPath))
        {
            throw new IOException("The READY manifest already exists and will not be overwritten.");
        }

        string temporaryPath = Path.Combine(
            validated.EffectiveInboxRoot,
            $".{readyFileName}.{Guid.NewGuid():N}.partial");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, finalPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return new ReadyPackagePreparationResult(
            packageId,
            readyFileName,
            ReadyMediaPackageSerializer.CalculateDigest(manifest),
            manifest);
    }

    private async Task<PackageFileHash?> HashOptionalMetadataAsync(
        string root,
        string path,
        bool exists,
        CancellationToken cancellationToken)
    {
        if (!exists) return null;
        RequireRegularContainedFile(root, path);
        PackageFileHash value = await _hasher.HashStableAsync(path, MaximumSidecarBytes, cancellationToken)
            .ConfigureAwait(false);
        _ = AssetMetadataStore.Deserialize(value.Content!, "package programming metadata");
        return value;
    }

    private static void RequireRegularContainedFile(string root, string path)
    {
        MetadataPathSafety.EnsureNoReparsePoint(root, path);
        if (!File.Exists(path)) throw new FileNotFoundException("A required package member is missing.", path);
    }

    private static void EnsureOptionalUnchanged(
        string path,
        bool existed,
        PackageFileHash? observed)
    {
        if (!existed)
        {
            if (File.Exists(path)) throw new PackageMemberChangedException();
            return;
        }

        PackageFileStability.EnsureUnchanged(path, observed!);
    }

    private static ReadyPackageMember Required(PackageFileHash value) => new()
    {
        Length = value.Length,
        Sha256 = value.Sha256,
    };

    private static ReadyPackageOptionalMember Optional(PackageFileHash? value) => value is null
        ? MissingOptional()
        : new ReadyPackageOptionalMember
        {
            Present = true,
            Length = value.Length,
            Sha256 = value.Sha256,
        };

    private static ReadyPackageMember Placeholder() => new()
    {
        Length = 1,
        Sha256 = new string('0', 64),
    };

    private static ReadyPackageOptionalMember MissingOptional() => new() { Present = false };
}

public sealed record VerifiedReadyMediaPackage(
    ReadyMediaPackageManifest Manifest,
    string ManifestDigest,
    AssetMetadataDocument? SourceMetadata,
    AssetMetadataDocument? LibraryMetadata);

public sealed class ReadyMediaPackageVerifier
{
    private const int MaximumSidecarBytes = 1024 * 1024;
    private readonly IPackageFileHasher _hasher;
    private readonly ISourceManifestStore _sourceManifestStore;

    public ReadyMediaPackageVerifier(
        IPackageFileHasher? hasher = null,
        ISourceManifestStore? sourceManifestStore = null)
    {
        _hasher = hasher ?? new PackageFileHasher();
        _sourceManifestStore = sourceManifestStore ?? new SourceManifestStore();
    }

    public async Task<VerifiedReadyMediaPackage> VerifyAsync(
        MediaPackageStorageOptions options,
        ReadyMediaPackageManifest manifest,
        CancellationToken cancellationToken)
    {
        MediaPackageStorageOptions validated = options.Validate(requireInbox: true);
        ReadyMediaPackageValidator.Validate(manifest);
        string sourcePath = MetadataPathSafety.ResolveRelativeMediaPath(
            validated.SourceRoot,
            manifest.SourceRelativePath!);
        string libraryPath = MetadataPathSafety.ResolveRelativeMediaPath(
            validated.LibraryRoot,
            manifest.LibraryRelativePath!);
        string derivedLibrary = LibraryPathPolicy.GetDestinationPath(
            validated.SourceRoot,
            validated.LibraryRoot,
            sourcePath);
        if (!string.Equals(derivedLibrary, libraryPath, GetPathComparison()))
        {
            throw new InvalidDataException("The READY package violates the library path policy.");
        }

        string technicalPath = SourceManifestStore.GetManifestPath(libraryPath);
        string sourceMetadataPath = AssetMetadataStore.GetMetadataPath(sourcePath);
        string libraryMetadataPath = AssetMetadataStore.GetMetadataPath(libraryPath);
        ValidateOptionalPresence(manifest.SourceProgrammingMetadata!, sourceMetadataPath);
        ValidateOptionalPresence(manifest.LibraryProgrammingMetadata!, libraryMetadataPath);

        PackageFileHash source = await VerifyMemberAsync(
            validated.SourceRoot,
            sourcePath,
            manifest.Source!,
            0,
            cancellationToken)
            .ConfigureAwait(false);
        PackageFileHash library = await VerifyMemberAsync(
            validated.LibraryRoot,
            libraryPath,
            manifest.Library!,
            0,
            cancellationToken)
            .ConfigureAwait(false);
        PackageFileHash technical = await VerifyMemberAsync(
            validated.LibraryRoot,
            technicalPath,
            manifest.TechnicalManifest!,
            MaximumSidecarBytes,
            cancellationToken).ConfigureAwait(false);

        SourceFingerprint expected = _sourceManifestStore.CreateFingerprint(validated.SourceRoot, sourcePath);
        if (!_sourceManifestStore.Evaluate(libraryPath, expected).IsMatch)
        {
            throw new MediaPackageVerificationException(
                MediaLibraryRefreshIssueCode.InvalidTechnicalManifest);
        }

        AssetMetadataDocument? sourceMetadata = await VerifyOptionalMetadataAsync(
            validated.SourceRoot,
            sourceMetadataPath,
            manifest.SourceProgrammingMetadata!,
            cancellationToken).ConfigureAwait(false);
        AssetMetadataDocument? libraryMetadata = await VerifyOptionalMetadataAsync(
            validated.LibraryRoot,
            libraryMetadataPath,
            manifest.LibraryProgrammingMetadata!,
            cancellationToken).ConfigureAwait(false);
        PackageFileStability.EnsureUnchanged(sourcePath, source);
        PackageFileStability.EnsureUnchanged(libraryPath, library);
        PackageFileStability.EnsureUnchanged(technicalPath, technical);
        EnsureOptionalUnchanged(
            sourceMetadataPath,
            manifest.SourceProgrammingMetadata!,
            sourceMetadata);
        EnsureOptionalUnchanged(
            libraryMetadataPath,
            manifest.LibraryProgrammingMetadata!,
            libraryMetadata);
        return new VerifiedReadyMediaPackage(
            manifest,
            ReadyMediaPackageSerializer.CalculateDigest(manifest),
            sourceMetadata,
            libraryMetadata);
    }

    private async Task<PackageFileHash> VerifyMemberAsync(
        string root,
        string path,
        ReadyPackageMember declaration,
        int captureBytes,
        CancellationToken cancellationToken)
    {
        MetadataPathSafety.EnsureNoReparsePoint(root, path);
        PackageFileHash actual = await _hasher.HashStableAsync(path, captureBytes, cancellationToken)
            .ConfigureAwait(false);
        if (actual.Length != declaration.Length
            || !string.Equals(actual.Sha256, declaration.Sha256, StringComparison.Ordinal))
        {
            throw new MediaPackageVerificationException(
                MediaLibraryRefreshIssueCode.PackageMemberMismatch);
        }

        return actual;
    }

    private async Task<AssetMetadataDocument?> VerifyOptionalMetadataAsync(
        string root,
        string path,
        ReadyPackageOptionalMember declaration,
        CancellationToken cancellationToken)
    {
        if (!declaration.Present) return null;
        var required = new ReadyPackageMember
        {
            Length = declaration.Length!.Value,
            Sha256 = declaration.Sha256,
        };
        PackageFileHash actual = await VerifyMemberAsync(
            root,
            path,
            required,
            MaximumSidecarBytes,
            cancellationToken).ConfigureAwait(false);
        AssetMetadata metadata;
        try
        {
            metadata = AssetMetadataStore.Deserialize(actual.Content!, "READY programming metadata");
        }
        catch (Exception exception) when (exception is InvalidDataException or AssetMetadataValidationException)
        {
            throw new MediaPackageVerificationException(
                MediaLibraryRefreshIssueCode.InvalidProgrammingMetadata);
        }

        return new AssetMetadataDocument(metadata, actual.Content!);
    }

    private static void ValidateOptionalPresence(ReadyPackageOptionalMember declaration, string path)
    {
        if (declaration.Present != File.Exists(path))
        {
            throw new InvalidDataException("A READY programming-metadata declaration does not match the package.");
        }
    }

    private static void EnsureOptionalUnchanged(
        string path,
        ReadyPackageOptionalMember declaration,
        AssetMetadataDocument? document)
    {
        if (!declaration.Present)
        {
            if (File.Exists(path)) throw new PackageMemberChangedException();
            return;
        }

        var current = new FileInfo(path);
        if (!current.Exists
            || current.Length != declaration.Length
            || !string.Equals(
                MediaPackageHash.Sha256(document!.JsonBytes),
                declaration.Sha256,
                StringComparison.Ordinal))
        {
            throw new PackageMemberChangedException();
        }
    }

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
