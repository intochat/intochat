using IntoChat.OS.AI.Agents.Intents;
using IntoChat.OS.AI.Agents.Intents.Signals;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Agents.Intents;

public sealed class IntentsFacts(AIHost host)
{
    [Fact]
    public async Task DeclaringTwiceForOneRequestReturnsTheSameIntent()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(person.Key("memory/intents"));
        var first = await intents.Declare(new("req-1", "ship it", "conv/a"));
        var second = await intents.Declare(new("req-1", "ship it", "conv/a"));
        Assert.Equal(first, second);
        var intent = Assert.Single((await intents.Read()).Intents);
        Assert.Equal(IntentState.Heard, intent.State);
        Assert.Equal("req-1", Assert.Single(intent.History).RequestId);
    }

    [Fact]
    public async Task DeclaringPublishesIntentHeard()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(person.Key("memory/intents"));
        await using var heard = await host.Brain.Observe<IntentHeard>(intents, ct);
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        var signal = await heard.NextAsync(ct: ct);
        Assert.Equal((id, "req-1"), (signal.IntentId, signal.RequestId));
    }

    [Fact]
    public async Task TheActiveCapRefusesTheTwoHundredFiftySeventhIntent()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(person.Key("memory/intents"));
        for (var i = 0; i < IIntents.ActiveCap; i++) { await intents.Declare(new($"req-{i}", "ship it", "conv/a")); }
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => intents.Declare(new("req-over", "ship it", "conv/a")));
        Assert.Contains("256", refused.Message);
    }

    [Fact]
    public async Task ContinuingAnIntentDoesNotCreateANewOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(person.Key("memory/intents"));
        var a = await intents.Declare(new("req-1", "ship it", "conv/a"));
        var again = await intents.Declare(new("req-2", "and also", "conv/a", a));
        Assert.Equal(a, again);
        Assert.Single((await intents.Read()).Intents);
    }

    private static readonly IntentBrief Brief = new("Ship the plan", ["plan path"], [], ["PR open"]);

    [Fact]
    public async Task RephrasePublishesTheBriefAndMovesToRephrased()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(person.Key("memory/intents"));
        await using var rephrased = await host.Brain.Observe<IntentRephrased>(intents, ct);
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        await intents.Rephrase(id, Brief, 0.9, (await intents.Read()).Revision);
        var intent = Assert.Single((await intents.Read()).Intents);
        Assert.Equal(IntentState.Rephrased, intent.State);
        Assert.Equal("Ship the plan", intent.Brief!.Goal);
        Assert.Equal(0.9, intent.Confidence);
        var signal = await rephrased.NextAsync(ct: ct);
        Assert.Equal((0.9, "req-1"), (signal.Confidence, signal.RequestId));
    }

    [Fact]
    public async Task AnIllegalTransitionIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(person.Key("memory/intents"));
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () => await intents.Transition(id, IntentState.Done, "x", (await intents.Read()).Revision));
        Assert.Contains("Heard", refused.Message);
        Assert.Contains("Done", refused.Message);
    }

    [Fact]
    public async Task TheHappyPathRunsToDone()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(person.Key("memory/intents"));
        await using var moved = await host.Brain.Observe<IntentTransitioned>(intents, ct);
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        await intents.Rephrase(id, Brief, 0.9, (await intents.Read()).Revision);
        foreach (var to in new[] { IntentState.Confirmed, IntentState.Dispatched, IntentState.Waiting, IntentState.Dispatched, IntentState.Done })
        {
            await intents.Transition(id, to, "step", (await intents.Read()).Revision);
        }
        Assert.Equal(7, Assert.Single((await intents.Read()).Intents).History.Length);
        IntentTransitioned? last = null;
        for (var i = 0; i < 5; i++) { last = await moved.NextAsync(ct: ct); }
        Assert.Equal((IntentState.Dispatched, IntentState.Done), (last!.From, last.To));
    }

    [Fact]
    public async Task AStaleRevisionIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(person.Key("memory/intents"));
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        await Assert.ThrowsAsync<RevisionConflictException>(() => intents.Rephrase(id, Brief, 0.9, 0));
    }

    [Fact]
    public async Task ArmingPublishesIntentArmedAndKeepsTheCues()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(person.Key("memory/intents"));
        await using var armed = await host.Brain.Observe<IntentArmed>(intents, ct);
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        await intents.Arm(id, [new("time", "", "", null, null, DateTimeOffset.UtcNow.AddHours(1))], (await intents.Read()).Revision);
        Assert.Single(Assert.Single((await intents.Read()).Intents).Cues);
        Assert.Equal(id, (await armed.NextAsync(ct: ct)).IntentId);
    }

    [Fact]
    public async Task ATerminalIntentCannotMove()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(person.Key("memory/intents"));
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        await intents.Transition(id, IntentState.Abandoned, "no", (await intents.Read()).Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await intents.Transition(id, IntentState.Confirmed, "x", (await intents.Read()).Revision));
    }

    [Fact]
    public async Task RephraseKeepsTheStateOfADispatchedIntent()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(person.Key("memory/intents"));
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        await intents.Rephrase(id, Brief, 0.9, (await intents.Read()).Revision);
        await intents.Transition(id, IntentState.Confirmed, "ok", (await intents.Read()).Revision);
        await intents.Transition(id, IntentState.Dispatched, "go", (await intents.Read()).Revision);
        await intents.Rephrase(id, Brief with { Goal = "Ship it better" }, 0.8, (await intents.Read()).Revision);
        var intent = Assert.Single((await intents.Read()).Intents);
        Assert.Equal(IntentState.Dispatched, intent.State);
        Assert.Equal("Ship it better", intent.Brief!.Goal);
    }
}
