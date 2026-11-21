using DigitalBrain.Capabilities.Connections.Connectors;

namespace IntoChat.OS.Modules.Web;

internal static class WebIntegrations
{
    internal const string ApiKeyField = "ApiKey";
    internal const string BraveId = "brave";
    internal const string TavilyId = "tavily";

    internal static IntegrationSchema Brave { get; } = IntegrationSchema.For(BraveId, "Brave Search").RequiresSecret(ApiKeyField);
    internal static IntegrationSchema Tavily { get; } = IntegrationSchema.For(TavilyId, "Tavily").RequiresSecret(ApiKeyField);

    internal static IntegrationSchema[] All { get; } = [Brave, Tavily];

    internal static ConnectorDefinition[] Connectors { get; } =
    [
        .. All.Select(static schema => new ConnectorDefinition(schema.Id, schema.DisplayName, ["api-key"], [schema.Id])),
    ];
}
