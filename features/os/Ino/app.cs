/*
# Ino

Ino is the person's assistant as a presence: a living sphere at the centre of the home scene, surrounded by
what the brain is doing right now. Ino never does work itself; it shows the work of the router, the
assistant's turns and every behavior that runs, and it moves the stage so the app the person asked for
appears beside it. Escape brings the person back to this scene.

Inputs are plain text.
- open: composes the welcome and home scenes, sets the stage's home to the home scene and goes there.

## Scenario: Opening Ino puts the person on the home scene

Invoking "open" composes "home" with the ino_sphere in its idle state and an empty activity ring, and the
stage's top is the home scene.

## Scenario: A route turns Ino thoughtful

When the router publishes Routed, the presence machine moves to "thinking"; when the assistant's turn
completes it moves to "speaking" and then back to "idle".
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "ino";
    public const string Version = "1.0.0";

    public static readonly AppManifest Manifest = new(
        Title: "Ino",
        Description: "Your assistant as a presence: a living sphere showing what your brain is doing, and the way home.",
        Operations: [new("open", "Compose Ino's scenes and make the home scene the stage's home.")],
        Settings: [],
        Slots: [],
        Icon: "icon.svg");
}
