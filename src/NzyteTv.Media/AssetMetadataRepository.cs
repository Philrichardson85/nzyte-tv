using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzyteTv.Core;

namespace NzyteTv.Media;

public enum AssetMetadataStorageMode
{
    Adjacent,
    ExternalGeneration,
}

public enum AssetMetadataTree
{
    Source,
    Library,
}

public sealed record AssetMetadataStorageOptions
{
    public AssetMetadataStorageMode Mode { get; init; } = AssetMetadataStorageMode.Adjacent;

    public string? ExternalRoot { get; init; }

    public static AssetMetadataStorageOptions Adjacent { get; } = new();

    public static AssetMetadataStorageOptions External(string root) => new()
    {
        Mode = AssetMetadataStorageMode.ExternalGeneration,
        ExternalRoot = root,
    };

    public AssetMetadataStorageOptions Validate()
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new InvalidOperationException("The asset metadata storage mode is invalid.");
        }

        if (Mode == AssetMetadataStorageMode.Adjacent)
        {
            if (!string.IsNullOrWhiteSpace(ExternalRoot))
            {
                throw new InvalidOperationException(
                    "An external metadata root cannot be supplied in adjacent metadata mode.");
            }

            return this;
        }

        if (string.IsNullOrWhiteSpace(ExternalRoot) || !Path.IsPathFullyQualified(ExternalRoot))
        {
            throw new InvalidOperationException(
                "External-generation metadata mode requires an absolute external metadata root.");
        }

        return this with { ExternalRoot = Path.GetFullPath(ExternalRoot) };
    }
}

public sealed record AssetMetadataSnapshotIdentity(
    AssetMetadataStorageMode Mode,
    long? Revision = null,
    string? GenerationId = null);

public sealed record AssetMetadataDocument(AssetMetadata Metadata, byte[] JsonBytes);

public interface IAssetMetadataSnapshot
{
    AssetMetadataSnapshotIdentity Identity { get; }

    IReadOnlyList<string> DiscoverRelativeMediaPaths();

    bool Exists(string relativeMediaPath);

    AssetMetadataDocument Read(string relativeMediaPath);
}

public interface IAssetMetadataRepository
{
    IAssetMetadataSnapshot Pin(AssetMetadataTree tree, string mediaTreeRoot);

    IAssetMetadataSnapshot Pin(
        AssetMetadataTree tree,
        string mediaTreeRoot,
        AssetMetadataSnapshotIdentity identity);
}

public static class AssetMetadataRepository
{
    public static IAssetMetadataRepository Create(AssetMetadataStorageOptions? options = null)
    {
        AssetMetadataStorageOptions validated = (options ?? AssetMetadataStorageOptions.Adjacent).Validate();
        return validated.Mode switch
        {
            AssetMetadataStorageMode.Adjacent => new AdjacentAssetMetadataRepository(),
            AssetMetadataStorageMode.ExternalGeneration =>
                new ExternalAssetMetadataGenerationStore(validated.ExternalRoot!),
            _ => throw new InvalidOperationException("The asset metadata storage mode is invalid."),
        };
    }
}

public sealed class AdjacentAssetMetadataRepository(IAssetMetadataStore? metadataStore = null)
    : IAssetMetadataRepository
{
    private readonly IAssetMetadataStore _metadataStore = metadataStore ?? new AssetMetadataStore();

    public IAssetMetadataSnapshot Pin(AssetMetadataTree tree, string mediaTreeRoot) =>
        new AdjacentAssetMetadataSnapshot(mediaTreeRoot, _metadataStore);

    public IAssetMetadataSnapshot Pin(
        AssetMetadataTree tree,
        string mediaTreeRoot,
        AssetMetadataSnapshotIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Mode != AssetMetadataStorageMode.Adjacent
            || identity.Revision is not null
            || identity.GenerationId is not null)
        {
            throw new InvalidDataException("The requested metadata snapshot is not an adjacent metadata snapshot.");
        }

        return Pin(tree, mediaTreeRoot);
    }

    private sealed class AdjacentAssetMetadataSnapshot(
        string mediaTreeRoot,
        IAssetMetadataStore metadataStore) : IAssetMetadataSnapshot
    {
        private readonly string _root = Path.GetFullPath(mediaTreeRoot);

        public AssetMetadataSnapshotIdentity Identity { get; } =
            new(AssetMetadataStorageMode.Adjacent);

        public IReadOnlyList<string> DiscoverRelativeMediaPaths() =>
            MetadataPathSafety.DiscoverRelativeMetadataMediaPaths(_root);

        public bool Exists(string relativeMediaPath)
        {
            string mediaPath = MetadataPathSafety.ResolveRelativeMediaPath(_root, relativeMediaPath);
            return File.Exists(AssetMetadataStore.GetMetadataPath(mediaPath));
        }

        public AssetMetadataDocument Read(string relativeMediaPath)
        {
            string mediaPath = MetadataPathSafety.ResolveRelativeMediaPath(_root, relativeMediaPath);
            string metadataPath = AssetMetadataStore.GetMetadataPath(mediaPath);
            MetadataPathSafety.EnsureNoReparsePoint(_root, metadataPath);
            byte[] content = File.ReadAllBytes(metadataPath);
            AssetMetadata metadata = metadataStore.Read(mediaPath);
            byte[] after = File.ReadAllBytes(metadataPath);
            if (!content.AsSpan().SequenceEqual(after))
            {
                throw new IOException("Programming metadata changed while it was being read.");
            }

            return new AssetMetadataDocument(metadata, content);
        }
    }
}

public sealed class AssetMetadataGenerationConflictException(long expectedRevision, long actualRevision)
    : InvalidOperationException("The current metadata generation changed before publication.")
{
    public long ExpectedRevision { get; } = expectedRevision;

    public long ActualRevision { get; } = actualRevision;
}

public sealed record AssetMetadataGenerationPointer
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public long Revision { get; init; }

    public string? GenerationId { get; init; }
}

public sealed record AssetMetadataGenerationManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public long Revision { get; init; }

    public string? GenerationId { get; init; }
}

public sealed record AssetMetadataGenerationRecord(
    AssetMetadataTree Tree,
    string RelativeMediaPath,
    AssetMetadata Metadata);

public sealed class ExternalAssetMetadataGenerationStore : IAssetMetadataRepository
{
    public const string CurrentFileName = "current.json";
    public const string GenerationsDirectoryName = "generations";
    public const string GenerationManifestFileName = "generation.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly string _root;
    private readonly IAtomicTextFileWriter _writer;
    private readonly TimeSpan _lockTimeout;

    public ExternalAssetMetadataGenerationStore(
        string root,
        IAtomicTextFileWriter? writer = null,
        TimeSpan? lockTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("The external metadata root must be absolute.", nameof(root));
        }

        _root = Path.GetFullPath(root);
        _writer = writer ?? new AtomicTextFileWriter();
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(10);
    }

    public IAssetMetadataSnapshot Pin(AssetMetadataTree tree, string mediaTreeRoot)
    {
        AssetMetadataGenerationPointer pointer = ReadCurrent();
        return Pin(tree, mediaTreeRoot, new AssetMetadataSnapshotIdentity(
            AssetMetadataStorageMode.ExternalGeneration,
            pointer.Revision,
            pointer.GenerationId));
    }

    public IAssetMetadataSnapshot Pin(
        AssetMetadataTree tree,
        string mediaTreeRoot,
        AssetMetadataSnapshotIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Mode == AssetMetadataStorageMode.Adjacent
            && identity.Revision is null
            && identity.GenerationId is null)
        {
            // A frozen snapshot without generation identity predates external generations.
            // Preserve that snapshot's adjacent-sidecar authority even after cutover.
            return new AdjacentAssetMetadataRepository().Pin(tree, mediaTreeRoot, identity);
        }

        if (identity.Mode != AssetMetadataStorageMode.ExternalGeneration
            || identity.Revision is not long revision
            || string.IsNullOrWhiteSpace(identity.GenerationId))
        {
            throw new InvalidDataException("The requested metadata snapshot is not a complete external generation identity.");
        }

        ValidateRevision(revision);
        string generationId = ValidateGenerationId(identity.GenerationId);
        string generationRoot = GetGenerationRoot(generationId);
        ValidateGeneration(generationId, revision, generationRoot);

        string treeRoot = Path.Combine(generationRoot, GetTreeDirectoryName(tree));
        return new ExternalAssetMetadataSnapshot(
            mediaTreeRoot,
            treeRoot,
            new AssetMetadataSnapshotIdentity(
                AssetMetadataStorageMode.ExternalGeneration,
                revision,
                generationId));
    }

    public AssetMetadataGenerationPointer ReadCurrent()
    {
        string path = Path.Combine(_root, CurrentFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The current external metadata generation pointer is missing.", path);
        }

        MetadataPathSafety.EnsureNoReparsePoint(_root, path);
        AssetMetadataGenerationPointer pointer = DeserializeStrict<AssetMetadataGenerationPointer>(
            File.ReadAllText(path),
            "external metadata current pointer");
        ValidatePointer(pointer);
        ValidateGeneration(pointer.GenerationId!, pointer.Revision);
        return pointer;
    }

    public async Task CreateGenerationAsync(
        string generationId,
        long revision,
        IReadOnlyCollection<AssetMetadataGenerationRecord> records,
        CancellationToken cancellationToken)
    {
        generationId = ValidateGenerationId(generationId);
        ValidateRevision(revision);
        ArgumentNullException.ThrowIfNull(records);
        Directory.CreateDirectory(_root);
        MetadataPathSafety.EnsureNoReparsePoint(_root, _root);
        string generationsRoot = Path.Combine(_root, GenerationsDirectoryName);
        Directory.CreateDirectory(generationsRoot);
        MetadataPathSafety.EnsureNoReparsePoint(_root, generationsRoot);
        string finalRoot = GetGenerationRoot(generationId);
        if (Directory.Exists(finalRoot) || File.Exists(finalRoot))
        {
            throw new IOException("The external metadata generation already exists and is immutable.");
        }

        string stagingRoot = Path.Combine(_root, $".generation-{Guid.NewGuid():N}.staging");
        try
        {
            string sourceRoot = Path.Combine(stagingRoot, "source");
            string libraryRoot = Path.Combine(stagingRoot, "library");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(libraryRoot);
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AssetMetadataGenerationRecord record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(record);
                ArgumentNullException.ThrowIfNull(record.Metadata);
                string relative = MetadataPathSafety.NormalizeRelativeMediaPath(record.RelativeMediaPath);
                string identity = $"{GetTreeDirectoryName(record.Tree)}/{relative}";
                if (!identities.Add(identity))
                {
                    throw new InvalidDataException("The metadata generation contains a duplicate relative media identity.");
                }

                string treeRoot = record.Tree == AssetMetadataTree.Source ? sourceRoot : libraryRoot;
                string mediaPath = MetadataPathSafety.ResolveRelativeMediaPath(treeRoot, relative);
                string metadataPath = AssetMetadataStore.GetMetadataPath(mediaPath);
                Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
                await File.WriteAllTextAsync(
                    metadataPath,
                    AssetMetadataStore.Serialize(record.Metadata),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken).ConfigureAwait(false);
            }

            var manifest = new AssetMetadataGenerationManifest
            {
                Revision = revision,
                GenerationId = generationId,
            };
            await File.WriteAllTextAsync(
                Path.Combine(stagingRoot, GenerationManifestFileName),
                Serialize(manifest),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            Directory.Move(stagingRoot, finalRoot);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    public async Task<AssetMetadataGenerationPointer> PublishCurrentAsync(
        string generationId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        generationId = ValidateGenerationId(generationId);
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        Directory.CreateDirectory(_root);
        MetadataPathSafety.EnsureNoReparsePoint(_root, _root);
        await using FileStream heldLock = await AcquirePublicationLockAsync(cancellationToken).ConfigureAwait(false);
        AssetMetadataGenerationPointer? current = ReadCurrentIfExists();
        long actualRevision = current?.Revision ?? 0;
        if (actualRevision != expectedRevision)
        {
            throw new AssetMetadataGenerationConflictException(expectedRevision, actualRevision);
        }

        string generationRoot = GetGenerationRoot(generationId);
        long nextRevision = checked(expectedRevision + 1);
        ValidateGeneration(generationId, nextRevision, generationRoot);

        var pointer = new AssetMetadataGenerationPointer
        {
            Revision = nextRevision,
            GenerationId = generationId,
        };
        await _writer.WriteAsync(
            Path.Combine(_root, CurrentFileName),
            Serialize(pointer),
            cancellationToken).ConfigureAwait(false);
        return pointer;
    }

    private AssetMetadataGenerationPointer? ReadCurrentIfExists()
    {
        string path = Path.Combine(_root, CurrentFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        MetadataPathSafety.EnsureNoReparsePoint(_root, path);
        AssetMetadataGenerationPointer pointer = DeserializeStrict<AssetMetadataGenerationPointer>(
            File.ReadAllText(path),
            "external metadata current pointer");
        ValidatePointer(pointer);
        ValidateGeneration(pointer.GenerationId!, pointer.Revision);
        return pointer;
    }

    private void ValidateGeneration(string generationId, long revision, string? generationRoot = null)
    {
        generationRoot ??= GetGenerationRoot(generationId);
        AssetMetadataGenerationManifest manifest = ReadGenerationManifest(generationRoot);
        EnsureManifestMatches(generationId, revision, manifest);
        MetadataPathSafety.NormalizeExistingDirectory(
            Path.Combine(generationRoot, GetTreeDirectoryName(AssetMetadataTree.Source)),
            "source metadata generation tree");
        MetadataPathSafety.NormalizeExistingDirectory(
            Path.Combine(generationRoot, GetTreeDirectoryName(AssetMetadataTree.Library)),
            "library metadata generation tree");
    }

    private AssetMetadataGenerationManifest ReadGenerationManifest(string generationRoot)
    {
        MetadataPathSafety.NormalizeExistingDirectory(generationRoot, "external metadata generation");
        string path = Path.Combine(generationRoot, GenerationManifestFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The external metadata generation manifest is missing.", path);
        }

        MetadataPathSafety.EnsureNoReparsePoint(generationRoot, path);
        AssetMetadataGenerationManifest manifest = DeserializeStrict<AssetMetadataGenerationManifest>(
            File.ReadAllText(path),
            "external metadata generation manifest");
        if (manifest.SchemaVersion != AssetMetadataGenerationManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The external metadata generation manifest schema is unsupported.");
        }

        ValidateRevision(manifest.Revision);
        _ = ValidateGenerationId(manifest.GenerationId);
        _ = MetadataPathSafety.NormalizeExistingDirectory(
            Path.Combine(generationRoot, "source"),
            "external source metadata generation tree");
        _ = MetadataPathSafety.NormalizeExistingDirectory(
            Path.Combine(generationRoot, "library"),
            "external library metadata generation tree");
        return manifest;
    }

    private string GetGenerationRoot(string generationId)
    {
        string generationsRoot = Path.Combine(_root, GenerationsDirectoryName);
        return MetadataPathSafety.ResolveContainedPath(generationsRoot, ValidateGenerationId(generationId));
    }

    private async Task<FileStream> AcquirePublicationLockAsync(CancellationToken cancellationToken)
    {
        string lockPath = Path.Combine(_root, ".current.lock");
        if (File.Exists(lockPath))
        {
            MetadataPathSafety.EnsureNoReparsePoint(_root, lockPath);
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + _lockTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void ValidatePointer(AssetMetadataGenerationPointer pointer)
    {
        if (pointer.SchemaVersion != AssetMetadataGenerationPointer.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The external metadata current pointer schema is unsupported.");
        }

        ValidateRevision(pointer.Revision);
        _ = ValidateGenerationId(pointer.GenerationId);
    }

    private static void EnsureManifestMatches(
        string generationId,
        long revision,
        AssetMetadataGenerationManifest manifest)
    {
        if (manifest.Revision != revision
            || !string.Equals(manifest.GenerationId, generationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The external metadata generation identity does not match its manifest.");
        }
    }

    private static void ValidateRevision(long revision)
    {
        if (revision <= 0)
        {
            throw new InvalidDataException("An external metadata generation revision must be positive.");
        }
    }

    private static string ValidateGenerationId(string? generationId)
    {
        if (generationId is not { Length: 12 }
            || !generationId.All(character => character is >= '0' and <= '9')
            || generationId.All(character => character == '0'))
        {
            throw new InvalidDataException("An external metadata generation ID must be twelve digits and nonzero.");
        }

        return generationId;
    }

    private static string GetTreeDirectoryName(AssetMetadataTree tree) => tree switch
    {
        AssetMetadataTree.Source => "source",
        AssetMetadataTree.Library => "library",
        _ => throw new ArgumentOutOfRangeException(nameof(tree)),
    };

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine;

    private static T DeserializeStrict<T>(string json, string description) where T : class
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            ValidateNoDuplicateProperties(document.RootElement, "$", description);
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new InvalidDataException($"The {description} is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Malformed {description}: {exception.Message}", exception);
        }
    }

    private static void ValidateNoDuplicateProperties(JsonElement element, string location, string description)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"The {description} contains duplicate property '{property.Name}' at {location}.");
                }

                ValidateNoDuplicateProperties(property.Value, $"{location}.{property.Name}", description);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement child in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(child, $"{location}[{index}]", description);
                index++;
            }
        }
    }

    private sealed class ExternalAssetMetadataSnapshot(
        string mediaTreeRoot,
        string metadataTreeRoot,
        AssetMetadataSnapshotIdentity identity) : IAssetMetadataSnapshot
    {
        private readonly string _mediaRoot = MetadataPathSafety.NormalizeExistingDirectory(mediaTreeRoot, "media tree");
        private readonly string _metadataRoot = MetadataPathSafety.NormalizeExistingDirectory(
            metadataTreeRoot,
            "metadata generation tree");

        public AssetMetadataSnapshotIdentity Identity { get; } = identity;

        public IReadOnlyList<string> DiscoverRelativeMediaPaths() =>
            MetadataPathSafety.DiscoverRelativeMetadataMediaPaths(_metadataRoot);

        public bool Exists(string relativeMediaPath)
        {
            _ = MetadataPathSafety.ResolveRelativeMediaPath(_mediaRoot, relativeMediaPath);
            string metadataPath = GetExternalMetadataPath(relativeMediaPath);
            return File.Exists(metadataPath);
        }

        public AssetMetadataDocument Read(string relativeMediaPath)
        {
            _ = MetadataPathSafety.ResolveRelativeMediaPath(_mediaRoot, relativeMediaPath);
            string metadataPath = GetExternalMetadataPath(relativeMediaPath);
            MetadataPathSafety.EnsureNoReparsePoint(_metadataRoot, metadataPath);
            if (!File.Exists(metadataPath))
            {
                throw new FileNotFoundException("Programming metadata is missing from the pinned external generation.");
            }

            byte[] content = File.ReadAllBytes(metadataPath);
            return new AssetMetadataDocument(
                AssetMetadataStore.Deserialize(content, "external generation record"),
                content);
        }

        private string GetExternalMetadataPath(string relativeMediaPath)
        {
            string normalized = MetadataPathSafety.NormalizeRelativeMediaPath(relativeMediaPath);
            return MetadataPathSafety.ResolveContainedPath(
                _metadataRoot,
                normalized + AssetMetadataStore.MetadataSuffix);
        }
    }
}

internal static class MetadataPathSafety
{
    public static string NormalizeRelativeMediaPath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.StartsWith('/')
            || value.StartsWith('\\')
            || (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
            || value.Contains(':')
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException("A metadata media identity must be a safe relative path.");
        }

        string[] segments = value.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new InvalidDataException("A metadata media identity contains an invalid path segment.");
        }

        return string.Join('/', segments);
    }

    public static string ResolveRelativeMediaPath(string root, string relativeMediaPath) =>
        ResolveContainedPath(root, NormalizeRelativeMediaPath(relativeMediaPath));

    public static string ResolveContainedPath(string root, string relativePath)
    {
        string fullRoot = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(
            fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(fullRoot, candidate);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", GetPathComparison())
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", GetPathComparison()))
        {
            throw new InvalidDataException("A metadata path escapes its configured root.");
        }

        return candidate;
    }

    public static string NormalizeExistingDirectory(string path, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"The configured {description} is unavailable.");
        }

        EnsureNoReparsePoint(fullPath, fullPath);
        return fullPath;
    }

    public static void EnsureNoReparsePoint(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(path);
        _ = ResolveContainedPath(fullRoot, Path.GetRelativePath(fullRoot, fullPath));
        string relative = Path.GetRelativePath(fullRoot, fullPath);
        string current = fullRoot;
        CheckReparse(current);
        foreach (string segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            CheckReparse(current);
        }
    }

    public static IReadOnlyList<string> DiscoverRelativeMetadataMediaPaths(string root)
    {
        string fullRoot = NormalizeExistingDirectory(root, "metadata tree");
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (string path in Directory.EnumerateFiles(
            fullRoot,
            $"*{AssetMetadataStore.MetadataSuffix}",
            options))
        {
            EnsureNoReparsePoint(fullRoot, path);
            string relativeMetadata = Path.GetRelativePath(fullRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            string relativeMedia = relativeMetadata[..^AssetMetadataStore.MetadataSuffix.Length];
            string normalized = NormalizeRelativeMediaPath(relativeMedia);
            if (!results.Add(normalized))
            {
                throw new InvalidDataException("The metadata tree contains duplicate normalized media identities.");
            }
        }

        return results.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static void CheckReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A metadata path traverses a symbolic link or reparse point.");
        }
    }

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
