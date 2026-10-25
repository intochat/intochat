using Aspire.Hosting;

namespace IntoChat.OS.Testing.Aspire;

// Chromium as a resource of the test host: Playwright's own image serving `run-server`, at the
// exact version of the Playwright driver the tests drive it with (the wire protocol requires an
// exact match), so CI needs Docker and no browser install.
/// <summary>Chromium as a resource of the test host, served by Playwright's own image.</summary>
public static class BrowserContainer
{
    internal const string ResourceName = "e2e-browser";
    internal const string EndpointName = "ws";
    private const int Port = 3000;

    internal static string PlaywrightVersion { get; } = PackageVersion();

    // The npm CLI is installed in a layer, so every session after the first reuses a cached image
    // and the server starts without touching the network.
    internal static void AddTo(IDistributedApplicationBuilder builder)
    {
        var context = Path.Combine(Path.GetTempPath(), "intochat-os-e2e-browser");
        Directory.CreateDirectory(context);
        builder.AddDockerfileFactory(ResourceName, context, _ => $$"""
                FROM mcr.microsoft.com/playwright:v{{PlaywrightVersion}}-noble
                RUN npm install -g playwright@{{PlaywrightVersion}}
                USER pwuser
                WORKDIR /home/pwuser
                ENTRYPOINT ["playwright", "run-server", "--port", "{{Port}}", "--host", "0.0.0.0"]
                """)
            .WithImageTag(PlaywrightVersion)
            .WithHttpEndpoint(targetPort: Port, name: EndpointName)
            .WithHttpHealthCheck("/", endpointName: EndpointName);
    }

    // Connections address the browser's own loopback through the test process, so the shell's
    // localhost endpoints load exactly as they do for a local browser.
    /// <summary>The network a container browser exposes back to the test process.</summary>
    public const string ExposedNetwork = "<loopback>";

    // The assembly carries no real version; the Node driver the package copies beside the tests does,
    // and the server must speak that driver's protocol.
    private static string PackageVersion()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, ".playwright", "package", "package.json");
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
        return json.RootElement.GetProperty("version").GetString()
            ?? throw new InvalidOperationException($"The Playwright driver at {manifest} names no version.");
    }
}
