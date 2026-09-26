using NzyteTv.Core;

namespace NzyteTv.Core.Tests;

public sealed class RationalNumberTests
{
    [Theory]
    [InlineData("30/1", 30)]
    [InlineData("30000/1001", 29.97002997)]
    [InlineData(" 60 / 2 ", 30)]
    [InlineData("30", 30)]
    public void TryParse_ValidValue_ReturnsExpectedRate(string value, double expected)
    {
        bool parsed = RationalNumber.TryParse(value, out double actual);

        Assert.True(parsed);
        Assert.Equal(expected, actual, precision: 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0/0")]
    [InlineData("abc")]
    public void TryParse_InvalidValue_ReturnsFalse(string? value)
    {
        Assert.False(RationalNumber.TryParse(value, out _));
    }
}
