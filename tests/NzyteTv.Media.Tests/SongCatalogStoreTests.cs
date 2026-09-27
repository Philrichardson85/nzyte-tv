using NzyteTv.Core;
using NzyteTv.Media;

namespace NzyteTv.Media.Tests;

public sealed class SongCatalogStoreTests
{
    [Fact]
    public void Load_MalformedJson_IsRejectedSafely()
    {
        using var fixture = new CatalogFixture("{ not json");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new SongCatalogStore().Load(fixture.Path));

        Assert.Contains("Malformed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_DuplicateContentGroupId_IsRejected()
    {
        using var fixture = new CatalogFixture(
            """
            {
              "schemaVersion": 1,
              "songs": [
                { "contentGroupId": "cold", "title": "Cold", "artist": "Nzyte" },
                { "contentGroupId": "cold", "title": "Cold Again", "artist": "Nzyte" }
              ]
            }
            """);

        CatalogValidationException exception = Assert.Throws<CatalogValidationException>(() =>
            new SongCatalogStore().Load(fixture.Path));

        Assert.Contains("Duplicate contentGroupId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_UnsupportedSchemaVersion_IsRejected()
    {
        using var fixture = new CatalogFixture(
            """
            {
              "schemaVersion": 2,
              "songs": []
            }
            """);

        CatalogValidationException exception = Assert.Throws<CatalogValidationException>(() =>
            new SongCatalogStore().Load(fixture.Path));

        Assert.Contains("Unsupported schemaVersion 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingRequiredSongValue_IsRejected()
    {
        using var fixture = new CatalogFixture(
            """
            {
              "schemaVersion": 1,
              "songs": [
                { "contentGroupId": "free-fallin", "artist": "Nzyte" }
              ]
            }
            """);

        CatalogValidationException exception = Assert.Throws<CatalogValidationException>(() =>
            new SongCatalogStore().Load(fixture.Path));

        Assert.Contains("title is required", exception.Message, StringComparison.Ordinal);
    }

    private sealed class CatalogFixture : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("nzytetv-catalog-").FullName;

        public CatalogFixture(string json)
        {
            Path = System.IO.Path.Combine(_directory, "songs.json");
            File.WriteAllText(Path, json);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
