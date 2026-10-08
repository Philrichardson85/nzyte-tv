using System.Text.Json;
using NzyteTv.Dashboard.Status;

namespace NzyteTv.Dashboard.Tests;

public sealed class DashboardSanitizationTests
{
    [Theory]
    [InlineData("C:\\private\\station.json")]
    [InlineData("/var/lib/nzyte-tv/state.json")]
    [InlineData("\\\\server\\private\\station.json")]
    [InlineData("../private/station.json")]
    [InlineData("ffmpeg -i source.mp4 --secret=value")]
    [InlineData("--stream-key FAKE-SECRET")]
    public void Sanitizer_RejectsPathAndCommandLikeValues(string hostile)
    {
        Assert.Null(DashboardTextSanitizer.Sanitize(hostile));
    }

    [Theory]
    [InlineData("Prince - Purple Rain")]
    [InlineData("Jay-Z - 99 Problems")]
    [InlineData("AC/DC")]
    [InlineData("Earth, Wind & Fire")]
    [InlineData("Beyoncé - Déjà Vu")]
    [InlineData("André 3000")]
    [InlineData("Artist_Name")]
    [InlineData("Song (Live)")]
    [InlineData("Artist / Song")]
    public void Sanitizer_PreservesLegitimateMediaText(string title)
    {
        Assert.Equal(title, DashboardTextSanitizer.Sanitize(title));
    }

    [Fact]
    public void Sanitizer_RedactsDestinationsFiltersMarkupControlsAndLength()
    {
        const string destination = "rtmps://example.invalid/live/FAKE-SECRET";
        string? destinationResult = DashboardTextSanitizer.Sanitize(
            $"Track {destination}");
        string? markupResult = DashboardTextSanitizer.Sanitize(
            "<script>alert(1)</script>\u0000\u0007");
        string? longResult = DashboardTextSanitizer.Sanitize(new string('x', 500));

        Assert.NotNull(destinationResult);
        Assert.DoesNotContain(destination, destinationResult, StringComparison.Ordinal);
        Assert.DoesNotContain("FAKE-SECRET", destinationResult, StringComparison.Ordinal);
        Assert.Equal("scriptalert(1)/script", markupResult);
        Assert.Equal(DashboardTextSanitizer.MaximumLength, longResult!.Length);
        Assert.DoesNotContain(longResult, character => char.IsControl(character));
    }

    [Fact]
    public async Task Provider_DropsEveryUnsafeInternalFieldFromPublicJson()
    {
        using var fixture = new DashboardStateFixture();
        const string secret = "rtmp://example.invalid/live/FAKE-SECRET";
        await fixture.WriteAllAsync(
            station: fixture.CreateStation() with
            {
                Title = "/var/private/media.mp4",
                Type = "<script>music</script>",
                LastError = $"failed {secret} C:\\private\\station.json --token value",
            },
            rolling: fixture.CreateRolling() with
            {
                LastTransitionError = $"failed {secret}",
            },
            replenishment: fixture.CreateReplenishment() with
            {
                Health = NzyteTv.Core.RollingReplenishmentHealth.Degraded,
                LastErrorAtUtc = DashboardStateFixture.Now,
                ErrorClassification =
                    NzyteTv.Core.RollingReplenishmentErrorClassification.Transient,
                LastError = $"failed {secret}",
            });

        DashboardStatusSnapshot snapshot = fixture.CreateProvider(101, 202).ReadStatus();
        string json = JsonSerializer.Serialize(snapshot);

        Assert.Null(snapshot.Playback.Title);
        Assert.DoesNotContain("<", snapshot.Playback.ItemType, StringComparison.Ordinal);
        Assert.DoesNotContain("FAKE-SECRET", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/var/", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LastError", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LastTransitionError", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RecentStandardError", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DashboardStateFixture.PlannerId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(DashboardStateFixture.QueueId, json, StringComparison.Ordinal);
    }
}
