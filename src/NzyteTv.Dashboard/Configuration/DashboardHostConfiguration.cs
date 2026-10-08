namespace NzyteTv.Dashboard.Configuration;

internal static class DashboardHostConfiguration
{
    private static readonly string[] AlternativeListenerKeys =
    [
        "urls",
        "http_ports",
        "https_ports",
    ];

    public static void RejectAlternativeListeners(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (string key in AlternativeListenerKeys)
        {
            if (!string.IsNullOrWhiteSpace(configuration[key]))
            {
                ThrowUnsupported(key);
            }
        }

        IConfigurationSection endpoints = configuration.GetSection("Kestrel:Endpoints");
        if (!string.IsNullOrWhiteSpace(endpoints.Value) || endpoints.GetChildren().Any())
        {
            ThrowUnsupported("Kestrel:Endpoints");
        }
    }

    private static void ThrowUnsupported(string key) => throw new InvalidOperationException(
        $"Dashboard binding accepts only Dashboard:Port; remove unsupported listener configuration '{key}'.");
}
