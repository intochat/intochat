using IntoChat.OS.Testing;

using IntoChat.OS.Modules.Apps.Tests.Integration;

namespace IntoChat.OS.Modules.Apps.Tests.Apps;

public sealed class AppSubscriptionFacts(AppsHost host)
{
    private const string Brain = "app-subscription-facts";

    [Fact]
    public async Task OnlyTheNamedOperationsRequestsAreYieldedMatchedOrdinally()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        IIntoChatOS program = brain;
        var app = brain.Get<IApp>(IApp.Key(Brain, "filtered"));
        await app.Create(new AppManifest("Filtered", "", [new("open", ""), new("Open", ""), new("add", "")], [], []), "spec");
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var opens = program.On(app, "open", () => { subscribed.TrySetResult(); return Task.CompletedTask; }, ct).GetAsyncEnumerator(ct);
        var next = opens.MoveNextAsync().AsTask();
        await subscribed.Task.WaitAsync(ct);
        await app.Request("Open", "");
        await app.Request("add", "2+3");
        var id = await app.Request("open", "x");
        Assert.True(await next.WaitAsync(ct));
        Assert.Equal((id, "open", "x"), (opens.Current.RequestId, opens.Current.Operation, opens.Current.Input));
    }
}
