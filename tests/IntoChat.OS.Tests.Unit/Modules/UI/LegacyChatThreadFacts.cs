using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.Legacy;

namespace IntoChat.OS.Tests.Unit.Modules.UI;

// The ui.* family is deleted; the one reader left is ui.chat, read-only, so Engineer can import the brainstorm history an earlier
// version stored there (owner decision 11).
public sealed class LegacyChatThreadFacts
{
    private static readonly BrainId Brain = new("legacychat");

    [Fact]
    public async Task AThreadTheOldChatNeuronStoredIsReadBack()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(UiModule).Assembly);
        var id = new NeuronId(Brain, "ui.chat", "engineer/chat/brainstorm");
        os.SeedStoredState(id, Stored(
            new ChatMessage("run-1-turn-0", "user", "Earlier goal"),
            new ChatMessage("run-1-turn-1", "assistant", "Earlier plan", Reasoning: "thinking")));

        var state = await os.Get<ILegacyChatThread>(id).Read();

        Assert.Equal(["run-1-turn-0", "run-1-turn-1"], state.Messages.Select(static message => message.Id));
        Assert.Equal("Earlier plan", state.Messages[1].Text);
        Assert.Equal("assistant", state.Messages[1].Role);
    }

    [Fact]
    public async Task AChatNeuronThatStoredNothingReadsEmpty()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(UiModule).Assembly);

        var state = await os.Get<ILegacyChatThread>(new NeuronId(Brain, "ui.chat", "never-written")).Read();

        Assert.Empty(state.Messages);
    }

    // Nothing writes the old neuron: its contract is the read alone.
    [Fact]
    public void TheReaderOnlyReads()
        => Assert.Equal(["Read"], typeof(ILegacyChatThread).GetMethods().Select(static method => method.Name));

    internal static string Stored(params ChatMessage[] messages)
        => System.Text.Json.JsonSerializer.Serialize(
            new UiChatThreadState() with { Version = messages.Length, Messages = messages },
            RuntimeProtocol.OptionsFor(typeof(UiChatThreadState)));
}
