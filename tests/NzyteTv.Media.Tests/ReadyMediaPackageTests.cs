using System.Text;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class ReadyMediaPackageTests
{
    [Fact]
    public void ValidManifest_RoundTripsAndHasCanonicalDigest()
    {
        ReadyMediaPackageManifest manifest = Valid();
        string json = ReadyMediaPackageSerializer.Serialize(manifest);
        ReadyMediaPackageManifest parsed = ReadyMediaPackageSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

        Assert.Equal(manifest, parsed);
        Assert.Equal(
            ReadyMediaPackageSerializer.CalculateDigest(manifest),
            ReadyMediaPackageSerializer.CalculateDigest(parsed));
    }

    [Fact]
    public void UnknownProperty_IsRejected()
    {
        string json = ReadyMediaPackageSerializer.Serialize(Valid()).TrimEnd();
        json = json[..^1] + ",\"unexpected\":true}";

        Assert.Throws<InvalidDataException>(() =>
            ReadyMediaPackageSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void DuplicateProperty_IsRejected()
    {
        string json = ReadyMediaPackageSerializer.Serialize(Valid()).TrimEnd();
        json = json[..^1] + ",\"packageId\":\"00000000000000000000000000000001\"}";

        Assert.Throws<InvalidDataException>(() =>
            ReadyMediaPackageSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void OversizedManifest_IsRejectedBeforeParsing()
    {
        byte[] content = new byte[ReadyMediaPackageManifest.MaximumManifestBytes + 1];

        Assert.Throws<InvalidDataException>(() => ReadyMediaPackageSerializer.Deserialize(content));
    }

    [Theory]
    [InlineData("../Music Videos/Test Song.mov")]
    [InlineData("Music Videos/../Test Song.mov")]
    [InlineData("Music Videos//Test Song.mov")]
    [InlineData("Music Videos/./Test Song.mov")]
    [InlineData("Music Videos\\Test Song.mov")]
    [InlineData("/srv/media/Test Song.mov")]
    [InlineData("C:\\Media\\Test Song.mov")]
    [InlineData("\\\\server\\share\\Test Song.mov")]
    public void UnsafeSourcePath_IsRejected(string path)
    {
        ReadyMediaPackageManifest manifest = Valid() with { SourceRelativePath = path };

        Assert.Throws<InvalidDataException>(() => ReadyMediaPackageValidator.Validate(manifest));
    }

    [Fact]
    public void UnsupportedSchemaPackageIdHashAndLength_AreRejected()
    {
        Assert.Throws<InvalidDataException>(() => ReadyMediaPackageValidator.Validate(
            Valid() with { SchemaVersion = 2 }));
        Assert.Throws<InvalidDataException>(() => ReadyMediaPackageValidator.Validate(
            Valid() with { PackageId = "not-a-package" }));
        Assert.Throws<InvalidDataException>(() => ReadyMediaPackageValidator.Validate(
            Valid() with { Source = new ReadyPackageMember { Length = 1, Sha256 = "bad" } }));
        Assert.Throws<InvalidDataException>(() => ReadyMediaPackageValidator.Validate(
            Valid() with { Library = new ReadyPackageMember { Length = 0, Sha256 = Hash('b') } }));
    }

    [Fact]
    public void SourceLibraryMismatchAndInvalidExtensions_AreRejected()
    {
        Assert.Throws<InvalidDataException>(() => ReadyMediaPackageValidator.Validate(
            Valid() with { LibraryRelativePath = "Music Videos/Other.mp4" }));
        Assert.Throws<InvalidDataException>(() => ReadyMediaPackageValidator.Validate(
            Valid() with { SourceRelativePath = "Music Videos/Test Song.txt" }));
        Assert.Throws<InvalidDataException>(() => ReadyMediaPackageValidator.Validate(
            Valid() with { LibraryRelativePath = "Music Videos/Test Song.mkv" }));
    }

    [Fact]
    public void OptionalDeclarationsMustBeInternallyConsistent()
    {
        Assert.Throws<InvalidDataException>(() => ReadyMediaPackageValidator.Validate(Valid() with
        {
            SourceProgrammingMetadata = new ReadyPackageOptionalMember
            {
                Present = false,
                Length = 10,
                Sha256 = Hash('d'),
            },
        }));
        Assert.Throws<InvalidDataException>(() => ReadyMediaPackageValidator.Validate(Valid() with
        {
            SourceProgrammingMetadata = new ReadyPackageOptionalMember { Present = true },
        }));
    }

    [Fact]
    public void ReadyFileName_MustExactlyMatchCanonicalPackageId()
    {
        const string id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        ReadyMediaPackageValidator.ValidateFileName(id + ".ready.json", id);

        Assert.Throws<InvalidDataException>(() =>
            ReadyMediaPackageValidator.ValidateFileName("other.ready.json", id));
        Assert.Throws<InvalidDataException>(() =>
            ReadyMediaPackageValidator.ValidateFileName(id.ToUpperInvariant() + ".ready.json", id));
    }

    internal static ReadyMediaPackageManifest Valid() => new()
    {
        PackageId = "00000000000000000000000000000001",
        PackageCreatedAt = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
        SourceRelativePath = "Music Videos/Test Song.mov",
        LibraryRelativePath = "Music Videos/Test Song.mp4",
        Source = new ReadyPackageMember { Length = 10, Sha256 = Hash('a') },
        Library = new ReadyPackageMember { Length = 20, Sha256 = Hash('b') },
        TechnicalManifest = new ReadyPackageMember { Length = 30, Sha256 = Hash('c') },
        SourceProgrammingMetadata = new ReadyPackageOptionalMember { Present = false },
        LibraryProgrammingMetadata = new ReadyPackageOptionalMember { Present = false },
    };

    private static string Hash(char value) => new(value, 64);
}
