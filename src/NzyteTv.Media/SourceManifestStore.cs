using System.Text;
using System.Text.Json;
using NzyteTv.Core;

namespace NzyteTv.Media;

public sealed record SourceFingerprint(
    int SchemaVersion,
    string SourceRelativePath,
    long SourceSize,
    DateTime SourceLastModifiedUtc,
    string BroadcastProfileVersion,
    string? VerticalLayout = null);

public enum ManifestMatchStatus
{
    Match,
    Missing,
    Mismatch,
    Corrupt,
}

public sealed record ManifestMatchResult(ManifestMatchStatus Status, string Detail)
{
    public bool IsMatch => Status == ManifestMatchStatus.Match;
}

public interface ISourceManifestStore
{
    SourceFingerprint CreateFingerprint(
        string sourceRoot,
        string sourcePath,
        NormalizationOptions? options = null);

    VerticalLayoutMode ReadVerticalLayout(string destinationPath);

    ManifestMatchResult Evaluate(string destinationPath, SourceFingerprint expected);

    Task WriteAsync(string destinationPath, SourceFingerprint fingerprint, CancellationToken cancellationToken);
}

public sealed class SourceManifestStore : ISourceManifestStore
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestSuffix = ".nzytetv.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _broadcastProfileVersion;

    public SourceManifestStore(string broadcastProfileVersion = BroadcastStandard.ProfileVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broadcastProfileVersion);
        _broadcastProfileVersion = broadcastProfileVersion;
    }

    public SourceFingerprint CreateFingerprint(
        string sourceRoot,
        string sourcePath,
        NormalizationOptions? options = null)
    {
        string root = Path.GetFullPath(sourceRoot);
        string source = Path.GetFullPath(sourcePath);
        string relativePath = Path.GetRelativePath(root, source);
        if (Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", GetPathComparison())
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", GetPathComparison()))
        {
            throw new InvalidOperationException($"Source file is outside the source root: {source}");
        }

        var file = new FileInfo(source);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"Source media file not found: {source}", source);
        }

        return new SourceFingerprint(
            CurrentSchemaVersion,
            relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/'),
            file.Length,
            file.LastWriteTimeUtc,
            _broadcastProfileVersion,
            (options ?? NormalizationOptions.Default).VerticalLayoutManifestValue);
    }

    public ManifestMatchResult Evaluate(string destinationPath, SourceFingerprint expected)
    {
        string manifestPath = GetManifestPath(destinationPath);
        if (!File.Exists(manifestPath))
        {
            return new ManifestMatchResult(ManifestMatchStatus.Missing, "Source manifest is missing.");
        }

        try
        {
            string json = File.ReadAllText(manifestPath);
            SourceFingerprint? actual = JsonSerializer.Deserialize<SourceFingerprint>(json, SerializerOptions);
            if (actual is null)
            {
                return new ManifestMatchResult(ManifestMatchStatus.Corrupt, "Source manifest is empty or invalid.");
            }

            if (actual.SchemaVersion != CurrentSchemaVersion)
            {
                return new ManifestMatchResult(
                    ManifestMatchStatus.Mismatch,
                    $"Manifest schema changed ({actual.SchemaVersion} -> {CurrentSchemaVersion}).");
            }

            if (!string.Equals(
                    actual.BroadcastProfileVersion,
                    expected.BroadcastProfileVersion,
                    StringComparison.Ordinal))
            {
                return new ManifestMatchResult(
                    ManifestMatchStatus.Mismatch,
                    $"Broadcast profile changed ({actual.BroadcastProfileVersion} -> {expected.BroadcastProfileVersion}).");
            }

            string actualVerticalLayout = NormalizeVerticalLayout(actual.VerticalLayout);
            string expectedVerticalLayout = NormalizeVerticalLayout(expected.VerticalLayout);
            if (!string.Equals(actualVerticalLayout, expectedVerticalLayout, StringComparison.Ordinal))
            {
                return new ManifestMatchResult(
                    ManifestMatchStatus.Mismatch,
                    $"Vertical layout changed ({actualVerticalLayout} -> {expectedVerticalLayout}).");
            }

            if (!string.Equals(actual.SourceRelativePath, expected.SourceRelativePath, StringComparison.Ordinal)
                || actual.SourceSize != expected.SourceSize
                || actual.SourceLastModifiedUtc != expected.SourceLastModifiedUtc)
            {
                return new ManifestMatchResult(ManifestMatchStatus.Mismatch, "Source fingerprint changed.");
            }

            return new ManifestMatchResult(ManifestMatchStatus.Match, "Source fingerprint matches.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ManifestMatchResult(
                ManifestMatchStatus.Corrupt,
                $"Source manifest could not be read: {exception.Message}");
        }
    }

    public VerticalLayoutMode ReadVerticalLayout(string destinationPath)
    {
        string manifestPath = GetManifestPath(destinationPath);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Source manifest is missing.", manifestPath);
        }

        SourceFingerprint manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SourceFingerprint>(
                File.ReadAllText(manifestPath),
                SerializerOptions) ?? throw new InvalidDataException(
                    "Source manifest is empty or invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Source manifest is malformed.", exception);
        }

        return manifest.VerticalLayout switch
        {
            null or "" => VerticalLayoutMode.None,
            string value when string.IsNullOrWhiteSpace(value) => VerticalLayoutMode.None,
            "none" => VerticalLayoutMode.None,
            "blurred-background" => VerticalLayoutMode.BlurredBackground,
            _ => throw new InvalidDataException(
                "Source manifest contains an unsupported vertical layout."),
        };
    }

    public async Task WriteAsync(
        string destinationPath,
        SourceFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        string manifestPath = GetManifestPath(destinationPath);
        string directory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException("The source manifest path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(manifestPath)}.{Guid.NewGuid():N}.partial");

        try
        {
            string json = JsonSerializer.Serialize(fingerprint, SerializerOptions);
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static string GetManifestPath(string destinationPath) =>
        Path.GetFullPath(destinationPath) + ManifestSuffix;

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static string NormalizeVerticalLayout(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "none" : value;
}
