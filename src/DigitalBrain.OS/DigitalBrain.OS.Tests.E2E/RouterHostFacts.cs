using System.Net.Http.Json;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;

namespace DigitalBrain.OS.Tests.E2E;

// The whole path: a request posted to the Router app reaches its synapse, is routed through Jev over
// the contract catalog, dispatched to ITimer.Start, and answered in the result neuron.
public sealed class RouterHostFacts(RouterHostFixture host) : BrainFact(host)
{
    [Fact(Timeout = 300_000)]
    public async Task ARequestRoutesThroughJevAndStartsTheTimer()
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = WorkspaceId;
        _ = await OpenPageAsync(ct);

        var result = Brain.Get<IText>(scope + "/apps/router/result");
        await using var changes = await Brain.Observe<TextChanged>(result, ct);

        using var asked = await Brain.HttpClient.PostAsJsonAsync($"/brains/{scope}/apps/router/request",
            new { operation = "ask", input = "set a timer for 5 minutes" }, ct);
        asked.EnsureSuccessStatusCode();

        // The answer is the result neuron: the router writes it after the route ran, one line per step.
        while ((await result.Read()).Markdown is "" or "Working…") { await changes.NextAsync(ct: ct); }
        var answer = (await result.Read()).Markdown;
        Assert.Contains("• Start", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", answer, StringComparison.Ordinal);
    }
}
