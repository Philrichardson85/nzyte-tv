using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NzyteTv.Media;

public sealed record ReadyPackageMember
{
    [JsonRequired]
    public long Length { get; init; }

    public string? Sha256 { get; init; }
}

public sealed record ReadyPackageOptionalMember
{
    [JsonRequired]
    public bool Present { get; init; }

    public long? Length { get; init; }

    public string? Sha256 { get; init; }
}

public sealed record ReadyMediaPackageManifest
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumManifestBytes = 64 * 1024;
    public const int MaximumRelativePathLength = 512;
    public const string FileSuffix = ".ready.json";

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? PackageId { get; init; }

    [JsonRequired]
    public DateTimeOffset PackageCreatedAt { get; init; }

    public string? SourceRelativePath { get; init; }

    public string? LibraryRelativePath { get; init; }

    public ReadyPackageMember? Source { get; init; }

    public ReadyPackageMember? Library { get; init; }

    public ReadyPackageMember? TechnicalManifest { get; init; }

    public ReadyPackageOptionalMember? SourceProgrammingMetadata { get; init; }

    public ReadyPackageOptionalMember? LibraryProgrammingMetadata { get; init; }
}

public static class ReadyMediaPackageSerializer
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static ReadyMediaPackageManifest Deserialize(ReadOnlySpan<byte> content)
    {
        if (content.Length is 0 or > ReadyMediaPackageManifest.MaximumManifestBytes)
        {
            throw new InvalidDataException("The READY manifest size is invalid.");
        }

        ReadyMediaPackageManifest manifest = StrictJson.Deserialize<ReadyMediaPackageManifest>(
            content,
            ReadOptions,
            "READY package manifest");
        ReadyMediaPackageValidator.Validate(manifest);
        return manifest;
    }

    public static string Serialize(ReadyMediaPackageManifest manifest)
    {
        ReadyMediaPackageValidator.Validate(manifest);
        return JsonSerializer.Serialize(manifest, WriteOptions) + Environment.NewLine;
    }

    public static string CalculateDigest(ReadyMediaPackageManifest manifest)
    {
        ReadyMediaPackageValidator.Validate(manifest);
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(manifest, CanonicalOptions);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }
}

public static class ReadyMediaPackageValidator
{
    private static readonly HashSet<string> SupportedSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".mkv",
    };

    public static void Validate(ReadyMediaPackageManifest? manifest)
    {
        if (manifest is null
            || manifest.SchemaVersion != ReadyMediaPackageManifest.CurrentSchemaVersion
            || !IsPackageId(manifest.PackageId)
            || manifest.PackageCreatedAt == default)
        {
            throw new InvalidDataException("The READY package identity or schema is invalid.");
        }

        string source = ValidateRelativePath(manifest.SourceRelativePath, "source");
        string library = ValidateRelativePath(manifest.LibraryRelativePath, "library");
        if (!SupportedSourceExtensions.Contains(Path.GetExtension(source)))
        {
            throw new InvalidDataException("The READY source extension is unsupported.");
        }

        if (!string.Equals(Path.GetExtension(library), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The READY library member must be an MP4.");
        }

        string expectedLibrary = Path.ChangeExtension(source, ".mp4").Replace('\\', '/');
        if (!string.Equals(expectedLibrary, library, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The READY source and library paths are inconsistent.");
        }

        ValidateRequiredMember(manifest.Source, "source");
        ValidateRequiredMember(manifest.Library, "library");
        ValidateRequiredMember(manifest.TechnicalManifest, "technical manifest");
        ValidateOptionalMember(manifest.SourceProgrammingMetadata, "source programming metadata");
        ValidateOptionalMember(manifest.LibraryProgrammingMetadata, "library programming metadata");
    }

    public static void ValidateFileName(string fileName, string packageId)
    {
        if (!IsPackageId(packageId)
            || !string.Equals(fileName, packageId + ReadyMediaPackageManifest.FileSuffix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The READY manifest filename is invalid.");
        }
    }

    public static bool IsPackageId(string? value) => value is { Length: 32 }
        && Guid.TryParseExact(value, "N", out Guid parsed)
        && string.Equals(parsed.ToString("N"), value, StringComparison.Ordinal);

    public static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ValidateRelativePath(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > ReadyMediaPackageManifest.MaximumRelativePathLength)
        {
            throw new InvalidDataException($"The READY {description} path is invalid.");
        }

        string normalized = MetadataPathSafety.NormalizeRelativeMediaPath(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The READY {description} path must use canonical forward-slash separators.");
        }

        return normalized;
    }

    private static void ValidateRequiredMember(ReadyPackageMember? member, string description)
    {
        if (member is null || member.Length <= 0 || !IsSha256(member.Sha256))
        {
            throw new InvalidDataException($"The READY {description} declaration is invalid.");
        }
    }

    private static void ValidateOptionalMember(ReadyPackageOptionalMember? member, string description)
    {
        if (member is null
            || (member.Present && (member.Length is null or <= 0 || !IsSha256(member.Sha256)))
            || (!member.Present && (member.Length is not null || member.Sha256 is not null)))
        {
            throw new InvalidDataException($"The READY {description} declaration is invalid.");
        }
    }
}

internal static class MediaPackageHash
{
    public static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public static string Sha256(string value) => Sha256(Encoding.UTF8.GetBytes(value));
}
