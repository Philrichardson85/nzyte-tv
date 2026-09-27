using System.Text.Json.Serialization;

namespace NzyteTv.Core;

public sealed class MediaRootDescriptor
{
    public const int CurrentSchemaVersion = 1;

    [JsonRequired]
    public int SchemaVersion { get; init; }
}
