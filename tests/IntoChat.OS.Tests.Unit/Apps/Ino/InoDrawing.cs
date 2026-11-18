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
        => InoView.Screen(s, dictation, props => ChatThreadComposition.Compose(ChatThreadNeuron.Draw(InoView.ConversationKey, InoView.Thread(s)), props));

    // What a running Ino shows: its state with the messages it last projected (they live in the thread, not in the stored state).
    internal static View Screen(InoApp app) => Screen(app.State with { Messages = [.. app.Messages] }, app.CanDictate);

    internal static Conversation Conversation(InoAppState s, bool dictation = false)
        => ChatThreadComposition.Compose(ChatThreadNeuron.Draw(InoView.ConversationKey, InoView.Thread(s)), InoView.ThreadProps(s, dictation));

    // The props Ino wrote on its conversation slot in a view it rendered.
    internal static ChatThreadProps PropsOf(View view)
        => ChatThreadComposition.FirstSlot(System.Text.Json.JsonSerializer.SerializeToElement(view, RuntimeProtocol.OptionsFor(typeof(View)))) is { } slot
            ? ChatThreadComposition.PropsOf(slot.Props)
            : throw new InvalidOperationException("The view has no conversation slot.");
}
