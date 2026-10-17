using System.Net.Http.Json;
using DigitalBrain.AI.Models;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Flutter.Workspace.Signals;

namespace DigitalBrain.OS.Tests.E2E;

// The assistant's send synapse, in the sandbox, asks Jev first: "open calculator" is routed to
// the named calculator.open capability, opens its workspace path, and never starts an agent turn.
[Collection("Router host")]
public sealed class AssistantGateFacts(RouterHostFixture host) : BrainFact(host)
{
    [Fact(Timeout = 300_000)]
    public async Task OpenCalculatorIsDispatchedByJevWithoutAnAgentTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var scope = WorkspaceId;

        var status = Brain.Get<IText>(scope + "/apps/ino/status");
        var workspace = Brain.Get<IWorkspace>(scope);
        await using var changes = await Brain.Observe<TextChanged>(status, ct);
        await using var windows = await Brain.Observe<WorkspaceChanged>(workspace, ct);
        var previous = Host.Model.SystemOneOverride;
        Host.Model.SystemOneOverride = request => RouterHostFixture.Choose(request, "calculator.open", "open", 0.95);
        try
        {
            using var sent = await User.Http.PostAsJsonAsync($"/brains/{scope}/apps/ino/request",
                new { operation = "send", input = "open calculator" }, ct);
            sent.EnsureSuccessStatusCode();

            while (!(await status.Read()).Markdown.StartsWith("Jev: ", StringComparison.Ordinal)) { await changes.NextAsync(ct: ct); }
            Assert.Contains("calculator.open (0.95)", (await status.Read()).Markdown, StringComparison.Ordinal);
            // Navigation is confirmed through the workspace change stream.
            while ((await workspace.Read()).Path[^1] != scope + "/apps/calculator")
            { await windows.NextAsync(ct: ct); }
            Assert.DoesNotContain(LLMModel.All.SelectMany(model => Host.Model.Requests(model.Id)),
                request => RouterHostFixture.Text(request).Contains("You are the user's assistant", StringComparison.Ordinal));
        }
        finally { Host.Model.SystemOneOverride = previous; }
    }
}
