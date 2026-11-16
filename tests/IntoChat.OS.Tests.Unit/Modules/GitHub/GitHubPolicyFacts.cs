using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Limits;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.GitHub.Tests.Unit;

public sealed class GitHubPolicyFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task Adopt(TestOS os, PolicyRule rule) => os.AdoptPolicy("b", rule, Ct);

    private static Task<(TestOS Os, IRepository Neuron, FakeGitHubSource Source)> CreateHarnessAsync()
    {
        var source = new FakeGitHubSource();
        var services = new ServiceCollection();
        GitHubModule.ConfigureServices(services, new global::Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton<DigitalBrain.Capabilities.Connections.Integrations.IModuleIntegrations<GitHubModule>>(new FakeIntegrations());
        services.AddSingleton<IGitHubRepositorySource>(source);
        var os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register(typeof(GitHubModule).Assembly);
        var id = new NeuronId(new BrainId("b"), "github", "connection/github/main");
        os.SeedStoredState(
            id,
            System.Text.Json.JsonSerializer.Serialize(
                new GitHubRepositoryState
                {
                    Status = ConnectionStatus.Connected,
                    RepositoryId = 12345,
                    Owner = "intochat",
                    Name = "os",
                    Account = "intochat/os",
                },
                DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(GitHubRepositoryState))));

        return Task.FromResult((os, os.Get<IRepository>(id), source));
    }

    private sealed class FakeIntegrations : DigitalBrain.Capabilities.Connections.Integrations.IModuleIntegrations<GitHubModule>
    {
        public DigitalBrain.Capabilities.Connections.Integrations.IIntegrationHandle Get(string integrationId) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ARefusingPolicyStopsTheWriteBeforeTheApiCall()
    {
        var (os, neuron, source) = await CreateHarnessAsync();
        await using var _ = os;
        foreach (var action in (string[])["github.branch.push", "github.pull-request.create", "github.review.reply", "github.pull-request.merge"])
        {
            await Adopt(os, new PolicyRule(
                $"deny-{action}",
                action,
                PolicyMeasure.Count,
                new PolicyWindow(PolicyWindowKind.None, null),
                0m,
                null,
                $"Never {action}",
                os.Clock.GetUtcNow()));
        }

        await Assert.ThrowsAsync<RuleViolationException>(
            () => neuron.PushBranch(new PushBranchRequest("feature-x", "0123456789012345678901234567890123456789")));
        await Assert.ThrowsAsync<RuleViolationException>(
            () => neuron.CreatePullRequest(new CreatePullRequest("Title", "feature-x", "main", "Body", false)));
        await Assert.ThrowsAsync<RuleViolationException>(
            () => neuron.ReplyToReviewComment(new ReplyReviewComment(42, 99, "Fixed")));

        using (RuntimeCallerContext.StampScope(CapabilityCallers.Of("user-1", "acc-1", "b", CallerKind.User)))
        {
            await Assert.ThrowsAsync<RuleViolationException>(
                () => neuron.MergePullRequest(new MergePullRequest(42, "head-sha-1234", "squash")));
        }

        Assert.Equal(0, source.WriteCalls);
    }

    [Fact]
    public async Task AnAllowedWriteIsCountedOnce()
    {
        var (os, neuron, source) = await CreateHarnessAsync();
        await using var _ = os;
        await Adopt(os, new PolicyRule(
            "r1",
            "github.branch.push",
            PolicyMeasure.Count,
            new PolicyWindow(PolicyWindowKind.Day, null),
            1m,
            null,
            "Allow at most 1 count of push a GitHub branch per day",
            os.Clock.GetUtcNow()));

        await neuron.PushBranch(new PushBranchRequest("feature-1", "0123456789012345678901234567890123456789"));
        Assert.Equal(1, source.WriteCalls);

        await Assert.ThrowsAsync<RuleViolationException>(
            () => neuron.PushBranch(new PushBranchRequest("feature-2", "0123456789012345678901234567890123456789")));
        Assert.Equal(1, source.WriteCalls);
    }

    [Fact]
    public async Task AFailedApiCallReleasesTheReservation()
    {
        var (os, neuron, source) = await CreateHarnessAsync();
        await using var _ = os;
        await Adopt(os, new PolicyRule(
            "r1",
            "github.branch.push",
            PolicyMeasure.Count,
            new PolicyWindow(PolicyWindowKind.Day, null),
            1m,
            null,
            "Allow at most 1 count of push a GitHub branch per day",
            os.Clock.GetUtcNow()));

        source.ThrowOnNextWrite = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => neuron.PushBranch(new PushBranchRequest("feature-1", "0123456789012345678901234567890123456789")));

        await neuron.PushBranch(new PushBranchRequest("feature-1", "0123456789012345678901234567890123456789"));
        Assert.Equal(2, source.WriteCalls);

        await Assert.ThrowsAsync<RuleViolationException>(
            () => neuron.PushBranch(new PushBranchRequest("feature-2", "0123456789012345678901234567890123456789")));
    }

    [Fact]
    public void TheManifestDeclaresTheActions()
    {
        var actions = ModuleManifest.Of(typeof(GitHubModule)).Actions;
        Assert.Contains(actions, a => a.Name == "github.branch.push" && a.AmountUnit is null);
        Assert.Contains(actions, a => a.Name == "github.pull-request.create" && a.AmountUnit is null);
        Assert.Contains(actions, a => a.Name == "github.review.reply" && a.AmountUnit is null);
        Assert.Contains(actions, a => a.Name == "github.pull-request.merge" && a.AmountUnit is null);
    }

    private sealed class FakeGitHubSource : IGitHubRepositorySource
    {
        public int WriteCalls { get; private set; }
        public bool ThrowOnNextWrite { get; set; }

        public Task<PullRequestSnapshot> GetPullRequestAsync(GitHubRepository repository, int number, CancellationToken cancellationToken)
            => Task.FromResult(new PullRequestSnapshot(
                number,
                "PR",
                $"https://github.com/intochat/os/pull/{number}",
                true,
                false,
                "head-sha-1234",
                "base-sha-0000",
                null,
                "head-sha-1234",
                [],
                true,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                "rev-1",
                "cirev-1",
                12345,
                "main"));

        public Task<IReadOnlyList<PullRequestSnapshot>> ListOpenPullRequestsAsync(GitHubRepository repository, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PullRequestSnapshot>>([]);

        public Task<GitHubReviewEvidence> GetReviewEvidenceAsync(GitHubRepository repository, PullRequestSnapshot snapshot, CancellationToken cancellationToken)
            => Task.FromResult(new GitHubReviewEvidence(snapshot.HeadSha, snapshot.BaseSha, "", "", true));

        public Task<RequiredChecksRead> GetRequiredChecksAsync(GitHubRepository repository, string? branch, CancellationToken cancellationToken)
            => Task.FromResult(new RequiredChecksRead([], true, null));

        public Task PushBranchAsync(GitHubRepository repository, PushBranchRequest request, CancellationToken cancellationToken)
        {
            WriteCalls++;
            if (ThrowOnNextWrite)
            {
                ThrowOnNextWrite = false;
                throw new InvalidOperationException("Simulated GitHub push failure.");
            }
            return Task.CompletedTask;
        }

        public Task<PullRequestView> CreatePullRequestAsync(GitHubRepository repository, CreatePullRequest request, CancellationToken cancellationToken)
        {
            WriteCalls++;
            if (ThrowOnNextWrite)
            {
                ThrowOnNextWrite = false;
                throw new InvalidOperationException("Simulated GitHub PR creation failure.");
            }
            return Task.FromResult(new PullRequestView(1, request.Title, "https://github.com/intochat/os/pull/1", "head", "base", true, request.Draft, null));
        }

        public Task ReplyToReviewCommentAsync(GitHubRepository repository, ReplyReviewComment request, CancellationToken cancellationToken)
        {
            WriteCalls++;
            if (ThrowOnNextWrite)
            {
                ThrowOnNextWrite = false;
                throw new InvalidOperationException("Simulated GitHub review reply failure.");
            }
            return Task.CompletedTask;
        }

        public Task MergePullRequestAsync(GitHubRepository repository, MergePullRequest request, CancellationToken cancellationToken)
        {
            WriteCalls++;
            if (ThrowOnNextWrite)
            {
                ThrowOnNextWrite = false;
                throw new InvalidOperationException("Simulated GitHub merge failure.");
            }
            return Task.CompletedTask;
        }
    }
}
