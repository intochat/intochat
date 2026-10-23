using System.Net.Http.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Text.Signals;
using IntoChat.OS.Microsoft.Aspire;

namespace IntoChat.OS.Kernel.Tests.E2E;

// The loop end to end as the signed-in user: the shell posts a change set, the candidate is built and run
// beside the live cluster, the brain verifies it over its MCP, and the list shows Promoted. It needs the
// docker candidate host and the Aspire CLI cluster host (set INTOCHATOS_E2E_CANDIDATES=1 where both exist).
[Collection("Candidate host")]
public sealed class SelfImproveFacts
{
    [Fact(Timeout = 1_800_000)]
    public async Task AChangeSetIsProposedVerifiedAndPromotedAsACandidate()
    {
        if (Environment.GetEnvironmentVariable("INTOCHATOS_E2E_CANDIDATES") != "1")
        {
            Assert.Skip("Set INTOCHATOS_E2E_CANDIDATES=1 on a machine with docker and the Aspire CLI.");
        }
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new SelfImproveHostFixture();
        await using var brain = await fixture.LeaseAsync(ct);
        using var user = await brain.PersonAsync("candidate-" + Guid.NewGuid().ToString("N"), ct);
        using var caller = user.AsCaller();
        var scope = user.BrainId;
        var appKey = scope + "/apps/selfimprove";
        Assert.Equal(AppStatus.Live, (await brain.Get<IApp>(appKey).Read()).Status);
        await AppRuntimeReadiness.WaitAsync(brain, user, ct);
        var list = brain.Get<IText>(appKey + "/revisions");
        await using var changes = await brain.Observe<TextChanged>(list, ct);
        var change = new
        {
            branch = "os-runtime",
            summary = "Calculator: add percent",
            files = new Dictionary<string, string> { ["features/os/Calculator/app.cs"] = "// Calculator\n" },
            plan = new
            {
                calls = new[] { new { contract = "IntoChat.OS.Apps.IApp", key = appKey, method = "Read", arguments = "[]", expected = "\"Name\":\"selfimprove\"" } },
                spans = Array.Empty<string>(),
                budget = "00:10:00",
            },
        };
        using var requested = await user.Http.PostAsJsonAsync($"/brains/{scope}/apps/selfimprove/request",
            new { operation = "request", input = System.Text.Json.JsonSerializer.Serialize(change) }, ct);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        while (!(await list.Read()).Markdown.Contains("Promoted", StringComparison.Ordinal)) { await changes.NextAsync(ct: ct); }
        var sha = (await brain.Get<IText>(appKey + "/last").Read()).Markdown;
        var candidate = await brain.Get<ICandidate>(ICandidate.Key(scope, sha)).Read();
        Assert.Equal(CandidateStage.Promoted, candidate.Stage);
        Assert.True(candidate.Report!.Passed);
    }
}
