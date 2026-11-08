using System.Collections.Concurrent;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.GitHub.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

// The flow agent of the facts: Start answers at once with the reply Replies holds for the prompt, as TurnCompleted, and records the prompt.
[DigitalBrain.Neuron("agent")]
public sealed class ScriptedAgent : DigitalBrain.Neuron<PumpState>, IAgent
{
    internal static readonly ConcurrentDictionary<string, string> Prompts = new(StringComparer.Ordinal);

    // The reply to a prompt that starts with the key; facts of different flow types run in parallel, each with its own prefix.
    internal const string Fail = "!fail";

    internal static readonly ConcurrentDictionary<string, string> Replies = new(StringComparer.Ordinal);

    public Task Configure(AgentDefinition definition, long expectedRevision) => Task.CompletedTask;

    public IAsyncEnumerable<AgentTurnEvent> Run(AgentTurn turn, CancellationToken ct = default) => throw new NotSupportedException();

    public async Task<string> Start(AgentTurn turn, string? flowId = null)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var prompt = string.Concat(turn.Message.Content.OfType<AiText>().Select(text => text.Text));
        var runId = turn.RunId ?? Guid.NewGuid().ToString("N");
        Prompts[flowId ?? runId] = prompt;
        // The longest matching prefix wins; the reply Fail publishes TurnFailed instead.
        var reply = Replies.Where(entry => prompt.StartsWith(entry.Key, StringComparison.Ordinal)).OrderByDescending(entry => entry.Key.Length).Select(entry => entry.Value).FirstOrDefault() ?? "{}";
        await Save(State with { Sent = State.Sent + 1 }, reply == Fail
            ? new TurnFailed(Id.ToString(), runId, "the model is unavailable", false)
            : new TurnCompleted(Id.ToString(), runId, reply));
        return runId;
    }

    public Task Cancel() => Task.CompletedTask;

    public Task CancelRun(string runId) => Task.CompletedTask;

    // Configured already: the flow does not configure it again.
    public Task<AgentState> Read() => Task.FromResult(new AgentState(1, new AgentDefinition(), null, null, []));

    public Task<AiMessage[]> Transcript() => Task.FromResult<AiMessage[]>([]);

    public Task Clear() => Task.CompletedTask;

    public Task Post(string text, string id) => Task.CompletedTask;
}

public interface IRepositoryScript
{
    Task Merge(int number);

    Task<int> Created();

    Task Close(int number);
}

public sealed record StubRepositoryState(int Created = 0, bool Merged = false, bool Closed = false)
{
    public StubRepositoryState() : this(0, false, false) { }
}

// A GitHub repository of the facts: CreatePullRequest answers number 7; Merge closes it with a merge sha and publishes PullRequestChanged.
[DigitalBrain.Neuron("github")]
public sealed class StubRepository : DigitalBrain.Neuron<StubRepositoryState>, IRepository, IRepositoryScript
{
    public const int Number = 7;

    public async Task<PullRequestView> CreatePullRequest(CreatePullRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Save(State with { Created = State.Created + 1 }, new PullRequestChanged(Number, "1:0", $"github:1:{Number}"));
        return new PullRequestView(Number, request.Title, "https://example.test/pr/7", "head", "base", true, false);
    }

    public Task<PullRequestRead> ReadPullRequest(ReadPullRequest query, CancellationToken cancellationToken = default)
        => Task.FromResult(new PullRequestRead(new PullRequestSnapshot(query.Number, "pr", "https://example.test/pr/7", !State.Merged && !State.Closed, false, "head", "base",
            State.Merged ? "merge" : null, "head", [], true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, State.Merged ? "2" : "1", "0", 1)));

    public Task Merge(int number) => Save(State with { Merged = true }, new PullRequestChanged(number, "2:0", $"github:1:{number}"));

    public Task<int> Created() => Task.FromResult(State.Created);

    public Task Close(int number) => Save(State with { Closed = true }, new PullRequestChanged(number, "3:0", $"github:1:{number}"));

    public Task<RepositoryView> Refresh(RefreshRepository request) => throw new NotSupportedException();

    public Task<RepositoryView> Read() => throw new NotSupportedException();

    public Task<PullRequestsRead> ReadPullRequests() => throw new NotSupportedException();

    public Task<ReviewEvidenceRead> ReadReviewEvidence(ReadReviewEvidence query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<RequiredChecksRead> ReadRequiredChecks(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task PushBranch(PushBranchRequest request) => throw new NotSupportedException();

    public Task ReplyToReviewComment(ReplyReviewComment request) => throw new NotSupportedException();

    public Task MergePullRequest(MergePullRequest request) => throw new NotSupportedException();
}

// A silo with the Apps module, the facts' flow types and the stubs above.
public sealed class FlowTypesFixture : BothRuntimesFixture
{
    protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlowsFactsModule)];

    protected override void ConfigureSilo(IServiceCollection services)
    {
        services.AddJournalPayloads(typeof(IFlows).Assembly);
        services.AddJournalPayloads(typeof(AppsModule).Assembly);
        services.AddJournalPayloads(typeof(FlowStarted).Assembly);
        services.AddJournalPayloads(typeof(TestFlow).Assembly);
        services.AddJournalPayloads(typeof(TurnCompleted).Assembly);
        services.AddJournalPayloads(typeof(PullRequestChanged).Assembly);
        services.TryAddSingleton(TimeProvider.System);
    }
}

internal static class FlowTypeFacts
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static IFlows Index(INeuronRuntime runtime) => runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

    public static NeuronId Id(string flowId)
    {
        Assert.True(NeuronId.TryParse(flowId, out var id));
        return id;
    }

    public static async Task<FlowView> Eventually(Func<Task<FlowView?>> read, Func<FlowView, bool> done)
    {
        FlowView? view = null;
        for (var i = 0; i < 400; i++)
        {
            view = await read();
            if (view is not null && done(view)) { return view; }
            await Task.Delay(50, Ct);
        }
        Assert.Fail($"The flow did not reach the expected view; last: {view}");
        return view!;
    }
}
