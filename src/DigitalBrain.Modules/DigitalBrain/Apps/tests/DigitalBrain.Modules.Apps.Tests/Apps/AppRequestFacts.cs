using DigitalBrain.Apps.Signals;

namespace DigitalBrain.Apps.Tests.Apps;

// A request is a published signal, nothing waits for it: the app's synapses react and the answer is
// neuron state. An operation the manifest does not declare is refused before anything is published.
public sealed class AppRequestFacts
{
    private const string Brain = "app-request-facts";

    [Fact]
    public async Task ARequestPublishesAppRequestedWithAFreshId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "echo"));
        await app.Create(new AppManifest("Echo", "echoes", [new("open", "")], [], []), "spec");
        await using var requested = await brain.SubscribeAsync<AppRequested>(app, ct);
        await using var signals = requested.ReadAllAsync(ct).GetAsyncEnumerator(ct);

        var id = await app.Request("open", "docked");

        Assert.True(await signals.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(id, signals.Current.RequestId);
        Assert.Equal(("open", "docked"), (signals.Current.Operation, signals.Current.Input));
        Assert.NotEqual(Guid.Empty, id);
    }

    [Fact]
    public async Task ARequestForAnUndeclaredOperationIsRefusedAndPublishesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "strict"));
        await app.Create(new AppManifest("Strict", "", [new("open", "")], [], []), "spec");
        await using var requested = await brain.SubscribeAsync<AppRequested>(app, ct);
        await using var signals = requested.ReadAllAsync(ct).GetAsyncEnumerator(ct);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => app.Request("missing", ""));

        Assert.Contains("open", error.Message, StringComparison.Ordinal);
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
        silence.CancelAfter(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signals.MoveNextAsync().AsTask().WaitAsync(silence.Token));
    }

    [Fact]
    public async Task AnOversizedInputIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "big"));
        await app.Create(new AppManifest("Big", "", [new("open", "")], [], []), "spec");
        await Assert.ThrowsAsync<ArgumentException>(() => app.Request("open", new string('x', 64 * 1024 + 1)));
    }
}
