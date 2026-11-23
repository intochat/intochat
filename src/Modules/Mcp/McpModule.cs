using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Mcp;

// McpServerNeuron is a plain neuron on the runtime's host grains. Configured servers reconcile at host start.
[DigitalBrain.Modules.Module(ManifestName)]
public sealed class McpModule : DigitalBrain.Modules.Module
{
    internal const string ManifestName = "mcp";

    public static ConnectorDefinition Connector => new(ManifestName, "MCP Servers", ["bearer-token"], [])
    {
        OptionalIntegrations = ["muncly"],
        Neurons = [typeof(IMcpServer)],
    };

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [DigitalBrain.Modules.Need.HttpClient("mcp", requestsPerMinute: 120)],
        Settings: [DigitalBrain.Modules.Setting.Of<McpModuleOptions>(ModuleConfiguration.ModuleOptionsSection(ManifestName))],
        Secrets: [])
    {
        Actions = [new("mcp.tool.run", "run an MCP tool", null)],
    };

    public static System.Reflection.Assembly? Contracts { get; } = typeof(IMcpServer).Assembly;

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.PostConfigure<McpModuleOptions>(options => AttachMunclyServer(options, configuration));
        services.TryAddSingleton<McpServersBootstrap>();
        services.AddHostedService(static sp => sp.GetRequiredService<McpServersBootstrap>());
    }

    internal static void AttachMunclyServer(McpModuleOptions options, IConfiguration configuration)
    {
        options.Servers ??= new(StringComparer.OrdinalIgnoreCase);
        if (options.Servers.ContainsKey("muncly-crm"))
        {
            return;
        }

        var enabled = configuration["IntoChatOS:Modules:muncly:Options:Enabled"];
        if (string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var baseUrl = FirstNonEmpty(
            configuration["IntoChatOS:Modules:muncly:Options:BaseUrl"],
            configuration["IntoChatOS:Integrations:muncly:BaseUrl"],
            configuration["MUNCLY_CRM_URL"]);
        var token = FirstNonEmpty(
            configuration["IntoChatOS:Integrations:muncly:McpToken"],
            configuration["MUNCLY_MCP_TOKEN"]);

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return;
        }

        var mcpUrl = $"{uri.GetLeftPart(UriPartial.Authority).TrimEnd('/')}/api/mcp";
        var sets = FirstNonEmpty(
            configuration["IntoChatOS:Modules:muncly:Options:ToolSets"],
            configuration["IntoChatOS:Integrations:muncly:ToolSets"]);
        if (!string.IsNullOrWhiteSpace(sets))
        {
            mcpUrl += $"?sets={Uri.EscapeDataString(sets.Trim())}";
        }

        var allowed = configuration.GetSection("IntoChatOS:Modules:muncly:Options:AllowedTools").Get<string[]>();
        var headerValue = token.Trim().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? token.Trim()
            : $"Bearer {token.Trim()}";

        options.Servers["muncly-crm"] = new McpServerOptions
        {
            Url = mcpUrl,
            Title = "Muncly CRM",
            Enabled = true,
            HeaderName = "Authorization",
            HeaderValue = headerValue,
            AllowedTools = allowed is { Length: > 0 } ? allowed : null,
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
