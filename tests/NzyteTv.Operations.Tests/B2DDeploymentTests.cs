using System.Security.Cryptography;
using System.Text;

namespace NzyteTv.Operations.Tests;

public sealed class B2DDeploymentTests
{
    [Fact]
    public void OperationsCandidateEnablesTrustedMediaRefreshWithoutExfatWrites()
    {
        string root = FindRepositoryRoot();
        string service = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "checkpoint-3b3-b2d",
            "nzyte-tv-operations.service"));
        string[] lines = service.Split('\n', StringSplitOptions.TrimEntries);

        Assert.Contains("User=nzyte-ops", lines);
        Assert.Contains("Group=nzyte-ops", lines);
        Assert.Contains(
            "SupplementaryGroups=nzyte-programming nzyte-media-metadata",
            lines);
        Assert.Contains("Environment=Operations__MediaLibrary__Enabled=true", lines);
        Assert.Contains(
            "Environment=Operations__MediaLibrary__MediaRoot=/srv/nzyte-tv/media",
            lines);
        Assert.Contains(
            "Environment=Operations__MediaLibrary__MetadataRoot=/var/lib/nzyte-tv-media-metadata",
            lines);
        Assert.DoesNotContain(
            lines,
            line => line.StartsWith("Environment=Operations__MediaLibrary__InboxRoot=", StringComparison.Ordinal));
        Assert.Contains("ReadOnlyPaths=/srv/nzyte-tv/media", lines);
        Assert.Contains(
            "ReadWritePaths=/srv/nzyte-tv/media/catalog /var/lib/nzyte-tv-media-metadata /run/nzyte-tv-operations",
            lines);
        Assert.DoesNotContain(
            lines,
            line => line.StartsWith("ReadWritePaths=", StringComparison.Ordinal)
                && (line.Contains("/srv/nzyte-tv/media/source", StringComparison.Ordinal)
                    || line.Contains("/srv/nzyte-tv/media/library", StringComparison.Ordinal)
                    || line.Contains("/srv/nzyte-tv/media/inbox", StringComparison.Ordinal)));
    }

    [Fact]
    public void BroadcasterCandidateRetainsB1BoundariesAndAddsReadOnlyMetadataAccess()
    {
        string root = FindRepositoryRoot();
        string dropIn = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "checkpoint-3b3-b2d",
            "nzyte-tv.service.d",
            "external-metadata.conf"));
        string[] lines = dropIn.Split('\n', StringSplitOptions.TrimEntries);

        Assert.Contains("Requires=srv-nzyte\\x2dtv-media-catalog.mount", lines);
        Assert.Contains(
            "RequiresMountsFor=/var/lib/nzyte-tv /srv/nzyte-tv/media /srv/nzyte-tv/media/catalog /var/lib/nzyte-tv-media-metadata",
            lines);
        Assert.Contains(
            "SupplementaryGroups=nzyte-tv-state nzyte-media-metadata",
            lines);
        Assert.Contains("ReadOnlyPaths=/var/lib/nzyte-tv-media-metadata", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("ReadWritePaths=", StringComparison.Ordinal));
        Assert.DoesNotContain("nzyte-dashboard", dropIn, StringComparison.Ordinal);
    }

    [Fact]
    public void B2DCandidatesContainNoSecretsPrivilegesOrServiceControl()
    {
        string root = FindRepositoryRoot();
        string directory = Path.Combine(root, "deploy", "checkpoint-3b3-b2d");
        string combined = string.Join(
            "\n",
            Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        foreach (string forbidden in new[]
        {
            "EnvironmentFile=",
            "secrets.env",
            "rtmp://",
            "rtmps://",
            "sudo",
            "systemctl",
            "ExecStartPre=",
            "ExecStartPost=",
        })
        {
            Assert.DoesNotContain(forbidden, combined, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("NoNewPrivileges=yes", combined, StringComparison.Ordinal);
        Assert.Contains("CapabilityBoundingSet=", combined, StringComparison.Ordinal);
        Assert.Contains("AmbientCapabilities=", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void RollingExampleSelectsExternalMetadataExplicitly()
    {
        string root = FindRepositoryRoot();
        string example = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "config",
            "rolling-station.json.example"));

        Assert.Contains("\"assetMetadataStorage\"", example, StringComparison.Ordinal);
        Assert.Contains("\"mode\": \"externalGeneration\"", example, StringComparison.Ordinal);
        Assert.Contains(
            "\"externalRoot\": \"/var/lib/nzyte-tv-media-metadata\"",
            example,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasedB1DeploymentTemplatesRemainByteEquivalent()
    {
        string root = FindRepositoryRoot();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["nzyte-tv-operations.service"] =
                "215034017b0d33c470a2a53b15ed277cff1b587ed2d941bb66ed184113f924ec",
            ["nzyte-tv-dashboard.service"] =
                "972e7e52e0909f1b623fd7d8dad4bc3e837bf61c14a9d9d4c92f7c7ab8df3896",
            ["nzyte-tv-programming-catalog.mount.template"] =
                "9e97b3b2a3c166e2ecc788416484f16fb590ff58bc428e14c4383f822090d187",
            [Path.Combine("nzyte-tv.service.d", "programming-catalog.conf")] =
                "6001dc583cef84e42f0114928155bca005151d9858db52587b135f18b6d11c4f",
        };

        foreach ((string relativePath, string expectedHash) in expected)
        {
            string text = File.ReadAllText(Path.Combine(
                root,
                "deploy",
                "checkpoint-3b3-b1",
                relativePath)).Replace("\r\n", "\n", StringComparison.Ordinal);
            string actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            Assert.Equal(expectedHash, actual);
        }
    }

    private static string FindRepositoryRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "NzyteTv.slnx"))) return current;
            current = Directory.GetParent(current)?.FullName;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
