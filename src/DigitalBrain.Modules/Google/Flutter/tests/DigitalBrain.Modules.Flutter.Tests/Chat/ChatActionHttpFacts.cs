using System.Net;
using DigitalBrain.Flutter.Chat;
using DigitalBrain.Framework;

namespace DigitalBrain.Modules.Flutter.Tests.Chat;

[Collection(FlutterHostCollection.Name)]
public sealed class ChatActionHttpFacts(FlutterHostFixture host)
{
    [Fact]
    public async Task ChatActionEdgeUsesCanonicalPayloadAndRejectsStaleIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        using var person = await host.PersonAsync(ct);
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
