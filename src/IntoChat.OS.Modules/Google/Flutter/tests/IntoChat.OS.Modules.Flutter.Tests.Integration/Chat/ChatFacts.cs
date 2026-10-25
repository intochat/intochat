using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Chat;

public sealed class ChatFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task ReadReceiptsBelongToTheStampedPersonAndModelCallsCannotAcknowledgeThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        using var scope = brain.Owner.AsCaller();
        var chat = brain.Get<IChat>("owner/apps/chat/people");
        await chat.Project(new("conversation", 1, 1, [new("a1", ChatRole.Assistant, "answer")]));
        await chat.MarkRead("conversation", 1, 1, ["a1"]);
        using var second = await brain.Owner.AddAccountMemberAsync(brain, "second-person");
        using (second.AsCaller())
        {
            Assert.Equal(["a1"], (await chat.Read()).UnreadMessageIds);
            Assert.Empty((await chat.Read()).ReadReceipts);
            await chat.MarkRead("conversation", 1, 1, ["a1"]);
            Assert.Empty((await chat.Read()).UnreadMessageIds);
        }
        using (brain.Owner.AsModelImpersonatingCaller("model-conversation"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.MarkRead("conversation", 1, 1, ["a1"]));
        }
        await brain.DeactivateAsync(chat, ct);
        Assert.Empty((await chat.Read()).UnreadMessageIds);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ExplicitReadAcknowledgementsSurviveReactivationAndDoNotHideNewOrChangedAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        using var caller = brain.Owner.AsCaller();
        var chat = brain.Get<IChat>("owner/apps/chat/read-receipts");
        var projection = new ChatProjection("conversation", 1, 1,
            [new("u1", ChatRole.User, "hello"), new("a1", ChatRole.Assistant, "answer")]);
        await chat.Project(projection);
        Assert.Equal(["a1"], Unread(await chat.Read()));
        await MarkRead(chat, "conversation", 1, 1, ["a1"]);
        var revision = (await chat.Read()).Revision;
        await MarkRead(chat, "conversation", 1, 1, ["a1"]);
        Assert.Equal(revision, (await chat.Read()).Revision);
        await brain.DeactivateAsync(chat, ct);
        Assert.Empty(Unread(await chat.Read()));
        await chat.Project(projection with { Version = 2, Messages = [.. projection.Messages, new("a2", ChatRole.Assistant, "next")] });
        Assert.Equal(["a2"], Unread(await chat.Read()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarkRead(chat, "conversation", 1, 1, ["a2"]));
        await chat.Project(projection with { Version = 3, Messages = [new("a1", ChatRole.Assistant, "corrected")] });
        Assert.Equal(["a1"], Unread(await chat.Read()));
        await MarkRead(chat, "conversation", 1, 3, ["a1"]);
        await chat.Project(new("other", 2, 1, [new("a1", ChatRole.Assistant, "corrected")]));
        Assert.Equal(["a1"], Unread(await chat.Read()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarkRead(chat, "conversation", 1, 3, ["a1"]));
        await chat.Project(new("conversation", 3, 4, [new("a1", ChatRole.Assistant, "corrected")]));
        Assert.Empty(Unread(await chat.Read()));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReadAcknowledgementsCannotBeMadeByBehaviorsOrForUnfinishedAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var chat = brain.Get<IChat>("owner/apps/chat/read-validation");
        await chat.Project(new("conversation", 1, 1,
            [new("a1", ChatRole.Assistant, "partial") { Status = "running" }, new("u1", ChatRole.User, "hello")]));
        using (brain.Owner.AsCaller())
        {
            Assert.Empty(Unread(await chat.Read()));
            await Assert.ThrowsAsync<ArgumentException>(() => MarkRead(chat, "conversation", 1, 1, ["a1"]));
            await Assert.ThrowsAsync<ArgumentException>(() => MarkRead(chat, "conversation", 1, 1, ["unknown"]));
            await Assert.ThrowsAsync<ArgumentException>(() => MarkRead(chat, "conversation", 1, 1, ["u1"]));
        }
        using (brain.Owner.AsApp("owner/apps/chat"))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MarkRead(chat, "conversation", 1, 1, [])); }
    }

    private static string[] Unread(ChatState state) => state.UnreadMessageIds;

    private static Task MarkRead(IChat chat, string source, long generation, long version, string[] ids)
        => chat.MarkRead(source, generation, version, ids);

    [Trait("host", "own")]
    [Fact]
    public async Task ActionsSurviveReactivationAndStreamingAndDispatchCanonicalPayloads()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var chat = brain.Get<IChat>("owner/a/apps/chat/actions");
        var handler = brain.Get<IPrimitiveTestHandler>("owner/chat-action-handler");
        await brain.Get<IUiBinding>("owner/a/apps/chat/actions").Bind(UiHandlers.IdOf(handler));
        await using var handled = await brain.Observe<UiEventHandled>(handler, ct);
        var projection = new ChatProjection("first", 1, 1, [])
        { Actions = [new("run-1", "Stop", "canonical run-1")] };
        await chat.Project(projection);
        await brain.DeactivateAsync(chat, ct);
        Assert.Equal(projection.Actions, (await chat.Read()).Projection!.Actions);
        await chat.Project(projection with { Version = 2 });
        Assert.Empty((await handler.Events()).Decoded());
        await using var actions = await brain.Observe<ChatActionActivated>(chat, ct);
        await chat.Activate("first", 1, "run-1");
        var activated = await actions.NextAsync(ct: ct);
        Assert.Equal("canonical run-1", activated.Payload);
        Assert.Equal("first", activated.Source);
        Assert.Equal(1, activated.Generation);
        Assert.Equal("run-1", activated.ActionId);
        Assert.NotEqual(Guid.Empty, activated.RequestId);
        await PrimitiveBindingFacts.WaitFor(handled, 1, ct);
        var dispatched = Assert.IsType<ChatActionActivated>(Assert.Single((await handler.Events()).Decoded()));
        Assert.Equal(activated.RequestId, dispatched.RequestId);
        Assert.Equal(activated.Payload, dispatched.Payload);
        Assert.Equal(activated.ActionId, dispatched.ActionId);
        Assert.Equal(activated.Source, dispatched.Source);
        Assert.Equal(activated.Generation, dispatched.Generation);
    }

    [Trait("host", "own")]
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

    [Trait("host", "own")]
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

    [Trait("host", "own")]
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

    [Trait("host", "own")]
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
