namespace NzyteTv.Core;

public static class MediaPathPolicy
{
    public static string GetDefaultOutputPath(string inputPath, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        string fileName = Path.GetFileNameWithoutExtension(inputPath) + ".mp4";
        return Path.GetFullPath(Path.Combine(workingDirectory, "BroadcastReady", fileName));
    }

    public static void EnsureOutputIsAllowed(string inputPath, string outputPath, bool overwrite)
    {
        string source = Path.GetFullPath(inputPath);
        string destination = Path.GetFullPath(outputPath);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(source, destination, comparison))
        {
            throw new InvalidOperationException("The output path resolves to the source file. Source media is never overwritten.");
        }

        if (File.Exists(destination) && !overwrite)
        {
            throw new IOException($"Output already exists: {destination}. Use --overwrite to replace it.");
        }
    }
}
