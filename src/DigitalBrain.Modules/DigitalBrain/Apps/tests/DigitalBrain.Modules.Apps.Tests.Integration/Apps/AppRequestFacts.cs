using DigitalBrain.Apps.Signals;
using DigitalBrain.Testing;

using DigitalBrain.Modules.Apps.Tests.Integration;

namespace DigitalBrain.Apps.Tests.Apps;

// A request is a published signal and nothing waits for it: the app's synapses react and the answer
// is neuron state. An operation the manifest does not declare is refused before anything is published.
public sealed class AppRequestFacts(AppsHost host)
{
    private const string Brain = "app-request-facts";

    [Fact]
    public async Task ARequestPublishesAppRequestedWithAFreshId()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var app = brain.Get<IApp>(IApp.Key(Brain, "echo"));
        await app.Create(new AppManifest("Echo", "echoes", [new("open", "")], [], []), "spec");
        await using var requested = await brain.Observe<AppRequested>(app, ct);

        var id = await app.Request("open", "docked");

        var signal = await requested.NextAsync(ct: ct);
        Assert.Equal(id, signal.RequestId);
        Assert.Equal(("open", "docked"), (signal.Operation, signal.Input));
        Assert.NotEqual(Guid.Empty, id);
    }

    [Fact]
    public async Task ARequestForAnUndeclaredOperationIsRefusedAndPublishesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var app = brain.Get<IApp>(IApp.Key(Brain, "strict"));
        await app.Create(new AppManifest("Strict", "", [new("open", "")], [], []), "spec");
        await using var requested = await brain.Observe<AppRequested>(app, ct);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => app.Request("missing", ""));

        Assert.Contains("open", error.Message, StringComparison.Ordinal);
        // The next request is the first one published: the refused one never was.
        var next = await app.Request("open", "after");
        Assert.Equal(next, (await requested.NextAsync(ct: ct)).RequestId);
    }

    [Fact]
    public async Task AnOversizedInputIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var app = brain.Get<IApp>(IApp.Key(Brain, "big"));
        await app.Create(new AppManifest("Big", "", [new("open", "")], [], []), "spec");
        await Assert.ThrowsAsync<ArgumentException>(() => app.Request("open", new string('x', 64 * 1024 + 1)));
    }
}
