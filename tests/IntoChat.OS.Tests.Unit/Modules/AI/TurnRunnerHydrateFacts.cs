using System.Collections.Concurrent;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntoChat.OS.Tests.Unit.Modules.AI;

public sealed class TurnRunnerHydrateFacts
{
    private static readonly BrainId Brain = new("owner");
    private static readonly NeuronId Owner = new(Brain, "agent", "ino");
    private static readonly string[] AllContexts =
    [
        "context/memory.recall",
        "context/session.journal",
        "context/working.memory",
        "context/ui.state",
    ];

    [Fact]
    public async Task Hydrate_RunsIndependentSlicesInParallel_AndWritesDeterministicContext()
    {
        var arrived = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void SignalArrival()
        {
            if (Interlocked.Increment(ref arrived) == 3)
            {
                allStarted.TrySetResult();
            }
        }

        var recallCtx = new RecordingAgentContext();
        var sessionCtx = new RecordingAgentContext();
        var workingCtx = new RecordingAgentContext();
        var syncedUiKeys = new ConcurrentBag<string>();

        var memory = new FakeMemory(async () =>
        {
            SignalArrival();
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            return new MemoryRecallPack(
                OwnerRules: [new MemoryNote("rule-1", "preference", "style", MemoryTrust.Owner, "tone", "Be concise.", 8, DateTimeOffset.UtcNow)],
                RecalledItems: [new MemoryRecallHit("hit-1", "note", "User likes Rust.", 0.9)]);
        });

        var journal = new FakeSessionJournal(async () =>
        {
            SignalArrival();
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            return new SessionReturningView { FormattedMarkdown = "## Returning\nPick up PR 0.", ActiveProjectBrief = "project-303" };
        });

        var working = new FakeWorkingMemory(
            pack: new WorkingMemoryStartupPack { FormattedPrompt = "Working state", Project = null },
            brief: new ContextBrief("project-303", "Item 303", "project", "Zero-error startup and tool cache.", DateTimeOffset.UtcNow));

        var ui = new FakeUiIntrospector(async key =>
        {
            SignalArrival();
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            syncedUiKeys.Add(key);
            return new UiIntrospectionReport();
        });

        var ctx = new StubNeuronContext(new Dictionary<(Type, NeuronId), object>
        {
            [(typeof(IMemory), new NeuronId(Brain, "ai.memory", "memory"))] = memory,
            [(typeof(ISessionJournal), ISessionJournal.NeuronIdOf(Brain))] = journal,
            [(typeof(IWorkingMemory), IWorkingMemory.NeuronIdOf(Brain))] = working,
            [(typeof(IUiIntrospector), IUiIntrospector.NeuronIdOf(Brain))] = ui,
            [(typeof(IAgentContext), new NeuronId(Brain, "agent-context", "context/memory.recall"))] = recallCtx,
            [(typeof(IAgentContext), new NeuronId(Brain, "agent-context", "context/session.journal"))] = sessionCtx,
            [(typeof(IAgentContext), new NeuronId(Brain, "agent-context", "context/working.memory"))] = workingCtx,
        });

        var runner = new TurnRunner(
            ctx,
            Owner,
            new NoopRedactor(),
            TimeProvider.System,
            new ServiceCollection().BuildServiceProvider(),
            NullLogger.Instance);

        await runner.Hydrate(AllContexts, "status");

        Assert.Equal("Owner rules and preferences:\n- [style] tone: Be concise.", recallCtx.Entries["owner-rules"].Text);
        Assert.Equal("- User likes Rust.", recallCtx.Entries["recall"].Text);
        Assert.Equal("## Returning\nPick up PR 0.", sessionCtx.Entries["returning"].Text);
        Assert.Equal("Working state", workingCtx.Entries["startup"].Text);
        Assert.Equal("[Active Brief: Item 303]\nZero-error startup and tool cache.", workingCtx.Entries["project-brief"].Text);
        Assert.Equal(["context/ui.state"], syncedUiKeys.ToArray());
    }

    [Fact]
    public async Task Hydrate_FailureInOneSlice_DoesNotBlockOtherSlices()
    {
        var sessionCtx = new RecordingAgentContext();
        var workingCtx = new RecordingAgentContext();
        var syncedUiKeys = new ConcurrentBag<string>();

        var ctx = new StubNeuronContext(new Dictionary<(Type, NeuronId), object>
        {
            [(typeof(IMemory), new NeuronId(Brain, "ai.memory", "memory"))] = new FakeMemory(() => throw new InvalidOperationException("Memory offline")),
            [(typeof(ISessionJournal), ISessionJournal.NeuronIdOf(Brain))] = new FakeSessionJournal(() => Task.FromResult(new SessionReturningView { FormattedMarkdown = "Session ok" })),
            [(typeof(IWorkingMemory), IWorkingMemory.NeuronIdOf(Brain))] = new FakeWorkingMemory(new WorkingMemoryStartupPack { FormattedPrompt = "Working ok" }, null),
            [(typeof(IUiIntrospector), IUiIntrospector.NeuronIdOf(Brain))] = new FakeUiIntrospector(key => { syncedUiKeys.Add(key); return Task.FromResult(new UiIntrospectionReport()); }),
            [(typeof(IAgentContext), new NeuronId(Brain, "agent-context", "context/memory.recall"))] = new RecordingAgentContext(),
            [(typeof(IAgentContext), new NeuronId(Brain, "agent-context", "context/session.journal"))] = sessionCtx,
            [(typeof(IAgentContext), new NeuronId(Brain, "agent-context", "context/working.memory"))] = workingCtx,
        });

        var runner = new TurnRunner(
            ctx,
            Owner,
            new NoopRedactor(),
            TimeProvider.System,
            new ServiceCollection().BuildServiceProvider(),
            NullLogger.Instance);

        await runner.Hydrate(AllContexts, "status");

        Assert.Equal("Session ok", sessionCtx.Entries["returning"].Text);
        Assert.Equal("Working ok", workingCtx.Entries["startup"].Text);
        Assert.Equal(["context/ui.state"], syncedUiKeys.ToArray());
    }

    private sealed class NoopRedactor : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken = default) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
    }

    private sealed class RecordingAgentContext : IAgentContext
    {
        public ConcurrentDictionary<string, ContextEntry> Entries { get; } = new(StringComparer.Ordinal);
        public Task<ContextSnapshot> Read() => Task.FromResult(new ContextSnapshot(Entries.Count, [.. Entries.Values]));
        public Task Set(ContextEntry entry) { Entries[entry.Name] = entry; return Task.CompletedTask; }
        public Task Remove(string name) { Entries.TryRemove(name, out _); return Task.CompletedTask; }
        public Task<bool> Upsert(ContextEntry entry) { Entries[entry.Name] = entry; return Task.FromResult(true); }
        public Task<bool> Withdraw(string name) => Task.FromResult(Entries.TryRemove(name, out _));
    }

    private sealed class FakeMemory(Func<Task<MemoryRecallPack>> onRecall) : IMemory
    {
        public Task<MemoryRecallPack> RecallPack(string query, string? activeScope = null, int limit = 5) => onRecall();
        public Task<MemorySnapshot> Read() => throw new NotSupportedException();
        public Task<MemoryEpisode> CaptureEpisode(string agent, string summary, int importance = 5) => throw new NotSupportedException();
        public Task<MemoryFact> RememberFact(string subject, string text, int importance = 5) => throw new NotSupportedException();
        public Task<MemoryNote> RememberPreference(string scope, string subject, string body, int importance = 8) => throw new NotSupportedException();
        public Task<MemoryNote> PromoteNote(string noteId) => throw new NotSupportedException();
        public Task<MemoryGraphEdge> LinkEntities(string sourceEntity, string relation, string targetEntity, double weight = 1.0) => throw new NotSupportedException();
        public Task<MemoryRecallHit[]> Recall(string query, int limit = 5) => throw new NotSupportedException();
    }

    private sealed class FakeSessionJournal(Func<Task<SessionReturningView>> onRead) : ISessionJournal
    {
        public Task<SessionReturningView> ReadReturningView(string? todayDate = null) => onRead();
        public Task<SessionJournalBlock> OpenSession(string? conversationId = null, string? goal = null, string? projectBrief = null, string? date = null) => throw new NotSupportedException();
        public Task<SessionJournalBlock> RecordMilestone(SessionMilestone milestone) => throw new NotSupportedException();
        public Task<SessionJournalBlock> FinalizeSession(int? sessionNumber = null, string? date = null, string[]? openThreads = null) => throw new NotSupportedException();
        public Task FollowAgent(string agentKey) => throw new NotSupportedException();
        public Task<SessionJournalDay?> ReadDay(string? date = null) => throw new NotSupportedException();
        public Task<string[]> ListDates() => throw new NotSupportedException();
    }

    private sealed class FakeWorkingMemory(WorkingMemoryStartupPack pack, ContextBrief? brief) : IWorkingMemory
    {
        public Task<WorkingMemoryStartupPack> FreezeSession(string sessionId) => Task.FromResult(pack);
        public Task<ContextBrief?> GetBrief(string key) => Task.FromResult(brief);
        public Task<WorkingMemorySnapshot> Read() => throw new NotSupportedException();
        public Task<WorkingMemoryStartupPack> ReadStartupPack() => throw new NotSupportedException();
        public Task<WorkingMemoryScratchpad> UpsertScratchpad(WorkingMemorySection section, string entry, string? replaceMatching = null) => throw new NotSupportedException();
        public Task<WorkingMemoryScratchpad> RemoveScratchpad(WorkingMemorySection section, string match) => throw new NotSupportedException();
        public Task<WorkingMemoryScratchpad> SetScratchpad(WorkingMemoryScratchpad scratchpad) => throw new NotSupportedException();
        public Task<WorkingMemorySlice> SetSlice(WorkingMemorySliceKind kind, string content) => throw new NotSupportedException();
        public Task<WorkingMemorySlice?> GetSlice(WorkingMemorySliceKind kind) => throw new NotSupportedException();
        public Task<ContextBriefSummary> PutBrief(string key, string title, string category, string content) => throw new NotSupportedException();
        public Task<bool> RemoveBrief(string key) => throw new NotSupportedException();
        public Task<ContextBriefSummary[]> ListBriefs(string? category = null) => throw new NotSupportedException();
    }

    private sealed class FakeUiIntrospector(Func<string, Task<UiIntrospectionReport>> onSync) : IUiIntrospector
    {
        public Task<UiIntrospectionReport> SyncContext(string contextKey = IUiIntrospector.DefaultContextKey) => onSync(contextKey);
        public Task<UiScreenSnapshot?> InspectScreen(string appId) => throw new NotSupportedException();
        public Task<UiScreenSnapshot[]> InspectOpenScreens() => throw new NotSupportedException();
        public Task<UiStateGraphSnapshot> ExploreStateGraph(string stateOrScreenTypeName, int maxStates = 32) => throw new NotSupportedException();
        public Task PublishStateGraph(UiStateGraphSnapshot graph) => throw new NotSupportedException();
        public Task<UiStateGraphSnapshot[]> ListStateGraphs() => throw new NotSupportedException();
    }

    private sealed class StubNeuronContext(Dictionary<(Type, NeuronId), object> map) : INeuronContext
    {
        public NeuronId Id => Owner;
        public Caller? Caller => null;
        public TimeProvider Time => TimeProvider.System;
        public ITimers Timers => throw new NotSupportedException();
        public IReminders Reminders => throw new NotSupportedException();
        public IWires Wires => throw new NotSupportedException();
        public Task Publish(Signal signal) => Task.CompletedTask;
        public T Get<T>(NeuronId id) where T : class => (T)map[(typeof(T), id)];
        public void Defer(Func<Task> work) => throw new NotSupportedException();
    }
}
