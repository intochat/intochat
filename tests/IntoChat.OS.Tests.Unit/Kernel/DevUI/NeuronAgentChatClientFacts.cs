using IntoChat.OS.Kernel.DevUI;
using IntoChat.OS.Modules.AI.Agents;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Kernel.Tests.Unit.DevUI;

// DevUI shows the agent's own turn: its text streams through, a tool call or failure becomes a status line, a failed turn fails the
// response, and bookkeeping events show nothing.
public sealed class NeuronAgentChatClientFacts
{
    [Fact]
    public void TextStreamsThrough() => Assert.Equal("hi", NeuronAgentChatClient.Update(new AgentTurnEvent.TextChunk("hi"))!.Text);

    [Fact]
    public void AToolCallIsAStatusLine()
    {
        var update = NeuronAgentChatClient.Update(new AgentTurnEvent.ToolStarted("c1", "neurons_call", "{}"))!;
        Assert.Contains("neurons_call", Assert.IsType<TextReasoningContent>(Assert.Single(update.Contents)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedTurnFailsTheResponse()
        => Assert.Throws<InvalidOperationException>(() => NeuronAgentChatClient.Update(new AgentTurnEvent.TurnFailed("no model")));

    [Fact]
    public void BookkeepingShowsNothing() => Assert.Null(NeuronAgentChatClient.Update(new AgentTurnEvent.Finished()));
}
