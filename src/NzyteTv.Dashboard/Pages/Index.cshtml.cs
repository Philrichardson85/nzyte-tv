using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NzyteTv.Dashboard.Configuration;
using NzyteTv.Dashboard.Status;

namespace NzyteTv.Dashboard.Pages;

public sealed class IndexModel(
    IDashboardStatusProvider statusProvider,
    DashboardOptions options,
    IAntiforgery antiforgery) : PageModel
{
    public DashboardStatusSnapshot Snapshot { get; private set; } = null!;

    public int RefreshIntervalMilliseconds { get; } =
        checked(options.BrowserRefreshSeconds * 1000);

    public string AntiforgeryToken { get; private set; } = string.Empty;

    public void OnGet()
    {
        Snapshot = statusProvider.GetStatus();
        AntiforgeryToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken
            ?? throw new InvalidOperationException("An antiforgery request token was not generated.");
    }

    public static string Display(object? value) => value?.ToString() ?? "Unavailable";

    public static string DisplayNumber(long? value) => value?.ToString() ?? "Unavailable";

    public static string DisplayAge(long? seconds) => seconds is null
        ? "Unavailable"
        : $"{seconds} seconds";

    public static string DisplayTimestamp(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("u") ?? "Unavailable";

    public static string DisplayDuration(long? seconds)
    {
        if (seconds is null)
        {
            return "Unavailable";
        }

        TimeSpan duration = TimeSpan.FromSeconds(seconds.Value);
        return duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}"
            : duration.ToString("hh\\:mm\\:ss");
    }

    public static string DisplayQueuePosition(DashboardPlaybackInfo playback) =>
        playback.CurrentItemNumber is int current && playback.TotalItemCount is int total
            ? $"{current} of {total}"
            : "Unavailable";
}
