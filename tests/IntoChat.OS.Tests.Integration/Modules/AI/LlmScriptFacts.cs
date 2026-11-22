using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Modules.AI;

public sealed class LlmScriptFacts
{
    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task TwoTurnScriptCallsWebSearchAndReplies(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = new WebResult("Acme", new Uri("https://acme.test"), "Acme moves freight.");
        var script = LlmScript.New()
            .Turn(t => t.CallsTool("web_search", new { query = "acme", count = 3 }))
            .Turn(t => t.Says("Acme moves freight."));

        await using var os = await Journey.Start(
            kind,
            f => f.WebSearch(s => s.Answer("acme", expected)).Llm(script),
            ct);

        var ada = os.Person("ada");
        var conversation = ada.Get<IConversation>(
            new NeuronId(ada.Brain, IConversation.Type, IConversation.KeyOf(ConversationId.Parse("main"))));
        await conversation.Seat("assistant", new AgentDefinition
        {
            Contracts = [typeof(IWebSearch).FullName!],
        });

        await conversation.Say("What does Acme do?");

        var view = await WaitForRunAsync(conversation, expectedRuns: 1, ct);
        var run = Assert.Single(view.Runs);
        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal("Acme moves freight.", run.Reply);

        var assistantEntry = view.Entries.OfType<Said>().Last(e => e.By is Participant.Assistant);
        Assert.Equal("Acme moves freight.", assistantEntry.Text);

        var call = Assert.Single(script.Calls);
        Assert.Equal("web_search", call.Name);
        Assert.Contains("acme", call.ArgumentsJson, StringComparison.Ordinal);
        Assert.Contains("Acme moves freight.", call.ResultText, StringComparison.Ordinal);

        script.ShouldBeFinished();
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ExhaustedScriptFailsWithLastUserText(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var script = LlmScript.New()
            .Turn(t => t.Says("First reply."));

        await using var os = await Journey.Start(
            kind,
            f => f.WebSearch().Llm(script),
            ct);

        var ada = os.Person("ada");
        var conversation = ada.Get<IConversation>(
            new NeuronId(ada.Brain, IConversation.Type, IConversation.KeyOf(ConversationId.Parse("extra"))));
        await conversation.Seat("assistant", new AgentDefinition());

        await conversation.Say("First question.");
        var firstView = await WaitForRunAsync(conversation, expectedRuns: 1, ct);
        Assert.Equal(AgentRunStatus.Completed, Assert.Single(firstView.Runs).Status);
        script.ShouldBeFinished();

        // Direct model request after script is exhausted throws with the last user text.
        var client = os.Runtime.Services.GetRequiredService<IChatClient>();
        var directError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "Direct follow-up?")], cancellationToken: ct));
        Assert.Contains("the script has no turn left; the model was asked: Direct follow-up?", directError.Message, StringComparison.Ordinal);

        // Conversation turn after script is exhausted records the failure message.
        await conversation.Say("Tell me more about Acme.");
        var view = await WaitForRunAsync(conversation, expectedRuns: 2, ct);
        var failedRun = view.Runs.Last();
        Assert.Equal(AgentRunStatus.Failed, failedRun.Status);
        Assert.NotNull(failedRun.Error);
        Assert.Contains("the script has no turn left; the model was asked: Tell me more about Acme.", failedRun.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldBeFinishedThrowsWhenTurnsRemain()
    {
        var script = LlmScript.New()
            .Turn(t => t.Says("Unused reply."));

        var error = Assert.Throws<InvalidOperationException>(script.ShouldBeFinished);
        Assert.Contains("turn", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ConversationView> WaitForRunAsync(IConversation conversation, int expectedRuns, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var view = await conversation.Read();
            if (view.Active is null && view.Runs.Length >= expectedRuns)
            {
                return view;
            }
            await Task.Delay(25, ct);
        }
        return await conversation.Read();
    }
}
