using DigitalBrain.Modules.Options;

namespace IntoChat.OS.Modules.ArtCraft;

// Where each app's MCP server (Streamable HTTP) and web build are served. Hosted apps use the fixed loopback
// ports ArtCraftCatalog names, so the brain, the containers, the Playwright app origins and a person's browser
// all name the same URL.
public sealed class ArtCraftModuleOptions : IModuleOptions
{
    public static string[] HostedOrigins =>
        [.. ArtCraftCatalog.All.Select(static app => $"http://localhost:{app.DefaultWebPort}")];

    public bool Host { get; set; }

    public Dictionary<string, string?> McpEndpoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> WebEndpoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? PhotoCraftMcp
    {
        get => GetMcp("photocraft");
        set => SetMcp("photocraft", value);
    }

    public string? FilmCraftMcp
    {
        get => GetMcp("filmcraft");
        set => SetMcp("filmcraft", value);
    }

    public string? PhotoCraftWeb
    {
        get => GetWeb("photocraft");
        set => SetWeb("photocraft", value);
    }

    public string? FilmCraftWeb
    {
        get => GetWeb("filmcraft");
        set => SetWeb("filmcraft", value);
    }

    // When set (e.g. ghcr.io/intochat), the apps run the prebuilt os-artcraft-<app>-<mcp|web> images tagged with the
    // first 12 characters of the pinned ref; null builds the Dockerfiles locally.
    public string? ImageRegistry { get; set; }

    public string? GetMcp(string appId)
        => McpEndpoints.TryGetValue(ArtCraftCatalog.NormalizeAppId(appId), out var url) ? url : null;

    public void SetMcp(string appId, string? url)
        => McpEndpoints[ArtCraftCatalog.NormalizeAppId(appId)] = url;

    public string? GetWeb(string appId)
        => WebEndpoints.TryGetValue(ArtCraftCatalog.NormalizeAppId(appId), out var url) ? url : null;

    public void SetWeb(string appId, string? url)
        => WebEndpoints[ArtCraftCatalog.NormalizeAppId(appId)] = url;

    public ArtCraftModuleOptions WithHostedApps()
    {
        Host = true;
        foreach (var descriptor in ArtCraftCatalog.All)
        {
            McpEndpoints[descriptor.Id] = descriptor.DefaultMcpUrl;
            WebEndpoints[descriptor.Id] = descriptor.DefaultWebUrl;
        }
        return this;
    }

    public void Validate()
    {
        foreach (var url in McpEndpoints.Values.Concat(WebEndpoints.Values))
        {
            if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            {
                throw new ArgumentException($"ArtCraft endpoint '{url}' must be an absolute http(s) URL.");
            }
        }
    }

    internal (string Title, string? Mcp, string? Web) For(string app)
    {
        var descriptor = ArtCraftCatalog.Get(app);
        return (descriptor.Title, GetMcp(descriptor.Id), GetWeb(descriptor.Id));
    }
}
