using System.Net.Http.Json;
using DigitalBrain.AI.Models;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Flutter.Workspace.Signals;

namespace DigitalBrain.OS.Tests.E2E;

// The assistant's send synapse, in the sandbox, asks Jev first: "open calculator" is routed to
// IApp.Request above the gate, dispatched by the router, and never reaches a language-model turn.
public sealed class AssistantGateFacts(RouterHostFixture host) : BrainFact(host)
{
    [Fact(Timeout = 300_000)]
    public async Task OpenCalculatorIsDispatchedByJevWithoutAnAgentTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = WorkspaceId;

        var status = Brain.Get<IText>(scope + "/apps/assistant/status");
        var workspace = Brain.Get<IWorkspace>(scope);
        await using var changes = await Brain.Observe<TextChanged>(status, ct);
        await using var windows = await Brain.Observe<WorkspaceChanged>(workspace, ct);
        Host.Model.SystemOneOverride = request => RouterHostFixture.Choose(request, "apps.app2", "Request", 0.95);

        using var sent = await Brain.HttpClient.PostAsJsonAsync($"/brains/{scope}/apps/assistant/request",
            new { operation = "send", input = "open calculator" }, ct);
        sent.EnsureSuccessStatusCode();

        while (!(await status.Read()).Markdown.StartsWith("Jev: ", StringComparison.Ordinal)) { await changes.NextAsync(ct: ct); }
        Assert.Contains(typeof(DigitalBrain.Apps.IApp).FullName + ".Request (0.95)", (await status.Read()).Markdown, StringComparison.Ordinal);
        // The calculator's own synapse opens the window after the dispatch returns; the wait is on the workspace neuron.
        while (!(await workspace.Read()).Windows.Any(window => window.Reference.NeuronId.EndsWith("/apps/calculator/surface", StringComparison.Ordinal)))
        { await windows.NextAsync(ct: ct); }
        Assert.DoesNotContain(LLMModel.All.SelectMany(model => Host.Model.Requests(model.Id)),
            request => RouterHostFixture.Text(request).Contains("You are the user's assistant", StringComparison.Ordinal));
    }
}
