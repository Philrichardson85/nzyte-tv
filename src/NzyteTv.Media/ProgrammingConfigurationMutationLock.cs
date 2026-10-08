namespace NzyteTv.Media;

public interface IProgrammingConfigurationMutationLock
{
    ValueTask<IAsyncDisposable> AcquireAsync(
        string configurationPath,
        CancellationToken cancellationToken);
}

public sealed class ProgrammingConfigurationMutationLock : IProgrammingConfigurationMutationLock
{
    public const string LockFileSuffix = ".mutation.lock";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeout;

    public ProgrammingConfigurationMutationLock(
        TimeProvider? timeProvider = null,
        TimeSpan? timeout = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string configurationPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        string lockPath = Path.GetFullPath(configurationPath) + LockFileSuffix;
        long deadline = _timeProvider.GetTimestamp() + (long)(_timeout.TotalSeconds * _timeProvider.TimestampFrequency);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileStream stream = new(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
                return new FileLockHandle(stream);
            }
            catch (IOException) when (_timeProvider.GetTimestamp() < deadline)
            {
                await Task.Delay(RetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (UnauthorizedAccessException) when (_timeProvider.GetTimestamp() < deadline)
            {
                await Task.Delay(RetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new ProgrammingConfigurationLockException();
        }
    }

    private sealed class FileLockHandle(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}

public sealed class ProgrammingConfigurationLockException : InvalidOperationException
{
    public ProgrammingConfigurationLockException()
        : base("The programming configuration mutation lock is unavailable.")
    {
    }
}

public sealed class ProgrammingConfigurationConflictException(
    long expectedRevision,
    long actualRevision) : InvalidOperationException("The programming configuration revision is stale.")
{
    public long ExpectedRevision { get; } = expectedRevision;

    public long ActualRevision { get; } = actualRevision;
}
