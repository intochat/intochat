using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.Chat;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoUiChatSyncFacts
{
    private static readonly BrainId Brain = new("local");

    private static TestOS CreateOs()
    {
        var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(AIModule).Assembly);
        os.Register(typeof(UiModule).Assembly);
        os.Register<InoApp>();
        return os;
    }

    [Fact]
    public async Task InoConversations_list_returns_most_recent_first_and_switches_cleanly()
    {
        await using var os = CreateOs();
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));

        var convs = os.Get<IInoConversations>(new NeuronId(Brain, "ino.conversations", IInoConversations.DefaultKey));
        var id1 = InoModel.MintConversationId();
        var id2 = InoModel.MintConversationId();
        await convs.Use(InoModel.AgentKeyOf(id1));
        await convs.Use(InoModel.AgentKeyOf(id2));

        var list = await convs.List();
        Assert.Equal(2, list.Length);
        Assert.Equal(InoModel.AgentKeyOf(id2), list[0]);
        Assert.Equal(InoModel.AgentKeyOf(id1), list[1]);

        var ino = await os.Install<InoApp>();
        await ino.Request("switch-conversation", new InoSwitchConversation(id1));
        Assert.Equal(id1, ino.App.State.ConversationId);
        Assert.Contains(InoModel.AgentKeyOf(id2), ino.App.State.RecentConversations ?? []);

        var side = ino.App.RenderSide();
        var json = System.Text.Json.JsonSerializer.Serialize(side);
        Assert.Contains($"session-{id1}", json, StringComparison.Ordinal);
        Assert.Contains($"session-{id2}", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InoApp_syncs_to_ui_chat_thread_and_compacts_into_agent_context()
    {
        await using var os = CreateOs();
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));

        var ino = await os.Install<InoApp>();
        await ino.Request("open", new InoOpen());

        var uiChatKey = InoModel.UiChatKeyOf(ino.App.State.ConversationId);
        var uiChat = os.Get<IChatThread>(new NeuronId(Brain, "ui.chat", uiChatKey));
        var state = await uiChat.Read();
        Assert.Equal(ino.App.State.ConversationId, state.ConversationId);

        // Selecting a model on ui.chat should drive InoApp via ChatModelSelected signal.
        await uiChat.SelectModel("qwen2.5-coder:14b");

        var afterModel = await uiChat.Read();
        Assert.Equal("qwen2.5-coder:14b", afterModel.ActiveModel);

        // Verify BuildCompactSummary produces structured summary text.
        var summary = InoModel.BuildCompactSummary([
            new InoMessage("m1", "user", "Explain the UI neuron architecture"),
            new InoMessage("m2", "assistant", "Every interactive UI widget is a stateful neuron under src/Modules/UI."),
        ]);
        Assert.Contains("Compacted 2 message(s)", summary, StringComparison.Ordinal);
        Assert.Contains("Explain the UI neuron architecture", summary, StringComparison.Ordinal);

        // Switching Ino to DevUI mode renders the live ui.chat NeuronRef directly.
        await ino.Request("toggle-devui", new InoToggleDevUi());
        Assert.True(ino.App.State.DevUiMode);
        var devUiView = ino.App.Render();
        var devUiJson = System.Text.Json.JsonSerializer.Serialize(devUiView);
        Assert.Contains("ui.chat", devUiJson, StringComparison.Ordinal);
        Assert.Contains(uiChatKey, devUiJson, StringComparison.Ordinal);
    }
}
