namespace NzyteTv.Core;

public enum VerticalLayoutMode
{
    None,
    BlurredBackground,
}

public sealed record NormalizationOptions
{
    public static NormalizationOptions Default { get; } = new();

    public VerticalLayoutMode VerticalLayout { get; init; }

    public string VerticalLayoutManifestValue => VerticalLayout switch
    {
        VerticalLayoutMode.None => "none",
        VerticalLayoutMode.BlurredBackground => "blurred-background",
        _ => throw new InvalidOperationException($"Unsupported vertical layout mode: {VerticalLayout}"),
    };
}
