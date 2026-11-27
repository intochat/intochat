using DigitalBrain;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoModelFacts
{
    private static readonly DateTimeOffset At = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Participant.Human Owner = new(new AccountId("local"));
    private static readonly Participant.Assistant Ino = new("ino");

    [Fact]
    public void SendingShowsMyMessageAtOnce()
    {
        var next = InoModel.Sent(new InoAppState(), "hello", "p1", At);
        Assert.Equal(new InoPending("p1", "hello", At), next.Pending);
        Assert.Equal((InoModel.Working, "hello"), (next.Status, next.Title));
    }

    [Fact]
    public void ASecondSendWhileOneIsPendingChangesNothing()
    {
        var first = InoModel.Sent(new InoAppState(), "hello", "p1", At);
        Assert.Same(first, InoModel.Sent(first, "again", "p2", At));
    }

    [Fact]
    public void ASendWhileARunIsActiveChangesNothing()
    {
        var running = new InoAppState() with { ActiveRunId = "r1" };
        Assert.Same(running, InoModel.Sent(running, "again", "p2", At));
    }

    [Fact]
    public void AMessageOver32000CharactersIsNotSent()
    {
        var next = InoModel.Sent(new InoAppState(), new string('x', 32_001), "p1", At);
        Assert.Null(next.Pending);
        Assert.Equal("too-long", next.Error?.Code);
    }

    [Fact]
    public void FromConversationClearsPendingAndNeverWritesMessages()
    {
        var sent = InoModel.Sent(new InoAppState(), "hello", "p1", At);
        var view = View("main", 1, null, [new AgentRun("r1", AgentRunStatus.Completed, "hello", At, Reply: "Hi.")], User("hello"), Answer("Hi."));
        var next = InoModel.FromConversation(sent, view);

        Assert.Null(next.Pending);
        Assert.Null(next.Messages);
        Assert.Equal(InoModel.Ready, next.Status);
        Assert.Null(next.ActiveRunId);
    }

    [Fact]
    public void ARunningReplyKeepsInoWorking()
    {
        var active = new AgentRun("r1", AgentRunStatus.Running, "hello", At, Reply: "Hi");
        var next = InoModel.FromConversation(new InoAppState(), View("main", 1, active, [], User("hello")));
        Assert.Equal((InoModel.Working, "r1"), (next.Status, next.ActiveRunId));
    }

    [Fact]
    public void AViewOfAnotherConversationChangesNothing()
    {
        var current = new InoAppState();
        var other = View("earlier", 5, null, [new AgentRun("r1", AgentRunStatus.Completed, "old", At, Reply: "old reply")], User("old"), Answer("old reply"));
        Assert.Same(current, InoModel.FromConversation(current, other));
    }

    [Fact]
    public void AFailedReplyBecomesAnErrorWithItsCause()
    {
        var failed = new AgentRun("r1", AgentRunStatus.Failed, "hello", At, Error: "opaque") { Failure = AgentFailure.NoProvider };
        var next = InoModel.FromConversation(new InoAppState(), View("main", 1, null, [failed], User("hello")));
        Assert.Equal(InoModel.Failed, next.Status);
        Assert.Equal("no-provider", next.Error?.Code);
        Assert.Null(next.ActiveRunId);
    }

    [Fact]
    public void FromConversationReadsTypedRunFailureEvenWithoutMatchingErrorText()
    {
        var failed = new AgentRun("r1", AgentRunStatus.Failed, "hello", At, Error: "completely opaque vendor payload 9999")
        {
            Failure = AgentFailure.ProviderRejectedKey,
        };
        var next = InoModel.FromConversation(new InoAppState(), View("main", 1, null, [failed], User("hello")));
        Assert.Equal(InoModel.Failed, next.Status);
        Assert.Equal("provider-rejected-key", next.Error?.Code);
    }

    [Fact]
    public void AnInterruptedReplyCanBeTriedAgain()
    {
        var interrupted = new AgentRun("r1", AgentRunStatus.Interrupted, "hello", At, Reply: "Hi");
        var next = InoModel.FromConversation(new InoAppState(), View("main", 1, null, [interrupted], User("hello")));
        Assert.Equal(InoModel.Failed, next.Status);
        Assert.Equal("interrupted", next.Error?.Code);
        Assert.Null(next.ActiveRunId);
    }

    [Fact]
    public void ANewSendClearsTheLastError()
    {
        var failed = new InoAppState() with { Error = new InoError("unknown"), Status = InoModel.Failed };
        Assert.Null(InoModel.Sent(failed, "again", "p2", At).Error);
    }

    [Fact]
    public void AFailedStartDropsThePendingMessageAndShowsWhy()
    {
        var next = InoModel.StartFailed(InoModel.Sent(new InoAppState(), "hello", "p1", At), new InoError("paused", "An interrupt holds the agent."));
        Assert.Null(next.Pending);
        Assert.Equal("paused", next.Error?.Code);
        Assert.Equal(InoModel.Paused, next.Status);
    }

    [Fact]
    public void APauseShowsAndResumingClearsIt()
    {
        var paused = InoModel.Pause(new InoAppState(), "An interrupt holds the agent.");
        Assert.Equal((InoModel.Paused, "paused", true), (paused.Status, paused.Error?.Code, paused.Blocked));
        var resumed = InoModel.Resume(paused);
        Assert.Equal(InoModel.Ready, resumed.Status);
        Assert.Null(resumed.Error);
        Assert.False(resumed.Blocked);
    }

    [Fact]
    public void TheModelInInosHeaderIsTheModelTheTurnSelected()
    {
        var opened = new InoAppState();
        var started = InoModel.Started(opened, "r1", "qwen3.5:4b");
        Assert.Equal("qwen3.5:4b", started.SelectedModel);

        var view = View("main", 1, null,
            [new AgentRun("r1", AgentRunStatus.Completed, "hello", At, Reply: "Answered on Qwen 3 14B.")],
            User("hello"),
            Answer("Answered on Qwen 3 14B.", new ModelId("qwen3:14b")));
        var projected = InoModel.FromConversation(started, view);
        Assert.Equal("qwen3:14b", projected.SelectedModel);
    }

    [Fact]
    public void ANewConversationKeepsNothingOfTheOld()
    {
        var old = InoModel.FromConversation(
            new InoAppState(),
            View("main", 3, null, [new AgentRun("r1", AgentRunStatus.Completed, "hello", At, Reply: "Hi.")], User("hello"), Answer("Hi.")))
            with
        { GrantSummaries = ["read · files"] };
        var next = InoModel.NewConversation(old, "c2");
        Assert.Equal(("c2", "New chat", InoModel.Ready), (next.ConversationId, next.Title, next.Status));
        Assert.Null(next.Messages);
        Assert.Equal(["read · files"], next.GrantSummaries!);
    }

    [Fact]
    public void InoAgentDefinitionForwardsReasoningEffortForReasoningModelsAndOmitsForNonReasoningModels()
    {
        var solSettings = new UserSettingsState
        {
            DisplayName = "Ada",
            DefaultModel = KnownModels.Gpt56Sol.Id.Value,
            Variables = [new UserVariableEntry(InoAgentDefinition.ReasoningEffortVariable, "low")],
        };

        var solDef = InoAgentDefinition.For(solSettings);
        Assert.NotNull(solDef.Model);
        Assert.Equal(KnownModels.Gpt56Sol.Id.Value, solDef.Model!.Model);
        Assert.Equal("low", solDef.Model.Reasoning);

        var nonReasoningSettings = solSettings with { DefaultModel = KnownModels.Gpt54.Id.Value };
        var nonReasoningDef = InoAgentDefinition.For(nonReasoningSettings);
        Assert.NotNull(nonReasoningDef.Model);
        Assert.Equal(KnownModels.Gpt54.Id.Value, nonReasoningDef.Model!.Model);
        Assert.Null(nonReasoningDef.Model.Reasoning);

        var defaultEffortSettings = solSettings with
        {
            Variables = [new UserVariableEntry(InoAgentDefinition.ReasoningEffortVariable, "default")],
        };
        Assert.Null(InoAgentDefinition.For(defaultEffortSettings).Model!.Reasoning);
    }

    [Fact]
    public async Task CascadedVoiceIsReadyWhenHearAndSpeakAreReadyWithoutKokoro82M()
    {
        var catalog = new VoiceGateStubCatalog(
            hear: KnownModels.Gpt4oTranscribe.Id,
            speak: KnownModels.Gpt4oMiniTts.Id,
            talk: null,
            availability:
            [
                new ModelAvailability(KnownModels.Gpt4oTranscribe.Id, Ready: true, Reason: null),
                new ModelAvailability(KnownModels.Gpt4oMiniTts.Id, Ready: true, Reason: null),
                new ModelAvailability(KnownModels.Kokoro82M.Id, Ready: false, Reason: "Not installed"),
            ]);

        var host = new InoSetupHost(
            GetState: static () => new InoAppState(),
            ShowState: static _ => Task.CompletedTask,
            GetBrain: static () => new BrainId("local"),
            GetCatalog: () => catalog,
            GetDevice: static () => null,
            GetSettings: static () => throw new NotSupportedException(),
            GetConversation: static _ => throw new NotSupportedException(),
            GetSessions: static () => throw new NotSupportedException(),
            GetGrants: static () => throw new NotSupportedException(),
            GetApprovals: static () => throw new NotSupportedException(),
            GetProfileWriter: static () => throw new NotSupportedException(),
            FollowConversation: static _ => Task.CompletedTask,
            RefreshConversation: static _ => Task.CompletedTask,
            TryOptional: static (_, _) => Task.CompletedTask,
            ReadBoolOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadApprovalsOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadStringArrayOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadSettingsOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadDeviceOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadCatalogOptional: static (_, fallback, _) => Task.FromResult(fallback),
            ReadSessionPageOptional: static (_, fallback, _) => Task.FromResult(fallback),
            LogOptional: static (_, _) => { },
            Logger: null);

        var voice = await InoModelSetup.ReadVoiceAsync(host);

        Assert.True(voice.Hear);
        Assert.True(voice.Speak);
        Assert.True(voice.Talk);
    }

    private sealed class VoiceGateStubCatalog(
        ModelId? hear,
        ModelId? speak,
        ModelId? talk,
        ModelAvailability[] availability) : IModelCatalog
    {
        public Task<ModelCard[]> List(Abilities need = Abilities.None) => Task.FromResult(KnownModels.All.ToArray());

        public Task<ModelId> Pick(ModelRole role, ModelId? turn = null) => role switch
        {
            ModelRole.Hear when hear is { } h => Task.FromResult(h),
            ModelRole.Speak when speak is { } s => Task.FromResult(s),
            ModelRole.Talk when talk is { } t => Task.FromResult(t),
            _ => throw new InvalidOperationException($"Role {role} unavailable"),
        };

        public Task SetRole(ModelRole role, ModelId model) => Task.CompletedTask;
        public Task<RoleMap> Roles() => Task.FromResult(KnownModels.Defaults);
        public Task<ModelAvailability[]> Availability() => Task.FromResult(availability);
        public Task SaveProviderCredential(string provider, string credentialValue) => Task.CompletedTask;
    }

    private static ConversationView View(string id, long revision, AgentRun? active, AgentRun[] runs, params ConversationEntry[] entries)
        => new(ConversationId.Parse(id), revision, [Owner, Ino], entries, active, runs, false, []);

    private static Said User(string text) => new(EntryIds.Said("r1"), Owner, At, text);

    private static Said Answer(string text, ModelId? model = null) => new(EntryIds.Reply("r1"), Ino, At, text, model);
}
