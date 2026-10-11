using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Chat;

namespace DigitalBrain.Modules.Flutter.Tests.Chat;

public sealed class ChatFacts
{
    [Fact]
    public async Task ActionsSurviveReactivationAndStreamingAndDispatchCanonicalPayloads()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var chat = brain.Get<IChat>("owner/a/apps/chat/actions");
        var handler = brain.Get<IPrimitiveTestHandler>("chat-action-handler");
        await brain.Get<IUiBinding>("owner/a/apps/chat/actions").Bind(handler);
        await using var handled = await brain.Observe<UiEventHandled>(handler, ct);
        var projection = new ChatProjection("first", 1, 1, [])
        { Actions = [new("run-1", "Stop", "canonical run-1")] };
        await chat.Project(projection);
        await brain.DeactivateAsync(chat, ct);
        Assert.Equal(projection.Actions, (await chat.Read()).Projection!.Actions);
        await chat.Project(projection with { Version = 2 });
        Assert.Empty(await handler.Events());
        await using var actions = await brain.Observe<ChatActionActivated>(chat, ct);
        await chat.Activate("first", 1, "run-1");
        var activated = await actions.NextAsync(ct: ct);
        Assert.Equal("canonical run-1", activated.Payload);
        Assert.Equal("first", activated.Source);
        Assert.Equal(1, activated.Generation);
        Assert.Equal("run-1", activated.ActionId);
        Assert.NotEqual(Guid.Empty, activated.RequestId);
        await PrimitiveBindingFacts.WaitFor(handled, 1, ct);
        var dispatched = Assert.IsType<ChatActionActivated>(Assert.Single(await handler.Events()));
        Assert.Equal(activated.RequestId, dispatched.RequestId);
        Assert.Equal(activated.Payload, dispatched.Payload);
        Assert.Equal(activated.ActionId, dispatched.ActionId);
        Assert.Equal(activated.Source, dispatched.Source);
        Assert.Equal(activated.Generation, dispatched.Generation);
    }

    [Fact]
    public async Task LateUnknownAndDisabledActionsCannotTargetANewerRunOrConversation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var chat = brain.Get<IChat>("owner/a/apps/chat/stale");
        var projection = new ChatProjection("first", 1, 1, [])
        { Actions = [new("run-1", "Stop", "one")] };
        await chat.Project(projection);
        await chat.Project(projection with { Version = 2, Actions = [new("run-2", "Stop", "two")] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.Activate("first", 1, "run-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.Activate("first", 1, "unknown"));
        await chat.Project(projection with { Source = "second", Generation = 2, Actions = [new("run-2", "Stop", "two")] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.Activate("first", 1, "run-2"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.Activate("first", 2, "run-2"));
        await chat.Project(projection with { Source = "second", Generation = 2, Version = 2, Actions = [new("run-2", "Stop", "two", false)] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.Activate("second", 2, "run-2"));
        await chat.Project(projection with { Version = 999 });
        Assert.False(Assert.Single((await chat.Read()).Projection!.Actions).Enabled);
    }

    [Fact]
    public async Task InvalidActionsCannotReplaceTheProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var chat = brain.Get<IChat>("owner/a/apps/chat/invalid-actions");
        var projection = new ChatProjection("first", 1, 1, []);
        await chat.Project(projection);
        foreach (var actions in new ChatAction[][] { [new("same", "A", "a"), new("same", "B", "b")],
            [new("", "A", "a")], [new("id", "", "a")], [new("id", "A", null!)],
            [new("id", "A", new string('x', 32_001))], null! })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => chat.Project(projection with { Version = 2, Actions = actions }));
        }
        Assert.Equal(1, (await chat.Read()).Revision);
    }

    [Fact]
    public async Task ProjectionSurvivesReactivationAndIgnoresDuplicatesAndLateUpdates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
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
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var chat = brain.Get<IChat>("owner/a/apps/chat/transcript");
        var valid = new ChatProjection("owner/a/agents/first", 1, 1, [new("u1", ChatRole.User, "hello")]);
        await chat.Project(valid);
        await Assert.ThrowsAsync<ArgumentException>(() => chat.Project(valid with { Version = 2, Messages = [valid.Messages[0], valid.Messages[0]] }));
        await Assert.ThrowsAsync<ArgumentException>(() => chat.Project(valid with { Version = -1 }));
        Assert.Equal("hello", Assert.Single((await chat.Read()).Projection!.Messages).Text);
    }
}
