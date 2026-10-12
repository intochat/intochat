/*
# Ino

Ino is the person's assistant as a presence: a living sphere at the centre of the home scene, surrounded by
what the brain is doing right now. Ino never does work itself; it shows the work of the router, the
assistant's turns and recent app updates. The workspace owns navigation; Ino never docks or focuses another app.
The ring shows up to six apps by title, with Updated or Needs attention outcomes. Background subscriptions
are not active user tasks and do not change the assistant's presence. Detailed runs remain in Brain.

Inputs are plain text.
- open: composes the welcome and home scenes without changing workspace navigation.

## Scenario: Opening Ino puts the person on the home scene

Startup and "open" initialize missing scenes and preserve the existing activity ring and presence.

## Scenario: A route turns Ino thoughtful

When the router publishes Routed, the presence machine moves to "thinking"; when the assistant's turn
completes it moves to "speaking" and then back to "idle".
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "ino";
    public const string Version = "1.0.2";

    public static readonly AppManifest Manifest = new(
        Title: "Ino",
        Description: "Your assistant as a presence: a living sphere showing what your brain is doing, and the way home.",
        Operations: [new("open", "Compose Ino's scenes without changing workspace navigation.")],
        Settings: [],
        Slots: [],
        Entry: new("scene", "scenes/home"),
        Icon: "icon.svg");
}
