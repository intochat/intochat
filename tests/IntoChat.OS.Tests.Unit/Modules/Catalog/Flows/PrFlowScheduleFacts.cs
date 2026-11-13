using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

public interface IQuietRepository
{
    Task MergeQuietly();
}

public sealed record QuietRepositoryState(bool Merged = false)
{
    public QuietRepositoryState() : this(false) { }
}

[DigitalBrain.Signal("test.quiet-repository.merged")]
public sealed record QuietlyMerged : Signal;

// A repository that opens pull request 7 and can merge it without publishing PullRequestChanged, as a missed signal.
[DigitalBrain.Neurons.Neuron("github")]
public sealed class QuietRepository : DigitalBrain.Neurons.Neuron<QuietRepositoryState>, IRepository, IQuietRepository
{
    public Task<PullRequestView> CreatePullRequest(CreatePullRequest request)
        => Task.FromResult(new PullRequestView(7, request?.Title ?? "", "https://example.test/pr/7", "head", "base", true, false));

    public Task<PullRequestRead> ReadPullRequest(ReadPullRequest query, CancellationToken cancellationToken = default)
        => Task.FromResult(new PullRequestRead(new PullRequestSnapshot(query?.Number ?? 0, "pr", "https://example.test/pr/7", !State.Merged, false, "head", "base",
            State.Merged ? "merge" : null, "head", [], true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "1", "0", 1)));

    public Task MergeQuietly() => Save(State with { Merged = true }, new QuietlyMerged());

    public Task<RepositoryView> Refresh(RefreshRepository request) => throw new NotSupportedException();

    public Task<RepositoryView> Read() => throw new NotSupportedException();

    public Task<PullRequestsRead> ReadPullRequests() => throw new NotSupportedException();

    public Task<ReviewEvidenceRead> ReadReviewEvidence(ReadReviewEvidence query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<RequiredChecksRead> ReadRequiredChecks(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task PushBranch(PushBranchRequest request) => throw new NotSupportedException();

    public Task ReplyToReviewComment(ReplyReviewComment request) => throw new NotSupportedException();

    public Task MergePullRequest(MergePullRequest request) => throw new NotSupportedException();
}

// The merged wait on TestOS's clock: with no PullRequestChanged, the schedule expiry re-reads the pull request.
public sealed class PrFlowScheduleFacts
{
    private static readonly BrainId Brain = new("local");

    [Fact]
    public async Task MergedWaitReconcilesOnSchedule()
    {
        await using var os = TestOS.Local();
        os.Register<PrFlow>().Register<QuietRepository>();
        var repository = new NeuronId(Brain, "github", "quiet");
        var flow = os.Get<IFlowStarter>(new NeuronId(Brain, PrFlow.Alias, "quiet"));
        var inner = new PrInner { Repository = repository.ToString(), Branch = "feature", Title = "Quiet" };

        await flow.StartFromJson("Quiet", "test", null, JsonSerializer.Serialize(inner, PrFlow.Json));
        await os.Idle();
        Assert.Equal(FlowStatus.Waiting, (await flow.Head()).Status);

        // Still open at the first expiry: the wait is extended, not ended.
        os.Clock.Advance(inner.PollEvery);
        await os.Idle();
        var open = await flow.Head();
        Assert.Equal(FlowStatus.Waiting, open.Status);
        Assert.Equal(["open"], open.Done);

        await os.Get<IQuietRepository>(repository).MergeQuietly();
        os.Clock.Advance(inner.PollEvery - TimeSpan.FromMinutes(1));
        await os.Idle();
        Assert.Equal(FlowStatus.Waiting, (await flow.Head()).Status);

        os.Clock.Advance(TimeSpan.FromMinutes(1));
        await os.Idle();
        var done = await flow.Head();
        Assert.Equal(FlowStatus.Completed, done.Status);
        Assert.Equal(["open", "merged"], done.Done);
    }
}
