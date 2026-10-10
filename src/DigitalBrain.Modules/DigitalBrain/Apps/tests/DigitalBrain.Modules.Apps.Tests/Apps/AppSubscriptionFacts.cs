using DigitalBrain.Apps.Signals;

namespace DigitalBrain.Apps.Tests.Apps;

public sealed class AppSubscriptionFacts
{
    private const string Brain = "app-subscription-facts";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OnlyTheNamedOperationsRequestsAreYieldedMatchedOrdinally()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        IDigitalBrain program = brain;
        var app = brain.Get<IApp>(IApp.Key(Brain, "filtered"));
        await app.Create(new AppManifest("Filtered", "", [new("open", ""), new("Open", ""), new("add", "")], [], []), "spec");
        await using var opens = program.On(app, "open", ct).GetAsyncEnumerator(ct);
        var next = opens.MoveNextAsync().AsTask();
        await Task.Delay(300, ct); // no registration gate on this overload: let the subscription register
        await app.Request("Open", "");
        await app.Request("add", "2+3");
        var id = await app.Request("open", "x");
        Assert.True(await next.WaitAsync(Wait, ct));
        Assert.Equal((id, "open", "x"), (opens.Current.RequestId, opens.Current.Operation, opens.Current.Input));
    }
}
