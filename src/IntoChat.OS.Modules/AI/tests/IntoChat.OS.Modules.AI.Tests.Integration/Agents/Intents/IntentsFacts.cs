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
}
