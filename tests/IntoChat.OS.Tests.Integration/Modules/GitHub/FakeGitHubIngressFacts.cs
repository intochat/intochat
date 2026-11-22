using System.Text.Json;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.GitHub.Signals;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.GitHub;

namespace IntoChat.OS.Tests.Integration.Modules.GitHub;

public sealed class FakeGitHubIngressFacts
{
    private const string WebhookSecret = "test-github-webhook-secret-0123456789";

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task SignedIssuesWebhookDeliveryReachesRepositoryAndPublishesIssueChanged(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await JourneyOS.Start(
            kind,
            [typeof(GitHubModule)],
            f => f.GitHub(g => g
                .WebhookSecret(WebhookSecret)
                .Repository("intochat/os", repositoryId: 12345, installationId: 9001)
                .Issue("intochat/os", 42, "Crash on startup", body: "Steps to reproduce", labels: ["bug"])),
            ct);

        var ada = os.Person("ada");
        var github = os.Fake<FakeGitHub>();
        var repoId = await github.ConnectAsync(os.Runtime, ada.Brain, "intochat/os", ct: ct);

        var payload = JsonSerializer.Serialize(new
        {
            action = "opened",
            installation = new { id = 9001L },
            repository = new { id = 12345L, name = "os", owner = new { login = "intochat" } },
            issue = new { number = 42, title = "Crash on startup" },
        });

        var rejected = await github.DeliverAsync(
            os.Runtime,
            ada.Brain,
            "issues",
            payload,
            "wrong-webhook-secret-9999999999",
            ct);
        Assert.Equal(WebhookDeliveryResult.Unauthorized, rejected);

        var beforeIssues = await ada.Get<IRepository>(repoId).ReadIssues();
        Assert.Empty(beforeIssues.Snapshots);

        var accepted = await github.DeliverAsync(
            os.Runtime,
            ada.Brain,
            "issues",
            payload,
            WebhookSecret,
            ct);
        Assert.Equal(WebhookDeliveryResult.Accepted, accepted);

        var afterIssues = await ada.Get<IRepository>(repoId).ReadIssues();
        var issue = Assert.Single(afterIssues.Snapshots);
        Assert.Equal(42, issue.Number);
        Assert.Equal("Crash on startup", issue.Title);

        var signals = await os.Runtime.Signals<IssueChanged>(repoId, ct);
        var changed = Assert.Single(signals);
        Assert.Equal(42, changed.Number);
        Assert.Equal("opened", changed.Action);
    }
}
