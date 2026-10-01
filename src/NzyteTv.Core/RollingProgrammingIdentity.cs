using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NzyteTv.Core;

public static class RollingProgrammingSeed
{
    private const string Domain = "nzyte-tv/rolling-programming/seed/v1";

    public static int Derive(string plannerId, int baseSeed, long sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plannerId);
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        using var material = new MemoryStream();
        CanonicalRollingEncoding.WriteString(material, Domain);
        CanonicalRollingEncoding.WriteString(material, plannerId);
        CanonicalRollingEncoding.WriteInt32(material, baseSeed);
        CanonicalRollingEncoding.WriteInt64(material, sequence);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(material.GetBuffer().AsSpan(0, checked((int)material.Length)), digest);
        return BinaryPrimitives.ReadInt32BigEndian(digest);
    }
}

public static class RollingBlockIdentity
{
    private const string Domain = "nzyte-tv/rolling-programming/block/v1";

    public static string Calculate(RollingBlockIdentityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.PlannerId);
        ArgumentNullException.ThrowIfNull(input.Playlist);

        using var material = new MemoryStream();
        CanonicalRollingEncoding.WriteString(material, Domain);
        CanonicalRollingEncoding.WriteInt32(material, RollingProgrammingPolicy.IdentityFormatVersion);
        CanonicalRollingEncoding.WriteString(material, input.PlannerId);
        CanonicalRollingEncoding.WriteInt64(material, input.Sequence);
        CanonicalRollingEncoding.WriteNullableString(material, input.ParentBlockId);
        CanonicalRollingEncoding.WriteInt32(material, input.Seed);
        CanonicalRollingEncoding.WriteDouble(material, input.TargetDurationSeconds);
        CanonicalRollingEncoding.WriteDateTimeOffset(material, input.Playlist.ScheduleStartUtc);
        CanonicalRollingEncoding.WriteInt32(material, input.Playlist.SchemaVersion);
        CanonicalRollingEncoding.WriteDouble(material, input.Playlist.ActualDurationSeconds);
        CanonicalRollingEncoding.WriteDouble(material, input.Playlist.OverrunSeconds);
        CanonicalRollingEncoding.WriteString(material, input.HistoryBeforeHash);
        CanonicalRollingEncoding.WriteString(material, input.HistoryAfterHash);
        CanonicalRollingEncoding.WriteString(material, input.CatalogSnapshotHash);
        CanonicalRollingEncoding.WriteString(material, input.ProgrammingSnapshotHash);
        CanonicalRollingEncoding.WriteString(material, input.InventorySnapshotHash);
        CanonicalRollingEncoding.WriteString(material, input.PlannerAlgorithmVersion);

        PlaylistItem[] items = input.Playlist.Items.OrderBy(item => item.Sequence).ToArray();
        CanonicalRollingEncoding.WriteInt32(material, items.Length);
        foreach (PlaylistItem item in items)
        {
            CanonicalRollingEncoding.WriteInt32(material, item.Sequence);
            CanonicalRollingEncoding.WriteString(material, item.AssetId);
            CanonicalRollingEncoding.WriteNullableString(material, item.ContentGroupId);
            CanonicalRollingEncoding.WriteString(material, item.Title);
            CanonicalRollingEncoding.WriteString(material, item.Type);
            CanonicalRollingEncoding.WriteNullableString(material, item.Subtype);
            CanonicalRollingEncoding.WriteString(material, NormalizePortablePath(item.RelativePath));
            CanonicalRollingEncoding.WriteDouble(material, item.DurationSeconds);
            CanonicalRollingEncoding.WriteDouble(material, item.StartOffsetSeconds);
        }

        byte[] digest = SHA256.HashData(material.GetBuffer().AsSpan(0, checked((int)material.Length)));
        return Convert.ToHexStringLower(digest);
    }

    private static string NormalizePortablePath(string value) => value.Replace('\\', '/');
}

internal static class CanonicalRollingEncoding
{
    public static void WriteString(Stream destination, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(destination, bytes.Length);
        destination.Write(bytes);
    }

    public static void WriteNullableString(Stream destination, string? value)
    {
        destination.WriteByte(value is null ? (byte)0 : (byte)1);
        if (value is not null)
        {
            WriteString(destination, value);
        }
    }

    public static void WriteInt32(Stream destination, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        destination.Write(buffer);
    }

    public static void WriteInt64(Stream destination, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        destination.Write(buffer);
    }

    public static void WriteDouble(Stream destination, double value) =>
        WriteInt64(destination, BitConverter.DoubleToInt64Bits(value));

    public static void WriteDateTimeOffset(Stream destination, DateTimeOffset value)
    {
        WriteInt64(destination, value.UtcTicks);
        WriteInt32(destination, 0);
    }
}
