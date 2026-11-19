using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Memory;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

public sealed class SessionJournalFacts
{
    private static readonly BrainId TestBrain = new("session-journal");

    private static IDisposable AsPerson()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", TestBrain));

    [Fact]
    public async Task DailyNumberedSessionsTrackMilestonesFormatMarkdownAndFinalizeInPlace()
    {
        using var person = AsPerson();
        await using var os = TestOS.Local(options => options.Start = new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
        os.Register<SessionJournalNeuron>();

        var journal = os.Get<ISessionJournal>(ISessionJournal.NeuronIdOf(TestBrain));

        var s1 = await journal.OpenSession("conv-a", "Ship working memory neuron", "project:memory-v2");
        Assert.Equal(1, s1.SessionNumber);
        Assert.Equal("2026-11-19", s1.Date);
        Assert.Equal(SessionBlockStatus.Active, s1.Status);

        var updatedS1 = await journal.RecordMilestone(new SessionMilestone
        {
            ConversationId = "conv-a",
            Deliverables = ["WorkingMemoryNeuron.cs — 2.5 KB scratchpad"],
            Decisions = ["Freeze scratchpad per session to preserve prefix cache"],
            OpenThreads = ["Wire UI introspector", "Run unit suite"],
        });
        Assert.Equal(1, updatedS1.SessionNumber);
        Assert.Equal(2, updatedS1.OpenThreads.Length);

        // Resolving an open thread removes it cleanly; a second conversation opens Session 2 on the same day.
        updatedS1 = await journal.RecordMilestone(new SessionMilestone
        {
            ConversationId = "conv-a",
            ResolvedThreads = ["Wire UI introspector"],
        });
        Assert.Equal(["Run unit suite"], updatedS1.OpenThreads);

        var s2 = await journal.OpenSession("conv-b", "Audit knowledge graph");
        Assert.Equal(2, s2.SessionNumber);

        var finalized = await journal.FinalizeSession(sessionNumber: 1, date: "2026-11-19");
        Assert.Equal(SessionBlockStatus.Finalized, finalized.Status);
        Assert.NotNull(finalized.EndedAt);

        var day = await journal.ReadDay("2026-11-19");
        Assert.NotNull(day);
        Assert.Equal(2, day.Sessions.Length);

        var md = day.FormatMarkdown();
        Assert.Contains("## Session 1", md, StringComparison.Ordinal);
        Assert.Contains("### Project\nproject:memory-v2", md.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("### Goal\nShip working memory neuron", md.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("- Freeze scratchpad per session to preserve prefix cache", md, StringComparison.Ordinal);
        Assert.Contains("## Session 2", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturningViewReadsTodayFirstAndFallsBackToPriorDayOnlyWhenTodayIsEmpty()
    {
        using var person = AsPerson();
        await using var os = TestOS.Local(options => options.Start = new DateTimeOffset(2026, 11, 18, 18, 0, 0, TimeSpan.Zero));
        os.Register<SessionJournalNeuron>();

        var journal = os.Get<ISessionJournal>(ISessionJournal.NeuronIdOf(TestBrain));
        await journal.RecordMilestone(new SessionMilestone
        {
            ConversationId = "conv-yesterday",
            Date = "2026-11-18",
            Goal = "Prepare release branch",
            ProjectBrief = "project:release",
            OpenThreads = ["Rotate staging webhook key"],
        });

        // Advance to next day with zero sessions logged today: ReturningView falls back to 2026-11-18.
        var fallback = await journal.ReadReturningView("2026-11-19");
        Assert.True(fallback.IsPriorDay);
        Assert.Equal("2026-11-18", fallback.SourceDate);
        Assert.Equal("project:release", fallback.ActiveProjectBrief);
        Assert.Equal(["Rotate staging webhook key"], fallback.OpenThreads);

        // Once today has a session, ReturningView returns today's log instead of yesterday's.
        await journal.RecordMilestone(new SessionMilestone
        {
            ConversationId = "conv-today",
            Date = "2026-11-19",
            Goal = "Verify release deployment",
            OpenThreads = ["Check production traces"],
        });

        var todayView = await journal.ReadReturningView("2026-11-19");
        Assert.False(todayView.IsPriorDay);
        Assert.Equal("2026-11-19", todayView.SourceDate);
        Assert.Equal(["Check production traces"], todayView.OpenThreads);
    }

    [Fact]
    public async Task FollowAgentAutoCapturesTurnCompletedSignalsAcrossRestart()
    {
        using var person = AsPerson();
        await using var os = TestOS.Local(options => options.Start = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));
        os.Register<SessionJournalNeuron>();
        os.Register<StubTurnPublisherNeuron>();

        var journal = os.Get<ISessionJournal>(ISessionJournal.NeuronIdOf(TestBrain));
        await journal.FollowAgent("conv/alpha");

        var publisher = os.Get<IStubTurnPublisher>(new NeuronId(TestBrain, AgentSignals.NeuronType, "conv/alpha"));
        await publisher.EmitCompleted(
            $"{TestBrain.Value}/conv/alpha",
            "run-1",
            "Deliverable: docs/architecture.md\nDecision: Use concrete arrays in state\nOpen thread: Add browser E2E test");
        await os.Idle();
        await os.Restart();

        var day = await os.Get<ISessionJournal>(ISessionJournal.NeuronIdOf(TestBrain)).ReadDay("2026-11-19");
        Assert.NotNull(day);
        var session = Assert.Single(day.Sessions);
        Assert.Contains(session.Deliverables, d => d.Contains("docs/architecture.md", StringComparison.Ordinal));
        Assert.Contains(session.Decisions, d => d.Contains("concrete arrays", StringComparison.Ordinal));
        Assert.Contains(session.OpenThreads, t => t.Contains("browser E2E", StringComparison.Ordinal));
    }

    public interface IStubTurnPublisher
    {
        Task EmitCompleted(string agentId, string runId, string reply);
    }

    [DigitalBrain.Neurons.Neuron(AgentSignals.NeuronType)]
    private sealed class StubTurnPublisherNeuron : DigitalBrain.Neurons.Neuron, IStubTurnPublisher
    {
        public Task EmitCompleted(string agentId, string runId, string reply)
            => Publish(new TurnCompleted(agentId, runId, reply));
    }
}
