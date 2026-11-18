using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using IntoChat.OS.Tests;
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
        var edge = TestEdge.Authority;
        using var _ = edge.Enter(edge.Person("p1", "a1", new BrainId("b1")));

        var result = await ContractActions.Marked(edge, "chat-1", () => throw new UnauthorizedAccessException(Denial));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.True(json.RootElement.GetProperty("isError").GetBoolean());
        Assert.Equal(Denial, json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AToolCallRunsAsTheAgentInItsConversationAndRestoresTheCaller()
    {
        var edge = TestEdge.Authority;
        var original = edge.Person("p1", "a1", new BrainId("b1"));
        using var _ = edge.Enter(original);
        Caller? seen = null;

        await ContractActions.Marked(edge, "chat-1", () =>
        {
            seen = edge.Current;
            return Task.FromResult<object>(new { ok = true });
        });

        Assert.IsType<Actor.Agent>(seen!.Actor);
        Assert.Equal(("p1", "a1", "b1", "chat-1"), (seen.For!.Principal, seen.For.Account, seen.Brain.Value, seen.Conversation));
        Assert.Equal(original, edge.Current);
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
