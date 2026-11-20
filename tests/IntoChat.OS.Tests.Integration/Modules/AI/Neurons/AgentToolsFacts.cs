using System.Diagnostics;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Apps.Tests.Integration.Ino;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Tools;
using IntoChat.OS.Modules.Apps.Runs;
using IntoChat.OS.Tests;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// The Integration fixture's silo pipeline has no grant filter, so a real denial cannot be produced here; these facts
// pin the shape the model receives when the filter's denial reaches a tool call as an UnauthorizedAccessException,
// plus stored-definition former-name resolution on the real silo.
public sealed class AgentToolsFacts(InoSiloFixture fixture) : IClassFixture<InoSiloFixture>
{
    private const string Denial = "No grant for semantic type 'run'. The value is treated as missing.";

    [Fact]
    public async Task ADeniedToolCallReturnsAnErrorToTheModel()
    {
        var edge = TestEdge.Authority;
        using var _ = edge.Enter(edge.Person("p1", "a1", new BrainId("b1")));

        var result = await AgentTools.Marked(edge, "chat-1", () => throw new UnauthorizedAccessException(Denial));

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

        await AgentTools.Marked(edge, "chat-1", () =>
        {
            seen = edge.Current;
            return Task.FromResult<object>(new { ok = true });
        });

        Assert.IsType<Actor.Agent>(seen!.Actor);
        Assert.Equal(("p1", "a1", "b1", "chat-1"), (seen.For!.Principal, seen.For.Account, seen.Brain.Value, seen.Conversation));
        Assert.Equal(original, edge.Current);
    }

    [Fact]
    public async Task AStoredDefinitionWithInoNamesKeepsItsTools()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = fixture.NewBrain("formertools");
        using var _ = fixture.AsPerson(brain);

        ChatOptions? lastOptions = null;
        fixture.Model.Respond((_, options, _) =>
        {
            lastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        });

        var agentKey = $"{brain}/ino/conversations/former";
        var agent = SiloNeurons.Get<IAgent>(fixture.Services, agentKey);
        await agent.Configure(new AgentDefinition { Contracts = ["IntoChat.OS.Modules.Apps.IInoFiles"] }, 0);

        var runId = await agent.Start(AgentTurn.Text("save a note"));
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            var state = await agent.Read();
            if (state.Runs.Any(r => r.RunId == runId)) { break; }
            await Task.Delay(35, ct);
        }

        Assert.NotNull(lastOptions?.Tools);
        Assert.Contains(lastOptions!.Tools!.OfType<AIFunction>(), static t => t.Name == "files_write");
    }

    [Fact]
    public async Task NeuronsCallAcceptsArrayAndObject()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = fixture.NewBrain("namedargs");
        using var _ = fixture.AsPerson(brain);
        var surface = fixture.Services.GetRequiredService<OsNeuronSurface>();
        var contract = typeof(IRuns).FullName!;
        var key = $"{brain}/runs/index";

        var byObject = await surface.Call(contract, key, nameof(IRuns.List), """{"limit":2}""", ct);
        var byArray = await surface.Call(contract, key, nameof(IRuns.List), "[2]", ct);
        Assert.Equal(byArray!.Value.GetRawText(), byObject!.Value.GetRawText());

        var typo = await Assert.ThrowsAsync<ArgumentException>(() => surface.Call(contract, key, nameof(IRuns.List), """{"limt":2}""", ct));
        Assert.Contains("'limt'", typo.Message, StringComparison.Ordinal);

        var byEmpty = await surface.Call(contract, key, nameof(IRuns.List), "", ct);
        var byEmptyObject = await surface.Call(contract, key, nameof(IRuns.List), "{}", ct);
        Assert.Equal(byEmpty!.Value.GetRawText(), byEmptyObject!.Value.GetRawText());
    }
}
