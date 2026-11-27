using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Conversations;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Conversations;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Conversations;

public sealed class ConversationDecouplingFacts
{
    private static readonly BrainId Brain = new("owner");
    private static readonly NeuronId SessionListId = new(Brain, Threads.Type, IThreadDirectory.DefaultKey);

    private static AgentState EmptyAgentState() => new(0, new AgentDefinition(), null, null, []);

    [Fact]
    public async Task MigrationServiceSkipsNonConversationKeysAndEmptyPrefix()
    {
        var ct = TestContext.Current.CancellationToken;
        var invoked = false;
        var withPrefix = new ConversationMigrationService(new ConversationDefaults("ino", "ino/conversations/"));
        var nonConv = await withPrefix.MigrateLegacyAgentAsync(
            new NeuronId(Brain, IConversation.Type, "flow-conv/abc"),
            new AccountId("owner"),
            _ => { invoked = true; return new StubLegacyAgent(EmptyAgentState()); },
            ct);

        Assert.Equal(ConversationMigrationStatus.NothingToMigrate, nonConv.Status);
        Assert.False(invoked);

        var withoutPrefix = new ConversationMigrationService(new ConversationDefaults("ino", null));
        var noPrefixResult = await withoutPrefix.MigrateLegacyAgentAsync(
            new NeuronId(Brain, IConversation.Type, "conversations/main"),
            new AccountId("owner"),
            _ => { invoked = true; return new StubLegacyAgent(EmptyAgentState()); },
            ct);

        Assert.Equal(ConversationMigrationStatus.NothingToMigrate, noPrefixResult.Status);
        Assert.False(invoked);
    }

    [Fact]
    public async Task MigrationServiceReturnsNothingToMigrateWhenLegacyAgentIsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = new StubLegacyAgent(EmptyAgentState());
        var service = new ConversationMigrationService(new ConversationDefaults("ino", "ino/conversations/"));

        var result = await service.MigrateLegacyAgentAsync(
            new NeuronId(Brain, IConversation.Type, "conversations/main"),
            new AccountId("owner"),
            id =>
            {
                Assert.Equal("ino/conversations/main", id.Key);
                return agent;
            },
            ct);

        Assert.Equal(ConversationMigrationStatus.NothingToMigrate, result.Status);
        Assert.Equal(0, agent.TranscriptCalls);
    }

    [Fact]
    public async Task MigrationServiceTransformsPopulatedLegacyAgentAndAppliesToState()
    {
        var ct = TestContext.Current.CancellationToken;
        var started = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var ended = started.AddSeconds(2);
        var run = new AgentRun("r1", AgentRunStatus.Completed, "Plan Bergen trip", started)
        {
            EndedAt = ended,
            Reply = "Here is your Bergen itinerary.",
        };
        var legacyState = new AgentState(
            2,
            new AgentDefinition { Instructions = "Be helpful", Contexts = ["ctx/a"] },
            null,
            null,
            [run])
        {
            Blocked = true,
        };
        var agent = new StubLegacyAgent(legacyState);
        var service = new ConversationMigrationService(new ConversationDefaults("ino", "ino/conversations/"));

        var result = await service.MigrateLegacyAgentAsync(
            new NeuronId(Brain, IConversation.Type, "conversations/main"),
            new AccountId("owner"),
            _ => agent,
            ct);

        Assert.Equal(ConversationMigrationStatus.Migrated, result.Status);
        Assert.Equal("ino", result.DefaultAgentId);
        Assert.True(result.Blocked);
        Assert.Equal(1, agent.TranscriptCalls);
        Assert.NotNull(result.Entries);
        Assert.Equal(["Plan Bergen trip", "Here is your Bergen itinerary."], result.Entries!.OfType<Said>().Select(s => s.Text));

        var applied = ConversationMigrationService.ApplyToState(
            new ConversationState(),
            result,
            new AccountId("owner"),
            "assistant",
            keptRuns: 64);

        Assert.True(applied.Imported);
        Assert.Single(applied.Agents);
        Assert.Equal("ino", applied.Agents[0].AgentId);
        Assert.Equal("Be helpful", applied.Agents[0].Definition.Instructions);
        Assert.Contains(new Participant.Human(new AccountId("owner")), applied.Participants);
        Assert.Contains(new Participant.Assistant("ino"), applied.Participants);
        Assert.Equal(2, applied.Log.Length);
        Assert.Single(applied.Runs);
    }

    [Fact]
    public async Task MigrationServiceReturnsFailedWhenLegacyReadThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new ConversationMigrationService(new ConversationDefaults("ino", "ino/conversations/"));

        var result = await service.MigrateLegacyAgentAsync(
            new NeuronId(Brain, IConversation.Type, "conversations/main"),
            new AccountId("owner"),
            _ => new FailingLegacyAgent(),
            ct);

        Assert.Equal(ConversationMigrationStatus.Failed, result.Status);
    }

    [Fact]
    public async Task SessionListProjectionReactsToConversationChangedWithActivityText()
    {
        await using var os = TestOS.Local()
            .Register(typeof(FlutterModule).Assembly)
            .Register<SessionListProjectionNeuron>()
            .Register<StubConversationEmitter>();

        using var person = os.Edge.Enter(os.Edge.Person("person", "person", Brain));
        var convId = ConversationId.New();
        var emitterId = new NeuronId(Brain, "test.conversation-emitter", IConversation.KeyOf(convId));

        // Emit a ConversationChanged with ActivityText — SessionListProjectionNeuron must auto-wake via [Reacts] and touch ISessionList.
        await os.Get<IStubConversationEmitter>(emitterId).Emit(convId, 1, "Plan a trip to Bergen");
        await os.Idle();

        var entry = await os.Get<IThreadDirectory>(SessionListId).Get(convId.Value);
        Assert.NotNull(entry);
        Assert.Equal("Plan a trip to Bergen", entry!.Title);
    }

    [Fact]
    public async Task SessionListProjectionIgnoresChangesWithoutActivityTextOrInvalidSessionIds()
    {
        await using var os = TestOS.Local()
            .Register(typeof(FlutterModule).Assembly)
            .Register<SessionListProjectionNeuron>()
            .Register<StubConversationEmitter>();

        using var person = os.Edge.Enter(os.Edge.Person("person", "person", Brain));
        var convId = ConversationId.New();
        var emitterId = new NeuronId(Brain, "test.conversation-emitter", IConversation.KeyOf(convId));

        await os.Get<IStubConversationEmitter>(emitterId).Emit(convId, 1, null);
        await os.Get<IStubConversationEmitter>(emitterId).Emit(ConversationId.Parse("not-a-session-id"), 2, "Should be ignored");
        await os.Idle();

        var sessions = os.Get<IThreadDirectory>(SessionListId);
        Assert.Null(await sessions.Get(convId.Value));
        Assert.Empty((await sessions.List(archived: false)).Entries);
    }

    [Fact]
    public async Task SessionListProjectionSwallowsErrorsWhenSessionListIsUnavailable()
    {
        // Only register projection + stub emitter (no FlutterModule / ISessionList hosted).
        await using var os = TestOS.Local()
            .Register<SessionListProjectionNeuron>()
            .Register<StubConversationEmitter>();

        using var person = os.Edge.Enter(os.Edge.Person("person", "person", Brain));
        var convId = ConversationId.New();
        var emitterId = new NeuronId(Brain, "test.conversation-emitter", IConversation.KeyOf(convId));

        await os.Get<IStubConversationEmitter>(emitterId).Emit(convId, 1, "Best effort without session list");
        await os.Idle();
    }

    private sealed class StubLegacyAgent(AgentState state, AiMessage[]? transcript = null) : IAgent
    {
        public int TranscriptCalls { get; private set; }
        public Task Configure(AgentDefinition definition, long expectedRevision) => throw new NotSupportedException();
        public IAsyncEnumerable<AgentTurnEvent> Run(AgentTurn turn, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> Start(AgentTurn turn, string? flowId = null) => throw new NotSupportedException();
        public Task Cancel() => throw new NotSupportedException();
        public Task CancelRun(string runId) => throw new NotSupportedException();
        public Task<AgentState> Read() => Task.FromResult(state);
        public Task<AiMessage[]> Transcript() { TranscriptCalls++; return Task.FromResult(transcript ?? []); }
        public Task Clear() => throw new NotSupportedException();
        public Task Post(string text, string id) => throw new NotSupportedException();
    }

    private sealed class FailingLegacyAgent : IAgent
    {
        public Task Configure(AgentDefinition definition, long expectedRevision) => throw new NotSupportedException();
        public IAsyncEnumerable<AgentTurnEvent> Run(AgentTurn turn, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> Start(AgentTurn turn, string? flowId = null) => throw new NotSupportedException();
        public Task Cancel() => throw new NotSupportedException();
        public Task CancelRun(string runId) => throw new NotSupportedException();
        public Task<AgentState> Read() => throw new InvalidOperationException("Simulated legacy store failure.");
        public Task<AiMessage[]> Transcript() => throw new NotSupportedException();
        public Task Clear() => throw new NotSupportedException();
        public Task Post(string text, string id) => throw new NotSupportedException();
    }

    public interface IStubConversationEmitter
    {
        Task Emit(ConversationId id, long revision, string? activityText);
    }

    [Neuron("test.conversation-emitter")]
    public sealed class StubConversationEmitter : Neuron, IStubConversationEmitter, IConversation
    {
        public Task Emit(ConversationId id, long revision, string? activityText)
            => Publish(new ConversationChanged(id, revision) { ActivityText = activityText });

        public Task Configure(ThreadTrustPosture posture, ThreadKind kind, string? title = null, string? leadAgentId = null) => Task.CompletedTask;
        public Task Say(string text, ModelId? model = null, AttachmentRef[]? attachments = null, string? messageId = null, EntryId? replyTo = null) => Task.CompletedTask;
        public Task Stop() => Task.CompletedTask;
        public Task Regenerate(EntryId reply) => Task.CompletedTask;
        public Task<ConversationView> Read(long? sinceRevision = null) => throw new NotSupportedException();
        public Task Invite(AccountId friend, BrainId? friendBrain = null, DigitalBrain.Components.Role role = DigitalBrain.Components.Role.Editor) => Task.CompletedTask;
        public Task Pin(string screenType, string screenKey = "default") => Task.CompletedTask;
        public Task<DigitalBrain.Units.PublishedView> Act(string screenType, string screenKey, DigitalBrain.UI.Command command) => throw new NotSupportedException();
        public Task Here() => Task.CompletedTask;
        public Task Leave() => Task.CompletedTask;
        public Task React(EntryId target, string emoji, bool remove = false) => Task.CompletedTask;
        public Task Edit(EntryId target, string text) => Task.CompletedTask;
        public Task Delete(EntryId target) => Task.CompletedTask;
        public Task Seat(string agentId, AgentDefinition definition) => Task.CompletedTask;
        public Task Post(string text, string id, Visibility visibility = Visibility.Everyone) => Task.CompletedTask;
        public Task Voiced(string heard, string reply, ModelId model, string id) => Task.CompletedTask;
    }
}
