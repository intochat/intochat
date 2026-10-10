using System.Net.Http.Json;
using DigitalBrain.Apps;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
using DigitalBrain.Revisions;

namespace DigitalBrain.OS.Tests.E2E;

// The loop end to end as the signed-in user: the shell posts a change set, the synapses react in the
// brain's session, verification drives the "child" (this brain) over its MCP, and the list shows Promoted.
public sealed class SelfImproveFacts
{
    [Fact(Timeout = 240_000)]
    public async Task AChangeSetIsProposedVerifiedAndPromotedThroughTheLauncher()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new SelfImproveHostFixture();
        var brain = await fixture.LeaseAsync(ct);
        var scope = brain.WorkspaceId;
        var appKey = scope + "/apps/selfimprove";
        Assert.Equal(AppStatus.Live, (await brain.Get<IApp>(appKey).Read()).Status);
        var list = brain.Get<IText>(appKey + "/revisions");
        await using var changes = await brain.Observe<TextChanged>(list, ct);
        var change = new
        {
            branch = "os-runtime",
            summary = "Calculator: add percent",
            files = new Dictionary<string, string> { ["features/os/Calculator/app.cs"] = "// Calculator\n" },
            plan = new
            {
                calls = new[] { new { contract = "DigitalBrain.Apps.IApp", key = appKey, method = "Read", arguments = "[]", expected = "\"Name\":\"selfimprove\"" } },
                healthyResources = new[] { "OperatingSystem" },
                spans = Array.Empty<string>(),
                budget = "00:03:00",
                aspireKey = "DigitalBrain.OS",
            },
        };
        using var requested = await brain.HttpClient.PostAsJsonAsync($"/brains/{scope}/apps/selfimprove/request",
            new { operation = "request", input = System.Text.Json.JsonSerializer.Serialize(change) }, ct);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        while (!(await list.Read()).Markdown.Contains("Promoted", StringComparison.Ordinal)) { await changes.NextAsync(ct: ct); }
        var revision = await brain.Get<IRevision>(IRevision.Key(scope, "e2e0001")).Read();
        Assert.Equal(RevisionStage.Promoted, revision.Stage);
        Assert.True(revision.Report!.Passed);
        Assert.Equal(["propose", "start e2e0001", "healthy e2e0001", "promote e2e0001"], fixture.Launcher.Calls);
    }
}