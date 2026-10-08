using System.Net.Http.Json;
using DigitalBrain.Flutter.Workspace;

namespace DigitalBrain.OS.Tests.E2E;

// The whole path: a request is routed through Jev over the contract catalog, dispatched to
// ITimer.Start, and shown as a Timer window. Run headed to watch the Flutter web shell.
public sealed class RouterHostFacts(RouterHostFixture host) : BrainFact(host)
{
    [Fact(Timeout = 300_000)]
    public async Task ARequestRoutesThroughJevAndOpensTheTimerWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = Brain.WorkspaceId;
        _ = await OpenPageAsync(ct);

        using var asked = await Brain.HttpClient.PostAsJsonAsync($"/brains/{scope}/apps/router/ask",
            new { operation = "ask", input = "set a timer for 5 minutes" }, ct);
        asked.EnsureSuccessStatusCode();

        var state = await Brain.Get<IWorkspace>(scope).Read();
        Assert.Contains(state.Windows, window => window.Title == "Timer" || window.Reference.NeuronId.EndsWith("/timer", StringComparison.Ordinal));
    }
}
