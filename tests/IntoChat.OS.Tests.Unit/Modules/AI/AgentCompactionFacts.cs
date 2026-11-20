using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Testing;
using IntoChat.OS.Connections.Platform.Secrets;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.Tabs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

public sealed class AgentCompactionFacts
{
    private static readonly BrainId TestBrain = new("agent-compact");

    private static IDisposable AsPerson()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", TestBrain));

    private static TestOS CreateAgentOs(RecordingChatClient chatClient)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IChatClient>(chatClient);
        services.AddSingleton<IAiCredentials, NoProviders>();
        services.AddSingleton<ISecretRedactor, NoSecrets>();
        services.AddOptions<AIOptions>();
        services.AddSingleton<ModelCatalog>();
        var provider = services.BuildServiceProvider();

        var os = TestOS.Local(options =>
        {
            options.Services = provider;
            options.Start = new DateTimeOffset(2026, 11, 19, 15, 0, 0, TimeSpan.Zero);
        });
        os.Register(typeof(AIModule).Assembly);
        os.Register(typeof(UiModule).Assembly);
        return os;
    }

    private static async Task RunTurnToCompletionAsync(IAgent agent, string text, CancellationToken ct)
    {
        await foreach (var _ in agent.Run(AgentTurn.Text(text), ct))
        {
        }
    }

    [Fact]
    public async Task AutoCompactionThresholdSummarizesEvictedTurnsPreservesRememberMessagesAndWritesSummaryContext()
    {
        using var person = AsPerson();
        var ct = TestContext.Current.CancellationToken;
        var client = new RecordingChatClient(req => $"Reply #{req}");
        await using var os = CreateAgentOs(client);

        var agent = os.Get<IAgent>(new NeuronId(TestBrain, "agent", "conversations/threshold"));
        await agent.Configure(new AgentDefinition
        {
            CompactionThreshold = 6,
            CompactionRecentWindow = 2,
            Contexts = ["context/conversation.summary"],
        }, 0);

        await RunTurnToCompletionAsync(agent, "Please remember: always build single csproj in Release.", ct);
        await RunTurnToCompletionAsync(agent, "Inspect module boundaries", ct);
        await RunTurnToCompletionAsync(agent, "Add session journal milestones", ct);
        // After 3 turns -> 6 messages (at threshold). 4th turn -> 8 messages (> 6), triggering auto-compaction!
        await RunTurnToCompletionAsync(agent, "Verify unit test suite", ct);

        var state = await agent.Read();
        var transcript = await agent.Transcript();

        Assert.NotNull(state.Summary);
        Assert.Contains("Inspect module boundaries", state.Summary, StringComparison.Ordinal);
        Assert.True(transcript.Length < 8, $"Expected transcript to compact below 8 messages, got {transcript.Length}.");
        // Non-reducible "remember" message is retained in kept messages!
        Assert.Contains(transcript, m => m.Role == "user" && m.Content.OfType<AiText>().Any(t => t.Text.Contains("Please remember", StringComparison.Ordinal)));

        var summaryCtx = await os.Get<IAgentContext>(new NeuronId(TestBrain, "agent-context", "context/conversation.summary")).Read();
        var summaryEntry = Assert.Single(summaryCtx.Entries, e => e.Name == "summary");
        Assert.StartsWith("Earlier conversation summary: ", summaryEntry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlashCompactAndCompactApiSummarizeImmediatelyAndUseModelSummarizerWhenEnabled()
    {
        using var person = AsPerson();
        var ct = TestContext.Current.CancellationToken;
        var client = new RecordingChatClient(prompt =>
            prompt.Contains("Summarize this conversation history concisely", StringComparison.Ordinal)
                ? "LLM_COMPACTED_SUMMARY: Built WorkingMemory, SessionJournal, and UiIntrospector."
                : "Deliverable: WorkingMemoryNeuron.cs\nDecision: Keep startup pack under 2.5 KB");
        await using var os = CreateAgentOs(client);

        var agent = os.Get<IAgent>(new NeuronId(TestBrain, "agent", "conversations/slash-compact"));
        await agent.Configure(new AgentDefinition
        {
            CompactionThreshold = 40,
            CompactionRecentWindow = 20,
            UseModelSummarizer = true,
            Contexts = ["context/conversation.summary", "context/session.journal"],
        }, 0);

        await RunTurnToCompletionAsync(agent, "Implement Track 1 context and memory neurons", ct);
        await RunTurnToCompletionAsync(agent, "Wire live UI introspection", ct);

        Assert.Equal(4, (await agent.Transcript()).Length);

        // Execute /compact with focus instructions directly as a slash turn on AgentNeuron.
        var events = new List<AgentTurnEvent>();
        await foreach (var ev in agent.Run(AgentTurn.Text("/compact focus on memory architecture"), ct))
        {
            events.Add(ev);
        }

        Assert.Contains(events.OfType<AgentTurnEvent.TextChunk>(), chunk => chunk.Content.Contains("Compacted 4 of 4 message(s)", StringComparison.Ordinal));
        var afterSlash = await agent.Read();
        Assert.Equal("LLM_COMPACTED_SUMMARY: Built WorkingMemory, SessionJournal, and UiIntrospector.", afterSlash.Summary);
        Assert.Empty(await agent.Transcript());

        // Add one more turn and invoke IAgent.Compact() API directly.
        await RunTurnToCompletionAsync(agent, "Run final verification", ct);
        var apiCompacted = await agent.Compact("preserve verification outcome");
        Assert.Equal(2, apiCompacted.PreviousMessageCount);
        Assert.Equal(0, apiCompacted.KeptMessageCount);
        Assert.Contains("LLM_COMPACTED_SUMMARY", apiCompacted.Summary, StringComparison.Ordinal);

        // SessionJournal also recorded both the turn milestones and compaction decisions!
        var day = await os.Get<ISessionJournal>(ISessionJournal.NeuronIdOf(TestBrain)).ReadDay("2026-11-19");
        Assert.NotNull(day);
        var session = Assert.Single(day.Sessions);
        Assert.Equal("Implement Track 1 context and memory neurons", session.Goal);
        Assert.Contains(session.Deliverables, d => d.Contains("WorkingMemoryNeuron.cs", StringComparison.Ordinal));
        Assert.Contains(session.Decisions, d => d.Contains("Compacted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HydratesWorkingMemoryLazyProjectBriefSessionJournalAndUiIntrospectorIntoTurnSystemPrompt()
    {
        using var person = AsPerson();
        var ct = TestContext.Current.CancellationToken;
        var client = new RecordingChatClient(_ => "All context slices received.");
        await using var os = CreateAgentOs(client);

        var wm = os.Get<IWorkingMemory>(IWorkingMemory.NeuronIdOf(TestBrain));
        await wm.SetSlice(WorkingMemorySliceKind.Soul, "SOUL_MARKER_CONCISE");
        await wm.UpsertScratchpad(WorkingMemorySection.ActiveThreads, "THREAD_MARKER_WORKTREE");
        await wm.PutBrief("project:alpha", "Alpha Spec", "project", "LAZY_ALPHA_BRIEF_BODY");
        await wm.PutBrief("project:unreferenced", "Other Spec", "project", "UNREFERENCED_BRIEF_MUST_NOT_DUMP");

        var journal = os.Get<ISessionJournal>(ISessionJournal.NeuronIdOf(TestBrain));
        await journal.RecordMilestone(new SessionMilestone
        {
            ConversationId = "prior",
            Date = "2026-11-18",
            Goal = "Bootstrap alpha",
            ProjectBrief = "project:alpha",
            OpenThreads = ["OPEN_THREAD_MARKER_PR"],
        });

        var tabs = os.Get<ITabs>(new NeuronId(TestBrain, "ui.tabs", "workspace/tabs"));
        await tabs.Configure([new UiTabItem("plan", "Plan"), new UiTabItem("diff", "Diff")], selectedId: "diff");
        var introspector = os.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(TestBrain));
        await introspector.TrackNeuron("ui.tabs", "workspace/tabs");

        var agent = os.Get<IAgent>(new NeuronId(TestBrain, "agent", "conversations/hydrated"));
        await agent.Configure(new AgentDefinition
        {
            Contexts =
            [
                "context/working.memory",
                "context/session.journal",
                "context/ui.state",
            ],
        }, 0);

        await RunTurnToCompletionAsync(agent, "What is the current workspace status?", ct);

        var lastRequest = Assert.Single(client.Requests);
        var systemText = string.Join("\n", lastRequest.Where(m => m.Role == ChatRole.System).Select(m => m.Text));

        Assert.Contains("SOUL_MARKER_CONCISE", systemText, StringComparison.Ordinal);
        Assert.Contains("THREAD_MARKER_WORKTREE", systemText, StringComparison.Ordinal);
        Assert.Contains("OPEN_THREAD_MARKER_PR", systemText, StringComparison.Ordinal);
        // Active project brief referenced by returning journal IS lazy-loaded, while unreferenced brief is NOT dumped!
        Assert.Contains("LAZY_ALPHA_BRIEF_BODY", systemText, StringComparison.Ordinal);
        Assert.DoesNotContain("UNREFERENCED_BRIEF_MUST_NOT_DUMP", systemText, StringComparison.Ordinal);
        Assert.Contains("workspace/tabs", systemText, StringComparison.Ordinal);
    }

    private sealed class RecordingChatClient(Func<string, string> responder) : IChatClient
    {
        private int _callIndex;
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public ChatClientMetadata Metadata { get; } = new("recording");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            Requests.Add(snapshot);
            var prompt = string.Join("\n", snapshot.Select(m => m.Text));
            var reply = responder(prompt.Length > 0 ? prompt : (++_callIndex).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class NoProviders : IAiCredentials
    {
        public Task<IntegrationSnapshot> StatusOf(string integrationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = integrationId, Status = IntegrationStatus.Unconfigured });

        public Task<long> GenerationOf(string integrationId, CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public Task<ReleasedIntegration> MaterializeAsync(string integrationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string> ReleaseSecretAsync(string integrationId, string field, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string?> BrainKey(string brain, AiProvider provider, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class NoSecrets : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);

        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }
}
