using System.Text.Json;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.GitHub.Signals;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.GitHub;

namespace IntoChat.OS.Tests.Integration.Modules.GitHub;

public sealed class PullRequestReviewFacts
{
    private const string WebhookSecret = "test-github-pr-review-secret-0123456789";

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task PullRequestWebhookPublishesKindHeadShaAndAuthor(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var sha1 = new string('1', 40);
        var sha2 = new string('2', 40);

        await using var os = await JourneyOS.Start(
            kind,
            [typeof(GitHubModule)],
            f => f.GitHub(g => g
                .WebhookSecret(WebhookSecret)
                .Repository("intochat/os", repositoryId: 54321, installationId: 9002)
                .PullRequest("intochat/os", 7, sha1, title: "Add review support", isDraft: false, author: "alice")),
            ct);

        var ada = os.Person("ada");
        var github = os.Fake<FakeGitHub>();
        var repoId = await github.ConnectAsync(os.Runtime, ada.Brain, "intochat/os", ct: ct);

        var openedPayload = JsonSerializer.Serialize(new
        {
            action = "opened",
            number = 7,
            installation = new { id = 9002L },
            repository = new { id = 54321L, name = "os", owner = new { login = "intochat" } },
            pull_request = new { number = 7, user = new { login = "alice" }, head = new { sha = sha1 } },
        });

        var openedResult = await github.DeliverAsync(os.Runtime, ada.Brain, "pull_request", openedPayload, WebhookSecret, ct);
        Assert.Equal(WebhookDeliveryResult.Accepted, openedResult);

        github.PullRequest("intochat/os", 7, sha2, title: "Add review support", isDraft: false, author: "alice");

        var syncPayload = JsonSerializer.Serialize(new
        {
            action = "synchronize",
            number = 7,
            installation = new { id = 9002L },
            repository = new { id = 54321L, name = "os", owner = new { login = "intochat" } },
            pull_request = new { number = 7, user = new { login = "alice" }, head = new { sha = sha2 } },
        });

        var syncResult = await github.DeliverAsync(os.Runtime, ada.Brain, "pull_request", syncPayload, WebhookSecret, ct);
        Assert.Equal(WebhookDeliveryResult.Accepted, syncResult);

        var signals = await os.Runtime.Signals<PullRequestChanged>(repoId, ct);
        Assert.Equal(2, signals.Count);

        Assert.Equal(7, signals[0].Number);
        Assert.Equal(PullRequestKind.Opened, signals[0].Kind);
        Assert.Equal(sha1, signals[0].HeadSha);
        Assert.Equal("alice", signals[0].Author);
        Assert.False(signals[0].IsDraft);

        Assert.Equal(7, signals[1].Number);
        Assert.Equal(PullRequestKind.Updated, signals[1].Kind);
        Assert.Equal(sha2, signals[1].HeadSha);
        Assert.Equal("alice", signals[1].Author);
        Assert.False(signals[1].IsDraft);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task SubmitReviewRecordsReviewInFakeGitHubWhenPermitted(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var headSha = new string('a', 40);

        await using var os = await JourneyOS.Start(
            kind,
            [typeof(GitHubModule)],
            f => f.GitHub(g => g
                .WebhookSecret(WebhookSecret)
                .Repository("intochat/os", repositoryId: 54322, installationId: 9003)
                .PullRequest("intochat/os", 12, headSha, title: "Refactor auth", author: "bob")),
            ct);

        var ada = os.Person("ada");
        var github = os.Fake<FakeGitHub>();
        var repoId = await github.ConnectAsync(os.Runtime, ada.Brain, "intochat/os", ct: ct);

        var request = new SubmitReviewRequest(
            Number: 12,
            HeadSha: headSha,
            Verdict: ReviewVerdict.RequestChanges,
            Body: "Please address the null check.",
            Comments: [new InlineComment("lib/Auth.cs", 42, "Handle null token explicitly.")]);

        var submitted = await ada.Get<IRepository>(repoId).SubmitReview(request, ct);

        Assert.False(string.IsNullOrWhiteSpace(submitted.ReviewId));
        Assert.Equal(12, submitted.Number);
        Assert.Equal(headSha, submitted.HeadSha);
        Assert.Equal(ReviewVerdict.RequestChanges, submitted.Verdict);

        var recorded = Assert.Single(github.SubmittedReviews);
        Assert.Equal(12, recorded.Number);
        Assert.Equal(headSha, recorded.HeadSha);
        Assert.Equal(ReviewVerdict.RequestChanges, recorded.Verdict);
        Assert.Equal("Please address the null check.", recorded.Body);
        var inline = Assert.Single(recorded.Comments);
        Assert.Equal("lib/Auth.cs", inline.Path);
        Assert.Equal(42, inline.Line);
        Assert.Equal("Handle null token explicitly.", inline.Body);
    }
}
