using DigitalBrain;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.AI.Tests;

// A script cannot hold a stream across the edge, so a turn starts with a run id and reports
// itself as signals the script subscribes to; the conversation keeps the transcript.
public sealed class AgentTurnStartFacts
{
    private static void StampUser() => CallerContextStamper.Stamp(new CallerContext
    {
        PrincipalId = "owner",
        AccountId = "owner",
        BrainId = "research",
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    });

    private static Task<ModuleBrain> Start(IChatClient client, CancellationToken ct) => ModuleTest.Create().WithModule<AIModule>()
        .ConfigureSilo(silo => silo.Services.AddSingleton(client)).StartAsync(ct);

    [Fact]
    public async Task StartAnswersARunIdAndPublishesTheTextThenCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(StubChatClient.Replying("hello there"), ct);
        using var signals = brain.SiloServices.GetRequiredService<LocalSignalHub>().Subscribe<Signal>();
        StampUser();
        try
        {
            var scope = BrainScope.CurrentId();
            var execution = brain.Get<IAgentExecution>(scope + "/apps/assistant/execution");

            var runId = await execution.Start(new(scope + "/apps/assistant/conversations/main", "", "Be brief.", [], "hi"));

            var seen = await UntilEnd(signals, runId, ct);
            Assert.IsType<AgentTurnCompleted>(seen[^1]);
            var text = Assert.Single(seen.OfType<AgentTurnProgressed>(), progressed => progressed.Kind == "text");
            Assert.Equal(("hello there", runId, 1), (text.Text, text.RunId, text.Sequence));
            var conversation = await brain.Get<IAgent>(scope + "/apps/assistant/conversations/main").ReadConversation(ct);
            var turn = Assert.Single(conversation.Turns);
            Assert.Equal(("hi", "hello there"), (turn.UserText, turn.AssistantText));
            Assert.Null(conversation.ActiveRunId);
        }
        finally { Orleans.Runtime.RequestContext.Clear(); }
    }

    [Fact]
    public async Task AFailingTurnPublishesFailedAndKeepsTheFailureInTheConversation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(StubChatClient.Failing("provider down"), ct);
        using var signals = brain.SiloServices.GetRequiredService<LocalSignalHub>().Subscribe<Signal>();
        StampUser();
        try
        {
            var scope = BrainScope.CurrentId();
            var execution = brain.Get<IAgentExecution>(scope + "/apps/assistant/execution");

            var runId = await execution.Start(new(scope + "/apps/assistant/conversations/main", "", "", [], "hi"));

            var failed = Assert.IsType<AgentTurnFailed>((await UntilEnd(signals, runId, ct))[^1]);
            Assert.Contains("provider down", failed.Error, StringComparison.Ordinal);
            var turn = Assert.Single((await brain.Get<IAgent>(scope + "/apps/assistant/conversations/main").ReadConversation(ct)).Turns);
            Assert.StartsWith("The turn failed:", turn.AssistantText, StringComparison.Ordinal);
        }
        finally { Orleans.Runtime.RequestContext.Clear(); }
    }

    [Fact]
    public async Task StartRefusesAConversationOutsideTheCallersBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(StubChatClient.Replying("never"), ct);
        StampUser();
        try
        {
            var execution = brain.Get<IAgentExecution>(BrainScope.CurrentId() + "/apps/assistant/execution");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                execution.Start(new("workspace-" + new string('0', 64) + "/conversations/main", "", "", [], "hi")));
        }
        finally { Orleans.Runtime.RequestContext.Clear(); }
    }

    // One app, two conversations: a turn still running in one must not hold up starting the other.
    [Fact]
    public async Task StartingATurnIsNotHeldUpByAnotherConversationsRunningTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubChatClient(async (_, _, _) =>
        {
            await release.Task;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"));
        });
        await using var brain = await Start(client, ct);
        StampUser();
        try
        {
            var scope = BrainScope.CurrentId();
            var execution = brain.Get<IAgentExecution>(scope + "/apps/assistant/execution");
            await execution.Start(new(scope + "/apps/assistant/conversations/one", "", "", [], "first"));
            await Task.Delay(300, ct);

            var second = execution.Start(new(scope + "/apps/assistant/conversations/two", "", "", [], "second"));

            Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5), ct)));
            release.TrySetResult();
            Assert.NotEmpty(await second);
        }
        finally { release.TrySetResult(); Orleans.Runtime.RequestContext.Clear(); }
    }

    // A second message while the conversation's turn still runs is refused, never interleaved.
    [Fact]
    public async Task ASecondTurnInTheSameConversationIsRefusedWhileOneRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubChatClient(async (_, _, _) =>
        {
            await release.Task;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"));
        });
        await using var brain = await Start(client, ct);
        StampUser();
        try
        {
            var scope = BrainScope.CurrentId();
            var execution = brain.Get<IAgentExecution>(scope + "/apps/assistant/execution");
            var conversation = scope + "/apps/assistant/conversations/main";
            await execution.Start(new(conversation, "", "", [], "first"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => execution.Start(new(conversation, "", "", [], "second")))
                .WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        finally { release.TrySetResult(); Orleans.Runtime.RequestContext.Clear(); }
    }

    // Every signal of the run, ending with its completion or failure.
    private static async Task<List<Signal>> UntilEnd(LocalSignalHub.Subscription<Signal> signals, string runId, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var seen = new List<Signal>();
        await foreach (var signal in signals.Reader.ReadAllAsync(deadline.Token))
        {
            switch (signal)
            {
                case AgentTurnProgressed progressed when progressed.RunId == runId: seen.Add(signal); break;
                case AgentTurnCompleted completed when completed.RunId == runId: seen.Add(signal); return seen;
                case AgentTurnFailed failed when failed.RunId == runId: seen.Add(signal); return seen;
            }
        }
        return seen;
    }
}
