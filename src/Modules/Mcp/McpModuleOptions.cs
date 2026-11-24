using System.Text.RegularExpressions;
using DigitalBrain.Modules.Options;

namespace IntoChat.OS.Modules.Mcp;

public sealed class McpServerOptions
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public bool Enabled { get; set; } = true;
    public string[]? AllowedTools { get; set; }
    public McpConfirmationMode ConfirmationMode { get; set; } = McpConfirmationMode.None;
}

public sealed partial class McpModuleOptions : IModuleOptions
{
    [Setting]
    public Dictionary<string, McpServerOptions> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["microsoft-learn"] = new() { Url = "https://learn.microsoft.com/api/mcp", Title = "Microsoft Learn" },
    };

    public void Validate()
    {
        if (Servers is null)
        {
            return;
        }

        foreach (var (name, server) in Servers)
        {
            if (string.IsNullOrEmpty(name) || !ServerNamePattern().IsMatch(name))
            {
                throw new ArgumentException($"MCP server name '{name}' must match ^[a-z0-9][a-z0-9-]{{0,31}}$.");
            }

            if (server is null)
            {
                throw new ArgumentException($"MCP server '{name}' options cannot be null.");
            }

            if (!server.Enabled && string.IsNullOrWhiteSpace(server.Url))
            {
                continue;
            }

            if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new ArgumentException($"MCP server '{name}' URL must be an absolute https URL (http is allowed only for loopback) without credentials.");
            }

            var allowed = uri.Scheme == Uri.UriSchemeHttps
                || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
            if (!allowed)
            {
                throw new ArgumentException($"MCP server '{name}' URL must use https (http is allowed only for loopback).");
            }

            if (server.AllowedTools is not null && server.AllowedTools.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException($"MCP server '{name}' AllowedTools entries must be non-empty.");
            }
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServerNamePattern();
}
