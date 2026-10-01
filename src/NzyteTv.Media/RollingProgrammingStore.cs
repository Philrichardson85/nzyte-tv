using System.Security.Cryptography;
using System.Text;
using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record RollingProgrammingPaths(
    string MediaRoot,
    string RollingRoot,
    string ManifestPath,
    string LocksDirectory,
    string PlannerLockPath,
    string StagingDirectory,
    string BlocksDirectory,
    string HistoryDirectory,
    string OrphanedDirectory)
{
    public static RollingProgrammingPaths FromMediaRoot(string mediaRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaRoot);
        string root = Path.GetFullPath(mediaRoot);
        string rolling = Path.Combine(root, "playlists", "rolling");
        string locks = Path.Combine(rolling, ".locks");
        return new RollingProgrammingPaths(
            root,
            rolling,
            Path.Combine(rolling, "manifest.json"),
            locks,
            Path.Combine(locks, "planner.lock"),
            Path.Combine(rolling, ".staging"),
            Path.Combine(rolling, "blocks"),
            Path.Combine(rolling, "history"),
            Path.Combine(rolling, "orphaned"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RollingRoot);
        Directory.CreateDirectory(LocksDirectory);
        Directory.CreateDirectory(StagingDirectory);
        Directory.CreateDirectory(BlocksDirectory);
        Directory.CreateDirectory(HistoryDirectory);
        Directory.CreateDirectory(OrphanedDirectory);
    }
}

public interface IRollingPlannerLock : IDisposable;

public interface IRollingPlannerLockProvider
{
    IRollingPlannerLock Acquire(string path);
}

public sealed class RollingPlannerLockProvider : IRollingPlannerLockProvider
{
    public IRollingPlannerLock Acquire(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Planner lock path has no parent directory."));
        try
        {
            return new HeldRollingPlannerLock(new FileStream(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None));
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another rolling programming planner currently holds the exclusive planner lock.",
                exception);
        }
    }

    private sealed class HeldRollingPlannerLock(FileStream stream) : IRollingPlannerLock
    {
        public void Dispose() => stream.Dispose();
    }
}

public interface IRollingPlanStore
{
    RollingProgrammingManifest LoadManifest(string path);

    RollingProgrammingManifest? LoadManifestIfExists(string path);

    Task WriteManifestAsync(
        string path,
        RollingProgrammingManifest manifest,
        CancellationToken cancellationToken);

    Task WriteStagedAsync<T>(string path, T value, CancellationToken cancellationToken);

    Task WriteImmutableAsync<T>(string path, T value, CancellationToken cancellationToken);

    T Read<T>(string path, string description);

    string ReadText(string path);
}

public sealed class RollingPlanStore(IAtomicTextFileWriter? writer = null) : IRollingPlanStore
{
    private readonly IAtomicTextFileWriter _writer = writer ?? new AtomicTextFileWriter();

    public RollingProgrammingManifest LoadManifest(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Rolling programming manifest not found: {fullPath}", fullPath);
        }

        RollingProgrammingManifest manifest = Read<RollingProgrammingManifest>(
            fullPath,
            $"rolling programming manifest '{fullPath}'");
        RollingManifestValidator.Validate(manifest);
        return manifest;
    }

    public RollingProgrammingManifest? LoadManifestIfExists(string path) =>
        File.Exists(Path.GetFullPath(path)) ? LoadManifest(path) : null;

    public Task WriteManifestAsync(
        string path,
        RollingProgrammingManifest manifest,
        CancellationToken cancellationToken)
    {
        RollingManifestValidator.Validate(manifest);
        return _writer.WriteAsync(
            Path.GetFullPath(path),
            RollingProgrammingJson.Serialize(manifest),
            cancellationToken);
    }

    public Task WriteStagedAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        _writer.WriteAsync(
            Path.GetFullPath(path),
            RollingProgrammingJson.Serialize(value),
            cancellationToken);

    public async Task WriteImmutableAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        string content = RollingProgrammingJson.Serialize(value);
        if (File.Exists(fullPath))
        {
            if (!string.Equals(File.ReadAllText(fullPath), content, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Immutable rolling artifact already exists with different content: {fullPath}");
            }

            return;
        }

        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Immutable rolling artifact has no parent directory.");
        Directory.CreateDirectory(directory);
        string publicationPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.publish");
        try
        {
            await _writer.WriteAsync(publicationPath, content, cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(publicationPath, fullPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(fullPath))
            {
                if (!string.Equals(File.ReadAllText(fullPath), content, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Immutable rolling artifact was published concurrently with different content: {fullPath}");
                }
            }
        }
        finally
        {
            if (File.Exists(publicationPath))
            {
                File.Delete(publicationPath);
            }
        }
    }

    public T Read<T>(string path, string description)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"{description} not found: {fullPath}", fullPath);
        }

        return RollingProgrammingJson.Deserialize<T>(File.ReadAllText(fullPath), description);
    }

    public string ReadText(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Rolling artifact not found: {fullPath}", fullPath);
        }

        return File.ReadAllText(fullPath);
    }
}

public static class RollingManifestValidator
{
    public static void Validate(RollingProgrammingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != RollingProgrammingManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported rolling manifest schemaVersion {manifest.SchemaVersion}; " +
                $"expected {RollingProgrammingManifest.CurrentSchemaVersion}.");
        }

        if (!Guid.TryParseExact(manifest.PlannerId, "N", out _)
            || !double.IsFinite(manifest.TargetBlockDurationSeconds)
            || manifest.TargetBlockDurationSeconds <= 0
            || manifest.TargetPreparedBlockCount < 1
            || manifest.NextSequence < 1
            || manifest.GenesisHistory is null
            || manifest.HistoryHead is null
            || manifest.Blocks is null
            || manifest.InitializedAtUtc == default)
        {
            throw new InvalidDataException("Rolling programming manifest is missing or invalid.");
        }

        ValidateHistoryReference(manifest.GenesisHistory, "genesis history");
        ValidateHistoryReference(manifest.HistoryHead, "history head");
        string? parent = null;
        RollingArtifactReference expectedHistory = manifest.GenesisHistory;
        for (int index = 0; index < manifest.Blocks.Count; index++)
        {
            RollingCommittedBlock block = manifest.Blocks[index];
            long expectedSequence = index + 1L;
            if (block.Sequence != expectedSequence
                || !string.Equals(block.ParentBlockId, parent, StringComparison.Ordinal)
                || block.HistoryBefore is null
                || block.HistoryAfter is null
                || block.HistoryBefore != expectedHistory)
            {
                throw new InvalidDataException(
                    $"Rolling manifest block chain is invalid at sequence {expectedSequence}.");
            }

            ValidateBlock(block);
            parent = block.BlockId;
            expectedHistory = block.HistoryAfter;
        }

        if (manifest.NextSequence != manifest.Blocks.Count + 1L
            || manifest.HistoryHead != expectedHistory)
        {
            throw new InvalidDataException(
                "Rolling manifest nextSequence or historyHead does not match its committed block chain.");
        }
    }

    public static void ValidateBlock(RollingCommittedBlock block, bool descriptor = false)
    {
        if (block.Sequence < 1
            || !IsSha256(block.BlockId)
            || (block.ParentBlockId is not null && !IsSha256(block.ParentBlockId))
            || string.IsNullOrWhiteSpace(block.PlaylistPath)
            || !IsSha256(block.PlaylistSha256)
            || string.IsNullOrWhiteSpace(block.DescriptorPath)
            || (!descriptor && !IsSha256(block.DescriptorSha256))
            || (descriptor && block.DescriptorSha256 is not null)
            || string.IsNullOrWhiteSpace(block.InputSnapshotPath)
            || !IsSha256(block.InputSnapshotSha256)
            || !double.IsFinite(block.TargetDurationSeconds)
            || block.TargetDurationSeconds <= 0
            || !double.IsFinite(block.ActualDurationSeconds)
            || block.ActualDurationSeconds <= 0
            || block.ScheduleStartUtc == default
            || block.ScheduleEndUtc <= block.ScheduleStartUtc
            || block.ItemCount < 1
            || block.HistoryBefore is null
            || block.HistoryAfter is null
            || !IsSha256(block.CatalogSnapshotHash)
            || string.IsNullOrWhiteSpace(block.ProgrammingSnapshotHash)
            || !IsSha256(block.InventorySnapshotHash)
            || string.IsNullOrWhiteSpace(block.PlannerAlgorithmVersion)
            || block.GeneratedAtUtc == default)
        {
            throw new InvalidDataException($"Rolling block {block.Sequence} is missing or invalid.");
        }

        ValidateHistoryReference(block.HistoryBefore, $"block {block.Sequence} historyBefore");
        ValidateHistoryReference(block.HistoryAfter, $"block {block.Sequence} historyAfter");
        if (block.ProgrammingSchemaVersion is null != block.ProgrammingRevision is null)
        {
            throw new InvalidDataException(
                $"Rolling block {block.Sequence} programming schema/revision is inconsistent.");
        }

        bool legacy = block.ProgrammingSchemaVersion is null;
        if (legacy != string.Equals(
                block.ProgrammingSnapshotHash,
                RollingProgrammingPolicy.LegacyProgrammingSnapshotMarker,
                StringComparison.Ordinal)
            || (!legacy && !IsSha256(block.ProgrammingSnapshotHash)))
        {
            throw new InvalidDataException(
                $"Rolling block {block.Sequence} programming snapshot identity is inconsistent.");
        }
    }

    public static void ValidateArtifactReference(RollingArtifactReference reference, string description)
    {
        if (string.IsNullOrWhiteSpace(reference.RelativePath) || !IsSha256(reference.Sha256))
        {
            throw new InvalidDataException($"Rolling {description} reference is invalid.");
        }
    }

    public static void ValidateHistoryReference(RollingArtifactReference reference, string description)
    {
        ValidateArtifactReference(reference, description);
        string expected = $"history/{reference.Sha256}.json";
        if (!string.Equals(
            reference.RelativePath.Replace('\\', '/'),
            expected,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Rolling {description} is not an immutable content-addressed history reference.");
        }
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public static class RollingPathSafety
{
    public static string ResolveExistingFile(string rollingRoot, string relativePath) =>
        Resolve(rollingRoot, relativePath, requireFile: true);

    public static string Resolve(string rollingRoot, string relativePath, bool requireFile = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Rolling artifact path must be relative: {relativePath}");
        }

        string root = Path.GetFullPath(rollingRoot);
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        EnsureContained(root, fullPath, relativePath);
        EnsureResolvedContained(root, fullPath, relativePath);
        if (requireFile && !File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Rolling artifact is missing: {relativePath}", fullPath);
        }

        return fullPath;
    }

    public static string ToPortableRelative(string rollingRoot, string fullPath)
    {
        string root = Path.GetFullPath(rollingRoot);
        string path = Path.GetFullPath(fullPath);
        EnsureContained(root, path, fullPath);
        return Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static void EnsureResolvedContained(string root, string fullPath, string suppliedPath)
    {
        string resolvedRoot = new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? root;
        string relative = Path.GetRelativePath(root, fullPath);
        string logical = root;
        string resolved = resolvedRoot;
        foreach (string segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            logical = Path.Combine(logical, segment);
            resolved = Path.Combine(resolved, segment);
            FileSystemInfo? info = Directory.Exists(logical)
                ? new DirectoryInfo(logical)
                : File.Exists(logical)
                    ? new FileInfo(logical)
                    : null;
            FileSystemInfo? target = info?.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                resolved = target.FullName;
            }

            EnsureContained(resolvedRoot, resolved, suppliedPath);
        }
    }

    private static void EnsureContained(string root, string candidate, string suppliedPath)
    {
        string relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", GetPathComparison())
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", GetPathComparison()))
        {
            throw new InvalidDataException($"Rolling artifact path escapes the rolling root: {suppliedPath}");
        }
    }

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
