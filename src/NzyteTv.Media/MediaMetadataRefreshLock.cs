namespace NzyteTv.Media;

public interface IMediaMetadataRefreshLock
{
    ValueTask<IAsyncDisposable> TryAcquireAsync(
        string externalMetadataRoot,
        CancellationToken cancellationToken);
}

public sealed class MediaMetadataRefreshLock : IMediaMetadataRefreshLock
{
    public const string LockFileName = "media-metadata-refresh.lock";

    public ValueTask<IAsyncDisposable> TryAcquireAsync(
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
            return ValueTask.FromResult<IAsyncDisposable>(new Handle(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new MediaMetadataRefreshBusyException();
        }
    }

    private sealed class Handle(FileStream stream) : IAsyncDisposable
    {
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
