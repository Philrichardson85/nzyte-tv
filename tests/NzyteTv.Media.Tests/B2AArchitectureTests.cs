using System.Security.Cryptography;

namespace NzyteTv.Media.Tests;

public sealed class B2AArchitectureTests
{
    [Fact]
    public void ReleasedB1DeploymentTemplatesRemainFrozen()
    {
        string root = FindRepositoryRoot();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["nzyte-tv.service.d/programming-catalog.conf"] =
                "57fac5b1dc9d7f1bf78a8157f8bec294b464e997e184e423a91b744868ef6df8",
            ["nzyte-tv-dashboard.service"] =
                "efcdc2be586ebfd7b8749a9b96a5fa12a4dbf2b4e4edd5c3c302fef6c0e50714",
            ["nzyte-tv-operations.service"] =
                "7f7349f0298a22f355e11cbf7aef7570fdb330ac0e1897e22f2a5e9238e4b647",
            ["nzyte-tv-programming-catalog.mount.template"] =
                "1bbfa7ff9ad9df5fa9ff15953abe3466aeffe668e7952f2dafabf4b5f806bada",
        };
        string directory = Path.Combine(root, "deploy", "checkpoint-3b3-b1");
        string[] actualFiles = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.Keys.OrderBy(path => path, StringComparer.Ordinal), actualFiles);
        foreach ((string relativePath, string expectedHash) in expected)
        {
            string actualHash = Convert.ToHexStringLower(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar)))));
            Assert.Equal(expectedHash, actualHash);
        }
    }

    [Fact]
    public void OperationsHelperDidNotGainExternalMetadataOrMediaWriteComposition()
    {
        string root = FindRepositoryRoot();
        string source = string.Join(
            "\n",
            Directory.EnumerateFiles(
                    Path.Combine(root, "src", "NzyteTv.Operations"),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Where(path => !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));

        Assert.DoesNotContain("ExternalAssetMetadataGenerationStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateGenerationAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishCurrentAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaNormalizer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", source, StringComparison.Ordinal);
        Assert.DoesNotContain("systemctl", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ffmpeg", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void B2AStorageFoundationContainsNoProductionPathOrProcessExecutionDependency()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "NzyteTv.Media",
            "AssetMetadataRepository.cs"));

        Assert.DoesNotContain("/srv/nzyte-tv", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/var/lib/nzyte-tv", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", source, StringComparison.Ordinal);
        Assert.DoesNotContain("systemctl", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ffmpeg", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "NzyteTv.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
