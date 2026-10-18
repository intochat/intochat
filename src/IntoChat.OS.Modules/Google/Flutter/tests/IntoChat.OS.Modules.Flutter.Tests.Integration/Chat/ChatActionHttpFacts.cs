using System.Net;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Chat;

public sealed class ChatActionHttpFacts(FlutterHost host)
{
    [Fact]
    public async Task ExplicitReadEdgeAcknowledgesOnlyTheDisplayedVersionAndKeepsActionIdsIndependent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var name = person.BrainId + "/apps/test/read-chat";
        var chat = host.Brain.Get<IChat>(name);
        await chat.Project(new("conversation", 7, 19, [new("answer", ChatRole.Assistant, "hello")])
        { Actions = [new("mark-read", "An ordinary action", "canonical")] });
        Assert.Equal(["answer"], (await chat.Read()).UnreadMessageIds);
        using var stale = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
            new { kind = "chat", name, context = "conversation", revision = 7, read = new { version = 18, messageIds = new[] { "answer" } } }, ct);
        Assert.False(stale.IsSuccessStatusCode);
        Assert.Equal(["answer"], (await chat.Read()).UnreadMessageIds);
        using var response = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
            new { kind = "chat", name, context = "conversation", revision = 7, read = new { version = 19, messageIds = new[] { "answer" } } }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await chat.Read()).UnreadMessageIds);
        Assert.Empty((await chat.Read()).ReadReceipts);
        await using var actions = await host.Brain.Observe<ChatActionActivated>(chat, ct);
        using var action = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
            new { kind = "chat", name, context = "conversation", revision = 7, action = "mark-read" }, ct);
        Assert.Equal(HttpStatusCode.OK, action.StatusCode);
        Assert.Equal("canonical", (await actions.NextAsync(ct: ct)).Payload);
    }

    [Fact]
    public async Task ChatActionEdgeUsesCanonicalPayloadAndRejectsStaleIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        using var person = await host.PersonAsync(ct: ct);
        var ws = person.BrainId;
        using var caller = person.AsCaller();
        var name = ws + "/apps/test/chat";
        var chat = brain.Get<IChat>(name);
        await chat.Project(new("conversation", 7, 19, [])
        { Actions = [new("stop/run-1", "Stop", "canonical")] });
        await using var events = await brain.Observe<ChatActionActivated>(chat, ct);
        using var response = await person.Http.PostAsJsonAsync($"/brains/{ws}/apps/event",
            new { kind = "chat", name, action = "stop/run-1", context = "conversation", revision = 7, value = "forged" }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("canonical", (await events.NextAsync(ct: ct)).Payload);
        using var stale = await person.Http.PostAsJsonAsync($"/brains/{ws}/apps/event",
            new { kind = "chat", name, action = "stop/run-1", context = "conversation", revision = 6 }, ct);
        Assert.False(stale.IsSuccessStatusCode);
        var snapshot = await person.Http.GetStringAsync($"/brains/{ws}/apps/node?kind=chat&name={Uri.EscapeDataString(name)}", ct);
        Assert.Contains("stop/run-1", snapshot);
    }
}
