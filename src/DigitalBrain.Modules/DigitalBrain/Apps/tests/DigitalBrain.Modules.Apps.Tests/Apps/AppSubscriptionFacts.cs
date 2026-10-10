using DigitalBrain.Apps.Signals;

namespace DigitalBrain.Apps.Tests.Apps;

public sealed class AppSubscriptionFacts
{
    private const string Brain = "app-subscription-facts";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OnlyTheNamedOperationsRequestsAreYielded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        IDigitalBrain program = brain;
        var app = brain.Get<IApp>(IApp.Key(Brain, "filtered"));
        await app.Create(new AppManifest("Filtered", "", [new("open", ""), new("add", "")], [], []), "spec");
        await using var adds = program.On(app, "add", ct).GetAsyncEnumerator(ct);
        var next = adds.MoveNextAsync().AsTask();
        await Task.Delay(300, ct);
        await app.Request("open", "");
        var id = await app.Request("add", "2+3");
        Assert.True(await next.WaitAsync(Wait, ct));
        Assert.Equal((id, "add", "2+3"), (adds.Current.RequestId, adds.Current.Operation, adds.Current.Input));
    }

    [Fact]
    public async Task TheOperationFilterIsOrdinal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        IDigitalBrain program = brain;
        var app = brain.Get<IApp>(IApp.Key(Brain, "cased"));
        await app.Create(new AppManifest("Cased", "", [new("open", ""), new("Open", "")], [], []), "spec");
        await using var lower = program.On(app, "open", ct).GetAsyncEnumerator(ct);
        var next = lower.MoveNextAsync().AsTask();
        await Task.Delay(300, ct);
        await app.Request("Open", "");
        await app.Request("open", "x");
        Assert.True(await next.WaitAsync(Wait, ct));
        Assert.Equal("x", lower.Current.Input);
    }
}
