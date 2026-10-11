/*
# Circle room

The circle's own app, installed into a circle brain when it is born and into no other brain. It acts only
on the circle brain's own neurons: the circle core and its chat. A member speaks with "say"; the transcript
is rebuilt from the chat whenever a line is spoken, never patched, so a repeated or late signal produces
the same transcript.

- say: the text to say in this circle, as the member who asked.

## Scenario: A line said in the circle is in its transcript

Invoking "say" with "hello" adds "hello" to the circle's chat, spoken by the member's account, and the
transcript shows it.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "circleroom";
    public const string Version = "1.0.0";

    public static readonly AppManifest Manifest = new(
        Title: "Circle room",
        Description: "The circle's own room: its chat and the apps shared into it.",
        Operations:
        [
            new("say", "Say the input in this circle."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg");
}
