using System.Collections.Concurrent;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.GitHub.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

public interface IScriptedConversationDriver
{
    Task Finish(string runId, string text, string reply);
}

public sealed record ScriptedConversationState(long Revision = 0, AgentRun? Active = null, AgentRun[]? Runs = null)
{
    public ScriptedConversationState() : this(0, null, []) { }
}

// The flow conversation of the facts: Say records an active run and defers completion (as ConversationNeuron does),
// publishing ConversationChanged on both transitions and recording the prompt keyed by flow key.
[DigitalBrain.Neurons.Neuron(Alias)]
public sealed class ScriptedConversation : DigitalBrain.Neurons.Neuron<ScriptedConversationState>, IConversation, IScriptedConversationDriver
{
    // Not "conversation": the AI module's ConversationNeuron owns that type, and fixtures load both.
    public const string Alias = "test.scripted-conversation";

    internal static readonly ConcurrentDictionary<string, string> Prompts = new(StringComparer.Ordinal);

    // The reply to a prompt that starts with the key; facts of different flow types run in parallel, each with its own prefix.
    internal const string Fail = "!fail";

    internal static readonly ConcurrentDictionary<string, string> Replies = new(StringComparer.Ordinal);

    private ConversationId ConvId => ConversationId.Parse(Id.Key.Replace('/', '-'));

    public Task Seat(string agentId, AgentDefinition definition) => Task.CompletedTask;

    public async Task Say(string text, ModelId? model = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var flowKey = Id.Key.StartsWith("flow-conv/", StringComparison.Ordinal) ? Id.Key["flow-conv/".Length..] : Id.Key;
        Prompts[flowKey] = text;
        // The longest matching prefix wins; the reply Fail records a Failed run instead.
        var reply = Replies.Where(entry => text.StartsWith(entry.Key, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.Key.Length)
            .Select(entry => entry.Value)
            .FirstOrDefault() ?? "{}";
        var runId = Guid.NewGuid().ToString("N");
        var active = new AgentRun(runId, AgentRunStatus.Running, text, DateTimeOffset.UtcNow);
        var revision = State.Revision + 1;
        await Save(State with { Revision = revision, Active = active }, new ConversationChanged(ConvId, revision));
        Context.Defer(() => Context.Get<IScriptedConversationDriver>(Id).Finish(runId, text, reply));
    }

    public async Task Finish(string runId, string text, string reply)
    {
        var now = DateTimeOffset.UtcNow;
        var finished = reply == Fail
            ? new AgentRun(runId, AgentRunStatus.Failed, text, now, now, Error: "the model is unavailable")
            : new AgentRun(runId, AgentRunStatus.Completed, text, now, now, Reply: reply);
        var revision = State.Revision + 1;
        var existing = State.Runs ?? [];
        await Save(
            State with { Revision = revision, Active = null, Runs = [.. existing, finished] },
            new ConversationChanged(ConvId, revision));
    }

    public Task Stop() => Task.CompletedTask;

    public Task Regenerate(EntryId reply) => Task.CompletedTask;

    public Task<ConversationView> Read(long? sinceRevision = null)
    {
        var runs = State.Runs ?? [];
        return Task.FromResult(new ConversationView(ConvId, State.Revision, [], [], State.Active, [.. runs], false, []));
    }

    public Task Post(string text, string id) => Task.CompletedTask;

    public Task Voiced(string heard, string reply, ModelId model, string id) => Task.CompletedTask;
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
[DigitalBrain.Neurons.Neuron("github")]
public sealed class StubRepository : DigitalBrain.Neurons.Neuron<StubRepositoryState>, IRepository, IRepositoryScript
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
    protected override IReadOnlyList<Type> Modules => [.. CatalogFamily.Modules, typeof(FlowsFactsModule)];

    protected override void ConfigureSilo(IServiceCollection services)
    {
        services.AddJournalPayloads(typeof(IFlows).Assembly);
        services.AddJournalPayloads(typeof(AppsModule).Assembly);
        services.AddJournalPayloads(typeof(IntoChat.OS.Modules.AI.Agents.Context.Signals.ContextUpdated).Assembly);
        services.AddJournalPayloads(typeof(FlowStarted).Assembly);
        services.AddJournalPayloads(typeof(TestFlow).Assembly);
        services.AddJournalPayloads(typeof(ConversationChanged).Assembly);
        services.AddJournalPayloads(typeof(PullRequestChanged).Assembly);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new AgentStepOptions { ConversationType = ScriptedConversation.Alias });
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
