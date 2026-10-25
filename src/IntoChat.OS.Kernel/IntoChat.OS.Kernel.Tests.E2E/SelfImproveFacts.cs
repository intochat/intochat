using System.Net.Http.Json;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.Text.Signals;
using IntoChat.OS.Modules.Microsoft.Aspire;

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
        var calculator = Assert.Single(new FeatureDirectory(SolutionProjects.Features).Load(),
            feature => feature.Owner == "os" && feature.Bundle.Name == "calculator");
        var declarationPath = Path.Combine(SolutionProjects.Features, calculator.Folder, AppFiles.DeclarationPath);
        var original = AppDeclaration.Read(await File.ReadAllTextAsync(declarationPath, ct));
        var marker = "candidate-catalog-" + Guid.NewGuid().ToString("N");
        var catalog = await brain.Get<IAppStore>(IAppStore.Key(scope)).List();
        var card = Assert.Single(catalog, entry => entry.Group == "builtin" && entry.Publisher == "os" && entry.Name == original.Name);
        Assert.Equal(original.Manifest.Title, card.Title);
        Assert.Equal(original.Manifest.Description, card.Description);
        Assert.DoesNotContain(catalog, entry => entry.Description.Contains(marker, StringComparison.Ordinal));
        var version = Version.Parse(original.Version);
        var revised = original with
        {
            Version = new Version(version.Major, version.Minor, checked(version.Build + 1)).ToString(),
            Manifest = original.Manifest with { Description = original.Manifest.Description + " " + marker },
        };
        var list = brain.Get<IText>(appKey + "/revisions");
        // Text is a plain neuron (06-04): its changes arrive through the session synapse neuron once subscribed.
        var subscribed = new TaskCompletionSource();
        await using var changes = brain.On<TextChanged>(list, () => { subscribed.TrySetResult(); return Task.CompletedTask; }, ct).GetAsyncEnumerator(ct);
        var first = changes.MoveNextAsync();
        await subscribed.Task.WaitAsync(ct);
        var change = new
        {
            branch = "os-runtime",
            summary = "Calculator: publish a candidate catalog revision",
            files = new Dictionary<string, string>
            {
                [Path.GetRelativePath(SolutionProjects.Root, declarationPath).Replace('\\', '/')] = AppDeclaration.Write(revised),
            },
            plan = new
            {
                calls = new[] { new { contract = typeof(IAppStore).FullName!, key = IAppStore.Key(scope), method = nameof(IAppStore.List), arguments = "[]", expected = marker } },
                spans = Array.Empty<string>(),
                budget = "00:10:00",
            },
        };
        using var requested = await user.Http.PostAsJsonAsync($"/brains/{scope}/apps/selfimprove/request",
            new { operation = "request", input = System.Text.Json.JsonSerializer.Serialize(change) }, ct);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        for (var more = await first; more; more = await changes.MoveNextAsync())
        {
            var refusal = (await brain.Get<IText>(appKey + "/report").Read()).Markdown;
            Assert.False(refusal.StartsWith("Request refused:", StringComparison.Ordinal), refusal);
            var proposed = (await brain.Get<IText>(appKey + "/last").Read()).Markdown;
            if (proposed.Length > 0)
            {
                var current = await brain.Get<ICandidate>(ICandidate.Key(scope, proposed)).Read();
                Assert.False(current.Stage is CandidateStage.Failed or CandidateStage.Discarded,
                    $"{current.Sha}: {current.Stage}: {current.Failure}");
            }
            if ((await list.Read()).Markdown.Contains("Promoted", StringComparison.Ordinal)) { break; }
        }
        var sha = (await brain.Get<IText>(appKey + "/last").Read()).Markdown;
        var candidate = await brain.Get<ICandidate>(ICandidate.Key(scope, sha)).Read();
        Assert.Equal(CandidateStage.Promoted, candidate.Stage);
        Assert.True(candidate.Report!.Passed);
    }
}
