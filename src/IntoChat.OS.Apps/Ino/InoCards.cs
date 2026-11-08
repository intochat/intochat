using System.Text.Json;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Ino;

// Ino tools answer InoToolResult, which the neuron surface serializes with web (camelCase) names:
// a completed call whose result object has a "card" object carries a card to render.
internal static class InoCards
{
    public static InoCard[] FromToolCalls(IEnumerable<AgentToolCall> calls)
        => [.. calls.Select(call => call.Result is { Length: > 0 } result ? FromResult(result) : null).OfType<InoCard>()];

    private static InoCard? FromResult(string result)
    {
        try
        {
            using var payload = JsonDocument.Parse(result);
            return payload.RootElement.ValueKind == JsonValueKind.Object
                && payload.RootElement.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object
                ? card.Deserialize<InoCard>(JsonSerializerOptions.Web)
                : null;
        }
        catch (JsonException) { return null; }
    }
}
