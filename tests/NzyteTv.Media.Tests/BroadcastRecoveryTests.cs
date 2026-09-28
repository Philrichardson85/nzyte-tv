namespace NzyteTv.Media.Tests;

public sealed class BroadcastRecoveryTests
{
    [Theory]
    [InlineData("out_time_us=1234000", 1.234)]
    [InlineData("out_time=00:00:02.500000", 2.5)]
    public void ProgressParser_ReadsMachineReadableTimes(string line, double expectedSeconds)
    {
        Assert.True(FfmpegProgressParser.TryParseOutputTime(line, out TimeSpan value));
        Assert.Equal(expectedSeconds, value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("Connection reset by peer")]
    [InlineData("Error closing file: Broken pipe")]
    [InlineData("Connection timed out")]
    public void FailureClassifier_RecognizesTransientOutputFailures(string diagnostic)
    {
        Assert.Equal(BroadcastFailureKind.Transient, BroadcastFailureClassifier.Classify(152, diagnostic, false));
    }

    [Fact]
    public void FailureClassifier_NeverRetriesCancellation()
    {
        Assert.Equal(BroadcastFailureKind.Cancelled, BroadcastFailureClassifier.Classify(152, "Broken pipe", true));
    }
}
