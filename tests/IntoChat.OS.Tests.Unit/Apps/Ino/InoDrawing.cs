using DigitalBrain.Invocation;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Tests;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

// What a person sees of Ino for a state, drawn without a runtime: Ino's screen with its conversation thread composed the way the client
// composes a chatThreadView with its slot props (ChatThreadComposition).
internal static class InoDrawing
{
    internal static View Screen(InoAppState s, bool dictation = false)
        => InoView.Screen(s, dictation, props => Thread(s, props));

    // What a running Ino shows: its state with the messages it last projected (they live in the thread, not in the stored state).
    internal static View Screen(InoApp app) => Screen(app.State with { Messages = [.. app.Messages] }, app.Voice.Hear);

    internal static Conversation Conversation(InoAppState s, bool dictation = false)
        => Thread(s, InoView.ThreadProps(s, dictation));

    // The thread as Ino writes it: its messages, and the stop and the error's retry the routed props show.
    private static Conversation Thread(InoAppState s, ChatThreadProps props)
        => ChatThreadComposition.Compose(
            ChatThreadNeuron.Draw(InoView.ConversationKey, InoView.Thread(s), props.Working, props.Error is { Retry: true }), props);

    // The props Ino wrote on its conversation slot in a view it rendered.
    internal static ChatThreadProps PropsOf(View view)
        => ChatThreadComposition.FirstSlot(System.Text.Json.JsonSerializer.SerializeToElement(view, RuntimeProtocol.OptionsFor(typeof(View)))) is { } slot
            ? ChatThreadComposition.PropsOf(slot.Props)
            : throw new InvalidOperationException("The view has no conversation slot.");
}
