namespace NzyteTv.Media;

public interface IMediaMetadataRefreshLock
{
    ValueTask<IMediaMetadataRefreshLease> TryAcquireAsync(
        string externalMetadataRoot,
        CancellationToken cancellationToken);
}

public interface IMediaMetadataRefreshLease : IAsyncDisposable
{
    string MetadataRoot { get; }

    void Consume(string expectedMetadataRoot);
}

public sealed class MediaMetadataRefreshLock : IMediaMetadataRefreshLock
{
    public const string LockFileName = "media-metadata-refresh.lock";

    public ValueTask<IMediaMetadataRefreshLease> TryAcquireAsync(
        string externalMetadataRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(externalMetadataRoot);
        if (!Path.IsPathFullyQualified(externalMetadataRoot))
        {
            throw new ArgumentException("The external metadata root must be absolute.", nameof(externalMetadataRoot));
        }

        string root = Path.GetFullPath(externalMetadataRoot);
        Directory.CreateDirectory(root);
        MetadataPathSafety.EnsureNoReparsePoint(root, root);
        string lockPath = Path.Combine(root, LockFileName);
        try
        {
            FileStream stream = new(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous);
            return ValueTask.FromResult<IMediaMetadataRefreshLease>(new Handle(stream, root));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new MediaMetadataRefreshBusyException();
        }
    }

    private sealed class Handle(FileStream stream, string metadataRoot) : IMediaMetadataRefreshLease
    {
        private int _consumed;

        public string MetadataRoot { get; } = metadataRoot;

        public void Consume(string expectedMetadataRoot)
        {
            if (!string.Equals(
                    MetadataRoot,
                    Path.GetFullPath(expectedMetadataRoot),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || Interlocked.Exchange(ref _consumed, 1) != 0)
            {
                throw new InvalidOperationException("The media metadata refresh lease is invalid or already consumed.");
            }
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}

public sealed class MediaMetadataRefreshBusyException : InvalidOperationException
{
    public MediaMetadataRefreshBusyException()
        : base("A media metadata refresh or bootstrap operation is already active.")
    {
    }
}
