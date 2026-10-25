using IntoChat.OS.Modules.AI.Agents.Intents;
using IntoChat.OS.Modules.AI.Agents.Intents.Signals;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Agents.Intents;

public sealed class IntentsFacts(AIHost host)
{
    [Fact]
    public async Task DeclaringTwiceForOneRequestReturnsTheSameIntent()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
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
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
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
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
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
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
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
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
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
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
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
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
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
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        await Assert.ThrowsAsync<RevisionConflictException>(() => intents.Rephrase(id, Brief, 0.9, 0));
    }

    [Fact]
    public async Task AStaleRevisionRefusesATransitionAndLeavesTheIntent()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        var stale = (await intents.Read()).Revision;
        await intents.Rephrase(id, Brief, 0.9, stale);
        await Assert.ThrowsAsync<RevisionConflictException>(() => intents.Transition(id, IntentState.Confirmed, "late", stale));
        var after = await intents.Read();
        Assert.Equal(IntentState.Rephrased, Assert.Single(after.Intents).State);
        await intents.Transition(id, IntentState.Confirmed, "fresh", after.Revision);
        Assert.Equal(IntentState.Confirmed, Assert.Single((await intents.Read()).Intents).State);
    }

    [Fact]
    public async Task AStaleRevisionRefusesArmingAndKeepsTheCues()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        var stale = (await intents.Read()).Revision;
        await intents.Arm(id, [new("time", "", "", null, null, DateTimeOffset.UtcNow.AddHours(1))], stale);
        await Assert.ThrowsAsync<RevisionConflictException>(() => intents.Arm(id, [], stale));
        Assert.Single(Assert.Single((await intents.Read()).Intents).Cues);
    }

    [Fact]
    public async Task ArmingPublishesIntentArmedAndKeepsTheCues()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
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
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
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
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
        await using var rephrased = await host.Brain.Observe<IntentRephrased>(intents, ct);
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        await intents.Rephrase(id, Brief, 0.9, (await intents.Read()).Revision);
        await rephrased.NextAsync(ct: ct);
        await intents.Transition(id, IntentState.Confirmed, "ok", (await intents.Read()).Revision);
        await intents.Transition(id, IntentState.Dispatched, "go", (await intents.Read()).Revision);
        var before = await intents.Read();
        await intents.Rephrase(id, Brief with { Goal = "Ship it better" }, 0.8, before.Revision);
        var after = await intents.Read();
        var intent = Assert.Single(after.Intents);
        Assert.Equal(IntentState.Dispatched, intent.State);
        Assert.Equal("Ship it better", intent.Brief!.Goal);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(before.Intents[0].History.Length, intent.History.Length);
        var signal = await rephrased.NextAsync(ct: ct);
        Assert.Equal((0.8, "req-1"), (signal.Confidence, signal.RequestId));
    }

    [Fact]
    public async Task RephraseKeepsTheStateOfAWaitingIntent()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        await intents.Rephrase(id, Brief, 0.9, (await intents.Read()).Revision);
        foreach (var to in new[] { IntentState.Confirmed, IntentState.Dispatched, IntentState.Waiting }) { await intents.Transition(id, to, "step", (await intents.Read()).Revision); }
        await intents.Rephrase(id, Brief with { Goal = "Later" }, 0.7, (await intents.Read()).Revision);
        var intent = Assert.Single((await intents.Read()).Intents);
        Assert.Equal(IntentState.Waiting, intent.State);
        Assert.Equal("Later", intent.Brief!.Goal);
    }

    [Fact]
    public async Task RephraseReturnsAConfirmedIntentToRephrased()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        await intents.Rephrase(id, Brief, 0.9, (await intents.Read()).Revision);
        await intents.Transition(id, IntentState.Confirmed, "ok", (await intents.Read()).Revision);
        await intents.Rephrase(id, Brief with { Goal = "Changed" }, 0.6, (await intents.Read()).Revision);
        var intent = Assert.Single((await intents.Read()).Intents);
        Assert.Equal(IntentState.Rephrased, intent.State);
        Assert.Equal("Changed", intent.Brief!.Goal);
    }

    [Theory]
    [InlineData(IntentState.Done)]
    [InlineData(IntentState.Abandoned)]
    public async Task RephraseOfATerminalIntentIsRefused(IntentState terminal)
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        if (terminal == IntentState.Done)
        {
            foreach (var to in new[] { IntentState.Rephrased, IntentState.Confirmed, IntentState.Dispatched, IntentState.Done })
            {
                if (to == IntentState.Rephrased) { await intents.Rephrase(id, Brief, 0.9, (await intents.Read()).Revision); }
                else { await intents.Transition(id, to, "step", (await intents.Read()).Revision); }
            }
        }
        else { await intents.Transition(id, IntentState.Abandoned, "no", (await intents.Read()).Revision); }
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await intents.Rephrase(id, Brief, 0.5, (await intents.Read()).Revision));
    }

    [Fact]
    public async Task ABlankRequestIdIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
        var blank = await Assert.ThrowsAsync<ArgumentException>(() => intents.Declare(new(" ", "ship it", "conv/a")));
        Assert.Contains("request id", blank.Message);
        var long_ = await Assert.ThrowsAsync<ArgumentException>(() => intents.Declare(new(new string('r', IIntents.RequestIdLimit + 1), "ship it", "conv/a")));
        Assert.Contains("request id", long_.Message);
        Assert.Contains("200", long_.Message);
        Assert.Empty((await intents.Read()).Intents);
    }

    [Fact]
    public async Task AnOverlongHeardIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => intents.Declare(new("req-1", new string('h', IIntents.HeardLimit + 1), "conv/a")));
        Assert.Contains("heard text", refused.Message);
        Assert.Contains("8000", refused.Message);
        Assert.Empty((await intents.Read()).Intents);
    }

    [Fact]
    public async Task AnUnknownContinuesIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => intents.Declare(new("req-1", "ship it", "conv/a", Guid.NewGuid())));
        Assert.Contains("to continue does not exist", refused.Message);
        Assert.Empty((await intents.Read()).Intents);
    }

    [Fact]
    public async Task AnUnknownIntentIdIsRefusedOnRephrase()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
        var refused = await Assert.ThrowsAsync<ArgumentException>(async () => await intents.Rephrase(Guid.NewGuid(), Brief, 0.9, (await intents.Read()).Revision));
        Assert.Equal("id", refused.ParamName);
        Assert.Contains("does not exist", refused.Message);
    }

    [Theory]
    [InlineData(IntentState.Done)]
    [InlineData(IntentState.Abandoned)]
    public async Task ArmingATerminalIntentIsRefused(IntentState terminal)
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        if (terminal == IntentState.Done)
        {
            await intents.Rephrase(id, Brief, 0.9, (await intents.Read()).Revision);
            foreach (var to in new[] { IntentState.Confirmed, IntentState.Dispatched, IntentState.Done }) { await intents.Transition(id, to, "step", (await intents.Read()).Revision); }
        }
        else { await intents.Transition(id, IntentState.Abandoned, "no", (await intents.Read()).Revision); }
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () => await intents.Arm(id, [], (await intents.Read()).Revision));
        Assert.Contains(terminal.ToString(), refused.Message);
    }

    [Fact]
    public async Task AConfidenceOutsideZeroToOneIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        foreach (var bad in new[] { -0.1, 1.1, double.NaN })
        {
            var refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await intents.Rephrase(id, Brief, bad, (await intents.Read()).Revision));
            Assert.Equal("confidence", refused.ParamName);
            Assert.Equal(bad, (double)refused.ActualValue!);
        }
        Assert.Equal(IntentState.Heard, Assert.Single((await intents.Read()).Intents).State);
    }

    [Fact]
    public async Task ConfidenceAtTheBoundsIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
        var id = await intents.Declare(new("req-1", "ship it", "conv/a"));
        await intents.Rephrase(id, Brief, 0, (await intents.Read()).Revision);
        Assert.Equal(0, Assert.Single((await intents.Read()).Intents).Confidence);
        await intents.Rephrase(id, Brief, 1, (await intents.Read()).Revision);
        Assert.Equal(1, Assert.Single((await intents.Read()).Intents).Confidence);
    }

    [Fact]
    public async Task TheCapLeavesTheCountAtTwoHundredFiftySix()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var intents = host.Brain.Get<IIntents>(IIntents.Key(person.BrainId));
        for (var i = 0; i < IIntents.ActiveCap; i++) { await intents.Declare(new($"req-{i}", "ship it", "conv/a")); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => intents.Declare(new("req-over", "ship it", "conv/a")));
        Assert.Equal(256, (await intents.Read()).Intents.Length);
    }
}
