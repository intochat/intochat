using IntoChat.OS.Modules.AI.GroupChat;
using IntoChat.OS.Modules.AI.GroupChat.Signals;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.AI.Models.OpenAI;
using IntoChat.OS.Testing.Model;
using Xunit;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

public sealed class GroupChatFacts(AIHost host)
{
    private const string LunaModel = "gpt-5.6-luna";
    private const string SolModel = "gpt-5.6-sol";

    private static readonly GroupChatSetup LunaAndGemma = new(
        [new("Luna", nameof(IGpt56Luna)), new("Gemma", nameof(IGpt56Sol))],
        Brief: "Brainstorm product ideas.",
        MaxRounds: 3);

    [Trait("host", "own")]
    [Fact]
    public async Task ParticipantsTakeTurnsAndConcludeWhenTheyAgree()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        model.Script(LunaModel, "Idea: a smart dog bowl.", "AGREE: the smart bowl, with a feeding log.", "Final: a smart dog bowl that logs feeding.");
        model.Script(SolModel, "Building on the bowl: add a feeding log.", "AGREE: bowl plus log.");
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithFakeModel(model).StartAsync(ct);

        var chat = brain.Get<IGroupChat>("chat-1");
        await chat.Configure(LunaAndGemma);
        await using var spoke = await brain.Observe<GroupChatSpoke>(chat, ct);
        await using var concluded = await brain.Observe<GroupChatConcluded>(chat, ct);
        var state = await chat.Ask("Name one product idea for dog owners");

        Assert.Equal(["Luna", "Gemma", "Luna", "Gemma"], state.Turns.Select(turn => turn.Speaker));
        Assert.Equal([1, 1, 2, 2], state.Turns.Select(turn => turn.Round));
        Assert.Equal(GroupChatStatus.Concluded, state.Status);
        Assert.True(state.Agreed);
        Assert.Equal("Final: a smart dog bowl that logs feeding.", state.Answer);
        Assert.Contains("Luna: Idea: a smart dog bowl.", model.Requests(SolModel)[0].GetRawText(), StringComparison.Ordinal);
        Assert.Equal("Luna", (await spoke.NextAsync(ct: ct)).Turn.Speaker);
        var conclusion = await concluded.NextAsync(ct: ct);
        Assert.Equal(2, conclusion.Rounds);
        Assert.Equal(state.Answer, conclusion.Answer);
        await brain.DeactivateAsync(chat, ct);
        var restored = await chat.Read();
        Assert.Equal(state.Turns, restored.Turns);
        Assert.Equal(LunaAndGemma.Participants, restored.Setup!.Participants);
        Assert.Equal(state.Answer, restored.Answer);
        Assert.Equal(GroupChatStatus.Concluded, restored.Status);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task DiscussionStopsAtMaxRoundsWithoutAgreement()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        model.Script(LunaModel, "A", "B", "C", "Final: A.");
        model.Script(SolModel, "not A", "not B", "not C");
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithFakeModel(model).StartAsync(ct);

        var chat = brain.Get<IGroupChat>("chat-2");
        await chat.Configure(LunaAndGemma);
        var state = await chat.Ask("Pick one");

        Assert.Equal(6, state.Turns.Length);
        Assert.False(state.Agreed);
        Assert.Equal("Final: A.", state.Answer);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AFailingParticipantLeavesTheChatFailedWithTheTurnsSoFar()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        model.Script(LunaModel, "A");
        model.Script(SolModel);
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithFakeModel(model).StartAsync(ct);

        var chat = brain.Get<IGroupChat>("chat-5");
        await chat.Configure(LunaAndGemma);
        await Assert.ThrowsAsync<System.ClientModel.ClientResultException>(() => chat.Ask("Pick one"));

        var state = await chat.Read();
        Assert.Equal(GroupChatStatus.Failed, state.Status);
        Assert.Equal("Luna", Assert.Single(state.Turns).Speaker);
    }

    [Fact]
    public async Task AskingBeforeConfiguringIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Get<IGroupChat>("chat-3").Ask("anything"));
    }

    [Fact]
    public async Task UnknownModelsAreRejectedAtConfiguration()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        await Assert.ThrowsAsync<ArgumentException>(() => brain.Get<IGroupChat>("chat-4")
            .Configure(new([new("Ghost", "INoSuchModel"), new("Gemma", nameof(IGemma4))])));
    }
}
