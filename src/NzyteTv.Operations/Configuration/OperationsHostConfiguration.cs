namespace NzyteTv.Operations.Configuration;

internal static class OperationsHostConfiguration
{
    private static readonly string[] AlternativeListenerKeys = ["urls", "http_ports", "https_ports"];

    public static void RejectTcpListeners(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (string key in AlternativeListenerKeys)
        {
            if (!string.IsNullOrWhiteSpace(configuration[key]))
            {
                throw new InvalidOperationException(
                    $"Operations helper accepts only its Unix socket; remove listener configuration '{key}'.");
            }
        }
        IConfigurationSection endpoints = configuration.GetSection("Kestrel:Endpoints");
        if (!string.IsNullOrWhiteSpace(endpoints.Value) || endpoints.GetChildren().Any())
        {
            throw new InvalidOperationException(
                "Operations helper accepts only its Unix socket; remove listener configuration 'Kestrel:Endpoints'.");
        }
    }
}
