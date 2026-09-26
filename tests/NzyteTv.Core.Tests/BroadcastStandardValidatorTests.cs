using NzyteTv.Core;

namespace NzyteTv.Core.Tests;

public sealed class BroadcastStandardValidatorTests
{
    [Fact]
    public void Validate_CompliantMedia_IsBroadcastReady()
    {
        VerificationResult result = new BroadcastStandardValidator().Validate(CreateCompliant());

        Assert.True(result.IsBroadcastReady);
        Assert.All(result.Checks, check => Assert.True(check.Passed, check.Label));
    }

    [Fact]
    public void Validate_MissingAudio_FailsAudioChecks()
    {
        MediaDescription media = CreateCompliant() with { Audio = null };

        VerificationResult result = new BroadcastStandardValidator().Validate(media);

        Assert.False(result.IsBroadcastReady);
        Assert.Contains(result.Checks, check => check.Section == "Audio" && !check.Passed);
    }

    [Fact]
    public void Validate_WrongVideoProperties_FailsRelevantChecks()
    {
        MediaDescription media = CreateCompliant() with
        {
            Video = new VideoDescription("hevc", "Main", 1280, 720, "yuv444p", "24000/1001", null),
        };

        VerificationResult result = new BroadcastStandardValidator().Validate(media);

        Assert.False(result.IsBroadcastReady);
        Assert.Contains(result.Checks, check => check.Label == "H.264 High" && !check.Passed);
        Assert.Contains(result.Checks, check => check.Label == "30 fps" && !check.Passed);
    }

    [Theory]
    [MemberData(nameof(KeyframeCases))]
    public void ValidateKeyframes_EnforcesIntervals(double[] timestamps, double duration, bool expected)
    {
        (bool passed, _) = BroadcastStandardValidator.ValidateKeyframes(timestamps, duration);

        Assert.Equal(expected, passed);
    }

    public static TheoryData<double[], double, bool> KeyframeCases => new()
    {
        { [0, 2, 4, 6], 7, true },
        { [0.02, 2.01, 4.03], 5, true },
        { [0, 2, 4.25], 5, false },
        { [0, 4], 5, false },
        { [0], 1.5, true },
        { [0], 5, false },
        { [], 0, false },
    };

    private static MediaDescription CreateCompliant() => new(
        "broadcast.mp4",
        TimeSpan.FromSeconds(7),
        "mov,mp4,m4a,3gp,3g2,mj2",
        10_000,
        new VideoDescription("h264", "High", 1920, 1080, "yuv420p", "30/1", 6_000_000),
        new AudioDescription("aac", 48_000, 2, 192_000),
        [0, 2, 4, 6]);
}
