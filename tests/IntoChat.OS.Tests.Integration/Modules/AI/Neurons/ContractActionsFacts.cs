using System.Text.Json;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Modules.AI.Agents;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// The Integration fixture's silo pipeline has no grant filter, so a real denial cannot be produced here; these facts
// pin the shape the model receives when the filter's denial reaches a tool call as an UnauthorizedAccessException.
public sealed class ContractActionsFacts
{
    private const string Denial = "No grant for semantic type 'run'. The value is treated as missing.";

    [Fact]
    public async Task ADeniedToolCallReturnsAnErrorToTheModel()
    {
        using var _ = RuntimeCallerContext.Stamp(new CapabilityCaller("p1", "a1", "b1"));

        var result = await ContractActions.Marked("chat-1", () => throw new UnauthorizedAccessException(Denial));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.True(json.RootElement.GetProperty("isError").GetBoolean());
        Assert.Equal(Denial, json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AToolCallRunsAsTheAgentInItsConversationAndRestoresTheCaller()
    {
        var original = new CapabilityCaller("p1", "a1", "b1");
        using var _ = RuntimeCallerContext.Stamp(original);
        CapabilityCaller? seen = null;

        await ContractActions.Marked("chat-1", () =>
        {
            seen = RuntimeCallerContext.Require();
            return Task.FromResult<object>(new { ok = true });
        });

        Assert.Equal(CallerKind.Agent, seen!.Kind);
        Assert.Equal(("p1", "a1", "b1", "chat-1"), (seen.PrincipalId, seen.AccountId, seen.BrainId, seen.ConversationId));
        Assert.Equal(original, RuntimeCallerContext.Require());
    }

    [Fact]
    public void NormalizeArgumentsAcceptsNullStringifiedArrayAndNativeJsonArray()
    {
        Assert.Equal("[]", ContractActions.NormalizeArguments(null));
        Assert.Equal("[]", ContractActions.NormalizeArguments(JsonDocument.Parse("null").RootElement));
        Assert.Equal("[\"owner\",\"pref\",\"body\"]", ContractActions.NormalizeArguments(JsonDocument.Parse("""["owner","pref","body"]""").RootElement));
        Assert.Equal("[\"owner\",\"pref\",\"body\"]", ContractActions.NormalizeArguments(JsonDocument.Parse(""""["owner","pref","body"]"""").RootElement));
    }
}
