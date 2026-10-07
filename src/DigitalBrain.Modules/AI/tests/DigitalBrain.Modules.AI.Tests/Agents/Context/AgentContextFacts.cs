using DigitalBrain.AI;
using DigitalBrain.AI.Agents.Context;
using DigitalBrain.AI.Agents.Context.Signals;
using Xunit;

namespace DigitalBrain.Modules.AI.Tests.Agents.Context;

public sealed class AgentContextFacts
{
    [Fact]
    public async Task SetReplacesAnEntryByNameAndPublishesContextUpdated()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AIModule>().StartAsync(ct);
        var context = brain.Get<IAgentContext>("research/agents/assistant/context");
        await using var subscription = await brain.SubscribeAsync<ContextUpdated>(context, ct);
        await using var updates = subscription.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        await context.Set(new("capabilities", "Available: invoices", ["lookup"]));
        await context.Set(new("workspace", "Workspace: research", []));
        await context.Set(new("capabilities", "Available: invoices, orders", ["lookup", "orders"]));
        var snapshot = await context.Read();
        Assert.Equal(3, snapshot.Revision);
        Assert.Equal(["capabilities", "workspace"], snapshot.Entries.Select(entry => entry.Name));
        Assert.Equal("Available: invoices, orders", snapshot.Entries[0].Text);
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal(("capabilities", 1L), (updates.Current.Name, updates.Current.Revision));
    }

    [Fact]
    public async Task RemoveDropsTheEntryPublishesContextRemovedAndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AIModule>().StartAsync(ct);
        var context = brain.Get<IAgentContext>("research/agents/assistant/context");
        await context.Set(new("capabilities", "x", []));
        await using var subscription = await brain.SubscribeAsync<ContextRemoved>(context, ct);
        await using var removed = subscription.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        await context.Remove("capabilities");
        await context.Remove("capabilities");
        Assert.Empty((await context.Read()).Entries);
        Assert.Equal(2, (await context.Read()).Revision);
        Assert.True(await removed.MoveNextAsync());
        Assert.Equal("capabilities", removed.Current.Name);
    }

    [Fact]
    public async Task EntriesSurviveReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AIModule>().StartAsync(ct);
        var context = brain.Get<IAgentContext>("research/agents/assistant/context");
        await context.Set(new("capabilities", "kept", ["lookup"]));
        await brain.DeactivateAsync(context, ct);
        var entry = Assert.Single((await context.Read()).Entries);
        Assert.Equal(("capabilities", "kept"), (entry.Name, entry.Text));
        Assert.Equal(["lookup"], entry.Tools);
    }

    [Fact]
    public async Task AnEntryNeedsABoundedNameAndText()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AIModule>().StartAsync(ct);
        var context = brain.Get<IAgentContext>("research/agents/assistant/context");
        await Assert.ThrowsAsync<ArgumentException>(() => context.Set(new("", "x", [])));
        await Assert.ThrowsAsync<ArgumentException>(() => context.Set(new(new string('n', 201), "x", [])));
        await Assert.ThrowsAsync<ArgumentException>(() => context.Set(new("big", new string('x', 32001), [])));
    }
}
