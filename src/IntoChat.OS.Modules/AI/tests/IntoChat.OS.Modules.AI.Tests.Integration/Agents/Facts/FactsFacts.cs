using IntoChat.OS.AI.Agents.Facts;
using IntoChat.OS.AI.Agents.Facts.Signals;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Agents.Facts;

public sealed class FactsFacts(AIHost host)
{
    private static FactAssertion Assertion(string dedup, string text = "Likes tea", int importance = 5, double confidence = 0.8) =>
        new(FactKind.Fact, text, "drinks", confidence, importance, DateTimeOffset.UtcNow, "episodes", 1, "v1", [], dedup);

    [Fact]
    public async Task AssertingTwiceForOneDedupKeyReturnsTheSameFact()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var facts = host.Brain.Get<IFacts>(IFacts.Key(person.BrainId));
        await using var asserted = await host.Brain.Observe<FactAsserted>(facts, ct);
        var first = await facts.Assert(Assertion("ep-1#0"));
        var second = await facts.Assert(Assertion("ep-1#0"));
        Assert.Equal(first, second);
        Assert.Single((await facts.Read()).Facts);
        var signal = await asserted.NextAsync(ct: ct);
        Assert.Equal((first, "ep-1#0"), (signal.FactId, signal.DedupKey));
    }

    [Fact]
    public async Task InvalidatingKeepsTheFactWithValidToAndReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var facts = host.Brain.Get<IFacts>(IFacts.Key(person.BrainId));
        await using var invalidated = await host.Brain.Observe<FactInvalidated>(facts, ct);
        var id = await facts.Assert(Assertion("ep-1#0"));
        await facts.Invalidate(id, "moved", null, (await facts.Read()).Revision);
        var fact = Assert.Single((await facts.Read()).Facts);
        Assert.NotNull(fact.ValidTo);
        Assert.Equal("moved", fact.InvalidReason);
        Assert.Empty(await facts.Current());
        Assert.Equal(id, (await invalidated.NextAsync(ct: ct)).FactId);
    }

    [Fact]
    public async Task ForgettingHidesFromCurrentButNotFromRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var facts = host.Brain.Get<IFacts>(IFacts.Key(person.BrainId));
        await using var forgotten = await host.Brain.Observe<FactForgotten>(facts, ct);
        var id = await facts.Assert(Assertion("ep-1#0"));
        await facts.Forget(id, "asked", (await facts.Read()).Revision);
        Assert.Empty(await facts.Current());
        Assert.True(Assert.Single((await facts.Read()).Facts).Hidden);
        Assert.Equal(id, (await forgotten.NextAsync(ct: ct)).FactId);
    }

    [Fact]
    public async Task AStaleRevisionIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var facts = host.Brain.Get<IFacts>(IFacts.Key(person.BrainId));
        var id = await facts.Assert(Assertion("ep-1#0"));
        await Assert.ThrowsAsync<RevisionConflictException>(() => facts.Invalidate(id, "x", null, 0));
        await Assert.ThrowsAsync<RevisionConflictException>(() => facts.Forget(id, "x", 0));
        Assert.Null(Assert.Single((await facts.Read()).Facts).ValidTo);
    }

    [Fact]
    public async Task TheCapRefusesTheFiveHundredThirteenthCurrentFact()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var facts = host.Brain.Get<IFacts>(IFacts.Key(person.BrainId));
        for (var i = 0; i < IFacts.Cap; i++) { await facts.Assert(Assertion($"ep-{i}#0")); }
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => facts.Assert(Assertion("ep-over#0")));
        Assert.Contains("512", refused.Message);
        Assert.Equal(512, (await facts.Read()).Facts.Length);
    }

    [Fact]
    public async Task ImportanceOutsideOneToTenIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var facts = host.Brain.Get<IFacts>(IFacts.Key(person.BrainId));
        foreach (var bad in new[] { 0, 11 })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => facts.Assert(Assertion("ep-1#0", importance: bad)));
        }
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => facts.Assert(Assertion("ep-1#0", confidence: double.NaN)));
        Assert.Empty((await facts.Read()).Facts);
    }

    [Fact]
    public async Task InvalidatingLinksTheSuperseder()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var facts = host.Brain.Get<IFacts>(IFacts.Key(person.BrainId));
        var old = await facts.Assert(Assertion("ep-1#0"));
        var next = await facts.Assert(Assertion("ep-2#0", "Likes coffee"));
        await facts.Invalidate(old, "changed", next, (await facts.Read()).Revision);
        var all = (await facts.Read()).Facts;
        Assert.Contains(next, all.Single(f => f.Id == old).Links);
        Assert.Contains(old, all.Single(f => f.Id == next).Links);
    }
}
