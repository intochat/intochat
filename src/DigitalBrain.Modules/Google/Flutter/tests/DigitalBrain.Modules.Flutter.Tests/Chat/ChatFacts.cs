using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Chat;

namespace DigitalBrain.Modules.Flutter.Tests.Chat;

public sealed class ChatFacts
{
    [Fact]
    public async Task ProjectionSurvivesReactivationAndIgnoresDuplicatesAndLateUpdates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<FlutterModule>().StartAsync(ct);
        var chat = brain.Get<IChat>("owner/a/apps/chat/transcript");
        var first = new ChatProjection("owner/a/agents/first", 1, 8, [new("u1", ChatRole.User, "hello")
        { Activities = [new("call", "Query", "select 1", "1")] }]);
        await chat.Project(first);
        await brain.DeactivateAsync(chat, ct);
        Assert.Equal("1", Assert.Single(Assert.Single((await chat.Read()).Projection!.Messages).Activities).Result);
        await chat.Project(first);
        await chat.Project(first with { Version = 7, Messages = [] });
        Assert.Equal(1, (await chat.Read()).Revision);
        await chat.Project(new("owner/a/agents/second", 2, 0, []));
        await brain.DeactivateAsync(chat, ct);
        await chat.Project(first with { Version = 999 });
        var state = await chat.Read();
        Assert.Equal(2, state.Revision);
        Assert.Equal("owner/a/agents/second", state.Projection?.Source);
        Assert.Empty(state.Projection!.Messages);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.Project(first with { Generation = 2, Version = 1 }));
        Assert.Equal(2, (await chat.Read()).Revision);
    }

    [Fact]
    public async Task InvalidMessagesCannotReplaceTheProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<FlutterModule>().StartAsync(ct);
        var chat = brain.Get<IChat>("owner/a/apps/chat/transcript");
        var valid = new ChatProjection("owner/a/agents/first", 1, 1, [new("u1", ChatRole.User, "hello")]);
        await chat.Project(valid);
        await Assert.ThrowsAsync<ArgumentException>(() => chat.Project(valid with { Version = 2, Messages = [valid.Messages[0], valid.Messages[0]] }));
        await Assert.ThrowsAsync<ArgumentException>(() => chat.Project(valid with { Version = -1 }));
        Assert.Equal("hello", Assert.Single((await chat.Read()).Projection!.Messages).Text);
    }
}
