/*
# Circle room

The circle's own app, installed into a circle brain when it is born and into no other brain. It acts only
on the circle brain's own neurons: the circle core and its chat. A member speaks with "say"; the transcript
is rebuilt from the chat whenever a line is spoken, never patched, so a repeated or late signal produces
the same transcript.

- say: the text to say in this circle, as the member who asked.
- share: an app of the member's own brain to install into this circle: its name, then the ids of the
  connections the circle gets its own copy of (space-separated). Every other connection the app is bound to
  stays the member's, reached only through the binding recorded when it was shared.

## Scenario: A line said in the circle is in its transcript

Invoking "say" with "hello" adds "hello" to the circle's chat, spoken by the member's account, and the
transcript shows it.

Startup publishes one sphere and a main layout hosting the existing room content.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "circleroom";
    public const string Version = "1.1.7";

    public static readonly AppManifest Manifest = new(
        Title: "Circle room",
        Description: "The circle's own room: its chat and the apps shared into it.",
        Operations:
        [
            new("open", "Refresh the room and selected publication without changing focus."),
            new("publication", "Preview and confirm an exact published release given its reference JSON."),
            new("say", "Say the input in this circle."),
            new("share", "Share an app of yours into this circle: its name, then the connection ids the circle gets its own copy of."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg");
}
