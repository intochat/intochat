using DigitalBrain.AI;
using DigitalBrain.AI.Agents.Context;
using DigitalBrain.AI.Agents.Context.Signals;

namespace DigitalBrain.Modules.AI.Tests.Agents.Context;

public sealed class AgentContextFacts
{
    [Fact]
    public async Task SetReplacesAnEntryByNameAndPublishesContextUpdated()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule>().StartAsync(ct);
        var context = brain.Get<IAgentContext>("owner/agents/assistant/context");
        await using var updates = await brain.Observe<ContextUpdated>(context, ct);
        await context.Set(new("capabilities", "Available: invoices", ["lookup"]));
        await context.Set(new("workspace", "Workspace: research", []));
        await context.Set(new("capabilities", "Available: invoices, orders", ["lookup", "orders"]));
        var snapshot = await context.Read();
        Assert.Equal(3, snapshot.Revision);
        Assert.Equal(["capabilities", "workspace"], snapshot.Entries.Select(entry => entry.Name));
        Assert.Equal("Available: invoices, orders", snapshot.Entries[0].Text);
        var first = await updates.NextAsync(ct: ct);
        Assert.Equal(("capabilities", 1L), (first.Name, first.Revision));
    }

    [Fact]
    public async Task RemoveDropsTheEntryPublishesContextRemovedAndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule>().StartAsync(ct);
        var context = brain.Get<IAgentContext>("owner/agents/assistant/context");
        await context.Set(new("capabilities", "x", []));
        await using var removed = await brain.Observe<ContextRemoved>(context, ct);
        await context.Remove("capabilities");
        await context.Remove("capabilities");
        Assert.Empty((await context.Read()).Entries);
        Assert.Equal(2, (await context.Read()).Revision);
        Assert.Equal("capabilities", (await removed.NextAsync(ct: ct)).Name);
    }

    [Fact]
    public async Task EntriesSurviveReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule>().StartAsync(ct);
        var context = brain.Get<IAgentContext>("owner/agents/assistant/context");
        await context.Set(new("capabilities", "kept", ["lookup"]));
        await brain.DeactivateAsync(context, ct);
        var entry = Assert.Single((await context.Read()).Entries);
        Assert.Equal(("capabilities", "kept"), (entry.Name, entry.Text));
        Assert.Equal(["lookup"], entry.Contracts);
    }

    [Fact]
    public async Task AnEntryNeedsABoundedNameTextAndTools()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule>().StartAsync(ct);
        var context = brain.Get<IAgentContext>("owner/agents/assistant/context");
        await Assert.ThrowsAsync<ArgumentException>(() => context.Set(new("", "x", [])));
        await Assert.ThrowsAsync<ArgumentException>(() => context.Set(new(new string('n', 201), "x", [])));
        await Assert.ThrowsAsync<ArgumentException>(() => context.Set(new("big", new string('x', 32001), [])));
        // JSON from digitalbrain-mcp or a script leaves a missing field null.
        await Assert.ThrowsAsync<ArgumentException>(() => context.Set(new("no-text", null!, [])));
        await Assert.ThrowsAsync<ArgumentException>(() => context.Set(new("no-tools", "x", null!)));
        Assert.Empty((await context.Read()).Entries);
    }
}
