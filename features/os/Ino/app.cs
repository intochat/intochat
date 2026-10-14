/*
# Ino

Ino is the assistant's presence. Its sphere's mood follows the assistant's activity; its main slot is
the assistant's selected conversation. open publishes the presentation.

- open: publishes the presence tree and the assistant's chat with voice input in main, with no side slot.
- new-conversation: requests a new conversation from the assistant.
- voice: requests voice input from the assistant.

The mood blackboard key is listening for listening or accepted activity, thinking for working or waiting,
acting for reading or searching, speaking for completed, error for attention, and idle otherwise.
Startup and open publish the same presentation under the executing program revision. A failure records
the presentation error under that revision. The workspace owns focus and navigation.
Each forwarding synapse remembers its handled request ids in its own ledger, so retained requests
do not start another conversation or voice capture when the program restarts.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "ino";
    public const string Version = "2.0.3";

    public static readonly AppManifest Manifest = new(
        Title: "Ino",
        Description: "Your assistant's presence, with its conversation and voice input.",
        Operations:
        [
            new("open", "Publish Ino's presence and the assistant's selected conversation."),
            new("new-conversation", "Start a new conversation with the assistant."),
            new("voice", "Begin voice input with the assistant."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg",
        Toolbar:
        [
            new("new-conversation", Icon: "chat", Label: "New conversation"),
            new("voice", Icon: "mic", Label: "Voice"),
        ]);
}
