namespace NzyteTv.Core;

public static class BroadcastStandard
{
    public const int Width = 1920;
    public const int Height = 1080;
    public const double FramesPerSecond = 30d;
    public const int GopSize = 60;
    public const double KeyframeIntervalSeconds = 2d;
    public const double FrameRateTolerance = 0.01d;
    public const double KeyframeToleranceSeconds = 0.10d;
    public const int AudioSampleRate = 48_000;
    public const int AudioChannels = 2;
}
