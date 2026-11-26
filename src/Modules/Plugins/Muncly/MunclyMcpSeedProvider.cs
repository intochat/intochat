using DigitalBrain.Capabilities.Connections.Integrations;
using IntoChat.OS.Modules.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IntoChat.Muncly;

// One-time seed provider that migrates Muncly's legacy config/integration MCP token onto
// the owner brain's McpToolConnection neuron without leaking Muncly knowledge into McpModule.
internal sealed class MunclyMcpSeedProvider(
    IConfiguration configuration,
    IModuleIntegrations<MunclyModule>? integrations = null,
    ILogger<MunclyMcpSeedProvider>? logger = null) : IMcpConnectionSeedProvider
{
    public async ValueTask<McpConnectionSeed?> ResolveAsync(string ownerBrainId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerBrainId);
        ct.ThrowIfCancellationRequested();

        var enabledRaw = configuration["IntoChatOS:Modules:muncly:Options:Enabled"];
        if (string.Equals(enabledRaw, "false", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var baseUrl = FirstNonEmpty(
            configuration["IntoChatOS:Modules:muncly:Options:BaseUrl"],
            configuration["IntoChatOS:Integrations:muncly:BaseUrl"],
            configuration["MUNCLY_BASE_URL"]);
        var toolSets = FirstNonEmpty(
            configuration["IntoChatOS:Modules:muncly:Options:ToolSets"],
            configuration["IntoChatOS:Integrations:muncly:ToolSets"],
            configuration["MUNCLY_TOOL_SETS"]);
        var token = FirstNonEmpty(
            configuration["IntoChatOS:Integrations:muncly:McpToken"],
            configuration["MUNCLY_MCP_TOKEN"]);

        if ((baseUrl is null || token is null) && integrations is not null)
        {
            try
            {
                var handle = integrations.Get(MunclyModule.ManifestName);
                if (baseUrl is null || toolSets is null)
                {
                    var snapshot = await handle.ReadAsync(ct).ConfigureAwait(false);
                    if (baseUrl is null && snapshot.Settings.TryGetValue(MunclyModule.BaseUrlField, out var savedUrl) && !string.IsNullOrWhiteSpace(savedUrl))
                    {
                        baseUrl = savedUrl.Trim();
                    }

                    if (toolSets is null && snapshot.Settings.TryGetValue(MunclyModule.ToolSetsField, out var savedSets) && !string.IsNullOrWhiteSpace(savedSets))
                    {
                        toolSets = savedSets.Trim();
                    }
                }

                if (token is null)
                {
                    var savedToken = await handle.ReadSecretAsync(MunclyModule.McpTokenField, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(savedToken))
                    {
                        token = savedToken.Trim();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // Optional legacy integration fallback must not break startup
            catch (Exception error)
#pragma warning restore CA1031
            {
                logger?.LogDebug(error, "Could not read legacy Muncly integration settings while resolving MCP seed.");
            }
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var allowedTools = configuration.GetSection("IntoChatOS:Modules:muncly:Options:AllowedTools").Get<string[]>();
        if (allowedTools is { Length: 0 })
        {
            allowedTools = null;
        }

        var options = new MunclyOptions
        {
            BaseUrl = baseUrl,
            ToolSets = toolSets,
            AllowedTools = allowedTools,
        };
        options.Validate();
        var url = options.BuildMcpEndpointUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var hasToken = !string.IsNullOrWhiteSpace(token);
        var config = new McpConnectionConfig(
            Url: url,
            Title: "Muncly CRM",
            HeaderName: hasToken ? "Authorization" : null,
            HeaderPrefix: hasToken ? "Bearer " : null,
            AllowedTools: allowedTools);

        return new McpConnectionSeed(MunclyOptions.DefaultMcpServerName, config, hasToken ? token : null);
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }
}
