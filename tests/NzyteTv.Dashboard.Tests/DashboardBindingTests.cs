using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using NzyteTv.Dashboard.Configuration;

namespace NzyteTv.Dashboard.Tests;

public sealed class DashboardBindingTests
{
    private static readonly string[] ListenerEnvironmentKeys =
    [
        "ASPNETCORE_URLS",
        "DOTNET_URLS",
        "ASPNETCORE_HTTP_PORTS",
        "DOTNET_HTTP_PORTS",
        "ASPNETCORE_HTTPS_PORTS",
        "DOTNET_HTTPS_PORTS",
        "Kestrel__Endpoints__Remote__Url",
    ];

    [Theory]
    [InlineData("aspnetcore-urls")]
    [InlineData("dotnet-urls")]
    [InlineData("command-line-urls")]
    [InlineData("kestrel-endpoint")]
    [InlineData("http-ports")]
    [InlineData("https-ports")]
    [InlineData("testing-hostile-url")]
    public async Task AlternativeListenerConfiguration_FailsStartupClosed(string scenario)
    {
        int port = GetUnusedPort();
        using Process process = CreateDashboardProcess(port, "Production");

        switch (scenario)
        {
            case "aspnetcore-urls":
                process.StartInfo.Environment["ASPNETCORE_URLS"] = $"http://0.0.0.0:{port}";
                break;
            case "dotnet-urls":
                process.StartInfo.Environment["DOTNET_URLS"] = $"http://0.0.0.0:{port}";
                break;
            case "command-line-urls":
                process.StartInfo.ArgumentList.Add("--urls");
                process.StartInfo.ArgumentList.Add($"http://0.0.0.0:{port}");
                break;
            case "kestrel-endpoint":
                process.StartInfo.Environment["Kestrel__Endpoints__Remote__Url"] =
                    $"http://0.0.0.0:{port}";
                break;
            case "http-ports":
                process.StartInfo.Environment["ASPNETCORE_HTTP_PORTS"] = port.ToString();
                break;
            case "https-ports":
                process.StartInfo.Environment["ASPNETCORE_HTTPS_PORTS"] = port.ToString();
                break;
            case "testing-hostile-url":
                process.StartInfo.Environment["DOTNET_ENVIRONMENT"] = "Testing";
                process.StartInfo.Environment["ASPNETCORE_URLS"] = $"http://0.0.0.0:{port}";
                break;
            default:
                throw new InvalidOperationException($"Unknown binding test scenario: {scenario}");
        }

        process.Start();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
            throw new Xunit.Sdk.XunitException(
                $"Dashboard did not reject hostile listener configuration for scenario '{scenario}'.");
        }

        string output = await standardOutput + await standardError;

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("Dashboard binding accepts only Dashboard:Port", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("urls")]
    [InlineData("http_ports")]
    [InlineData("https_ports")]
    [InlineData("Kestrel:Endpoints:Remote:Url")]
    public void NormalizedListenerKeys_AreRejectedRegardlessOfConfigurationProvider(string key)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [key] = "http://0.0.0.0:15081",
            })
            .Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            DashboardHostConfiguration.RejectAlternativeListeners(configuration));

        Assert.Contains("Dashboard:Port", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Testing")]
    public async Task DashboardPort_BindsExactlyOneIpv4LoopbackListener(string environment)
    {
        int port = GetUnusedPort();
        using Process process = CreateDashboardProcess(port, environment);
        process.Start();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();

        try
        {
            await WaitForHealthAsync(process, port, standardOutput, standardError);
            IPEndPoint[] listeners = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(endpoint => endpoint.Port == port)
                .ToArray();

            IPEndPoint listener = Assert.Single(listeners);
            Assert.Equal(IPAddress.Loopback, listener.Address);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutput, standardError);
        }
    }

    private static Process CreateDashboardProcess(int port, string environment)
    {
        string assemblyPath = typeof(Program).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("Dashboard assembly directory is unavailable."),
        };
        startInfo.ArgumentList.Add(assemblyPath);
        foreach (string key in ListenerEnvironmentKeys)
        {
            startInfo.Environment.Remove(key);
        }

        startInfo.Environment.Remove("ASPNETCORE_ENVIRONMENT");
        startInfo.Environment.Remove("DOTNET_ENVIRONMENT");
        startInfo.Environment["DOTNET_ENVIRONMENT"] = environment;
        startInfo.Environment["Dashboard__Port"] = port.ToString();
        startInfo.Environment["Logging__LogLevel__Default"] = "None";
        return new Process { StartInfo = startInfo };
    }

    private static async Task WaitForHealthAsync(
        Process process,
        int port,
        Task<string> standardOutput,
        Task<string> standardError)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (process.HasExited)
            {
                string output = await standardOutput + await standardError;
                throw new Xunit.Sdk.XunitException(
                    $"Dashboard exited before becoming healthy. Exit {process.ExitCode}.{Environment.NewLine}{output}");
            }

            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    $"http://127.0.0.1:{port}/healthz");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException("Dashboard did not become healthy within the test timeout.");
    }

    private static int GetUnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
