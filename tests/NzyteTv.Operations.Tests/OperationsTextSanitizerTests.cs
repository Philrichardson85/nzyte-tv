using NzyteTv.Operations.Spotlight;

namespace NzyteTv.Operations.Tests;

public sealed class OperationsTextSanitizerTests
{
    [Theory]
    [InlineData("Prince - Purple Rain")]
    [InlineData("Jay-Z - 99 Problems")]
    [InlineData("AC/DC")]
    [InlineData("Earth, Wind & Fire")]
    [InlineData("Beyoncé - Déjà Vu")]
    [InlineData("André 3000")]
    public void LegitimateCatalogTextIsPreserved(string value) =>
        Assert.Equal(value, OperationsTextSanitizer.Required(value));

    [Theory]
    [InlineData("rtmp://example.invalid/live/key")]
    [InlineData("rtmps://example.invalid/live/key")]
    [InlineData("/etc/nzyte-tv/secrets.env")]
    [InlineData("C:\\private\\secret.txt")]
    public void DestinationAndPathLikeCatalogTextIsRejected(string value) =>
        Assert.Throws<OperationsValidationException>(() => OperationsTextSanitizer.Required(value));

    [Fact]
    public void ControlCharactersAreRemoved() =>
        Assert.Equal("PurpleRain", OperationsTextSanitizer.Required("Purple\0Rain"));
}
