using System.Diagnostics;
using System.Text;

namespace NzyteTv.Media;

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    Action<string>? OnStandardOutput = null,
    Action<string>? OnStandardError = null,
    Action<int>? OnStarted = null,
    Action<int>? OnExited = null);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = request.FileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };

        foreach (string argument in request.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                stdoutClosed.TrySetResult();
                return;
            }

            lock (stdout)
            {
                stdout.AppendLine(eventArgs.Data);
            }

            request.OnStandardOutput?.Invoke(eventArgs.Data);
        };

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                stderrClosed.TrySetResult();
                return;
            }

            lock (stderr)
            {
                stderr.AppendLine(eventArgs.Data);
            }

            request.OnStandardError?.Invoke(eventArgs.Data);
        };

        bool started = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Unable to start process: {request.FileName}");
            }

            started = true;
            request.OnStarted?.Invoke(process.Id);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception exception) when (exception is
                    InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The process exited between the HasExited check and Kill.
                }
            });

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutClosed.Task, stderrClosed.Task).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process exited or was never fully associated while cancellation raced startup.
            }

            throw;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new ProcessExecutionException($"Unable to launch '{request.FileName}'.", exception);
        }
        finally
        {
            if (started)
            {
                request.OnExited?.Invoke(process.Id);
            }
        }
    }
}

public sealed class ProcessExecutionException : Exception
{
    public ProcessExecutionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
