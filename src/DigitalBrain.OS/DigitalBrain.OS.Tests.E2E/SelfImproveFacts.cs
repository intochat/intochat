using System.Net.Http.Json;
using DigitalBrain.Apps;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
using DigitalBrain.Microsoft.Aspire;

namespace DigitalBrain.OS.Tests.E2E;

// The loop end to end as the signed-in user: the shell posts a change set, the candidate is built and run
// beside the live cluster, the brain verifies it over its MCP, and the list shows Promoted. It needs the
// docker candidate host and the Aspire CLI cluster host (set DIGITALBRAIN_E2E_CANDIDATES=1 where both exist).
[Collection("Candidate host")]
public sealed class SelfImproveFacts
{
    [Fact(Timeout = 1_800_000)]
    public async Task AChangeSetIsProposedVerifiedAndPromotedAsACandidate()
    {
        if (Environment.GetEnvironmentVariable("DIGITALBRAIN_E2E_CANDIDATES") != "1")
        {
            Assert.Skip("Set DIGITALBRAIN_E2E_CANDIDATES=1 on a machine with docker and the Aspire CLI.");
        }
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new SelfImproveHostFixture();
        var brain = await fixture.LeaseAsync(ct);
        var scope = "workspace-" + Guid.NewGuid().ToString("N");
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
                spans = Array.Empty<string>(),
                budget = "00:10:00",
            },
        };
        using var requested = await brain.HttpClient.PostAsJsonAsync($"/brains/{scope}/apps/selfimprove/request",
            new { operation = "request", input = System.Text.Json.JsonSerializer.Serialize(change) }, ct);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        while (!(await list.Read()).Markdown.Contains("Promoted", StringComparison.Ordinal)) { await changes.NextAsync(ct: ct); }
        var sha = (await brain.Get<IText>(appKey + "/last").Read()).Markdown;
        var candidate = await brain.Get<ICandidate>(ICandidate.Key(scope, sha)).Read();
        Assert.Equal(CandidateStage.Promoted, candidate.Stage);
        Assert.True(candidate.Report!.Passed);
    }
}
