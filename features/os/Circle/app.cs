/*
# Circle

A circle is a brain of its own that several people belong to: it has its own chat, the apps its members
share into it and a share of its creator's compute. This app lives in each person's own brain and keeps
the index of the circles they are in; everything said or shared happens inside the circle brain, in its
own room app. Inviting someone and accepting an invitation are a person's own acts, done in the circle
brain itself (its /brains/{circle} route), never by a script.

The surface lists the person's circle brains, each with its name where this brain created it, and how to
open it. It is rebuilt from the circle index, never patched.

Inputs are plain text. Circle names are 1 to 40 of a-z, 0-9 and "-".
- create: the name. Creates a new circle brain with this person as its creator and only member.

## Scenario: Creating a circle lists it

Invoking "create" with "friends" creates a circle brain and lists "friends" with its brain id.
Workspace hosting: startup composes the declared entry using stable neuron identities. Opening the
workspace reads that state; composition and completion never change focus or reopen a closed entry.

*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "circle";
    public const string Version = "2.0.1";

    public static readonly AppManifest Manifest = new(
        Title: "Circle",
        Description: "The circles you are in: each one a brain of its own with a chat and shared apps.",
        Operations:
        [
            new("open", "Compose the circle index and open its window; input \"docked\" skips the window."),
            new("create", "Create a circle brain named by the input."),
        ],
        Settings: [],
        Slots: [],
        Entry: new("surface", "surface"),
        Icon: "icon.svg");
}
