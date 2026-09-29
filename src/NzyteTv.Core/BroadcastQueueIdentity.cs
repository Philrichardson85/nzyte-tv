using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NzyteTv.Core;

public static class BroadcastQueueIdentity
{
    private const string IdentityVersion = "NZYTE-TV-QUEUE-ID-V1";

    public static string Create(BroadcastPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.PlaylistContentHashes.Count != 0
            && plan.PlaylistContentHashes.Count != plan.PlaylistPaths.Count)
        {
            throw new ArgumentException(
                "Playlist content hashes must be empty or match the playlist count.",
                nameof(plan));
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, IdentityVersion);
        AppendInt32(hash, plan.PlaylistPaths.Count);
        for (int index = 0; index < plan.PlaylistPaths.Count; index++)
        {
            AppendString(hash, NormalizePath(plan.PlaylistPaths[index]));
            AppendString(hash, plan.PlaylistContentHashes.Count == 0
                ? string.Empty
                : plan.PlaylistContentHashes[index]);
        }

        AppendInt32(hash, plan.Items.Count);
        foreach (BroadcastPlanItem item in plan.Items)
        {
            AppendString(hash, NormalizePath(item.PlaylistPath));
            AppendInt32(hash, item.Sequence);
            AppendString(hash, item.AssetId);
            AppendString(hash, item.RelativePath.Replace('\\', '/'));
            AppendInt64(hash, BitConverter.DoubleToInt64Bits(item.DurationSeconds));
            AppendString(hash, item.Title);
            AppendString(hash, item.Type);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
