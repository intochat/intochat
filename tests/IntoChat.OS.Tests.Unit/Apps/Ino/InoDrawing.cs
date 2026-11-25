using DigitalBrain.Invocation;
using DigitalBrain.UI;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Tests;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

// What a person sees of Ino for a state, drawn without a runtime: Ino's screen with its conversation thread composed the way the client
// composes a chatThreadView with its slot props (ChatThreadComposition).
internal static class InoDrawing
{
    internal static View Screen(
        InoAppState s,
        bool dictation = false,
        ConversationView? view = null,
        SessionPage? sessions = null,
        IReadOnlyDictionary<string, AppManifest?>? apps = null,
        string? links = null,
        VoiceAbilities? voice = null)
        => InoView.Screen(s, voice ?? new VoiceAbilities(dictation, false, false), props => Thread(s, view, apps, links, props), view, sessions);

    // What a running Ino shows: its state with the conversation view it last read (messages live in the thread, never in stored state).
    internal static View Screen(InoApp app)
        => Screen(app.State, app.Voice.Hear, app.CurrentView, app.CurrentSessions, app.CurrentApps, app.CurrentLinks, app.Voice);

    internal static Conversation Conversation(
        InoAppState s,
        bool dictation = false,
        ConversationView? view = null,
        IReadOnlyDictionary<string, AppManifest?>? apps = null,
        string? links = null)
        => Thread(s, view, apps, links, InoView.ThreadProps(s, view, new VoiceAbilities(dictation, false, false)));

    // The thread as Ino writes it: its messages, and the stop and the error's retry the routed props show.
    private static Conversation Thread(
        InoAppState s,
        ConversationView? view,
        IReadOnlyDictionary<string, AppManifest?>? apps,
        string? links,
        ChatThreadProps props)
        => ChatThreadComposition.Compose(
            ChatThreadNeuron.Draw(InoView.ConversationKey, InoView.Thread(s, view, apps, links), props.Working, props.Error is { Retry: true }), props);

    // The props Ino wrote on its conversation slot in a view it rendered.
    internal static ChatThreadProps PropsOf(View view)
        => ChatThreadComposition.FirstSlot(System.Text.Json.JsonSerializer.SerializeToElement(view, RuntimeProtocol.OptionsFor(typeof(View)))) is { } slot
            ? ChatThreadComposition.PropsOf(slot.Props)
            : throw new InvalidOperationException("The view has no conversation slot.");
}
