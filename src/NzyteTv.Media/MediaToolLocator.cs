using System.Diagnostics;

namespace NzyteTv.Media;

public interface IMediaToolLocator
{
    Task<MediaToolPaths> LocateAsync(CancellationToken cancellationToken);
}

public sealed class MediaToolLocator : IMediaToolLocator
{
    public async Task<MediaToolPaths> LocateAsync(CancellationToken cancellationToken)
    {
        string? ffmpeg = FindOnPath("ffmpeg");
        string? ffprobe = FindOnPath("ffprobe");

        if (ffmpeg is null || ffprobe is null)
        {
            string missing = string.Join(" and ", new[]
            {
                ffmpeg is null ? "ffmpeg" : null,
                ffprobe is null ? "ffprobe" : null,
            }.Where(value => value is not null));

            throw new MediaToolNotFoundException(
                $"Required media tool(s) not found on PATH: {missing}. Install FFmpeg (which includes FFprobe), " +
                "then verify with 'ffmpeg -version' and 'ffprobe -version'.");
        }

        await VerifyStartsAsync(ffmpeg, cancellationToken).ConfigureAwait(false);
        await VerifyStartsAsync(ffprobe, cancellationToken).ConfigureAwait(false);
        return new MediaToolPaths(ffmpeg, ffprobe);
    }

    public static string? FindOnPath(string executableName)
    {
        string fileName = OperatingSystem.IsWindows() ? executableName + ".exe" : executableName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), fileName));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ignore malformed PATH entries and continue searching.
            }
        }

        return null;
    }

    private static async Task VerifyStartsAsync(string executable, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-version");

        try
        {
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new MediaToolNotFoundException($"Unable to run required media tool: {executable}", exception);
        }

        if (process.ExitCode != 0)
        {
            throw new MediaToolNotFoundException($"Required media tool returned exit code {process.ExitCode}: {executable}");
        }
    }
}

public sealed class MediaToolNotFoundException : Exception
{
    public MediaToolNotFoundException(string message)
        : base(message)
    {
    }

    public MediaToolNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
