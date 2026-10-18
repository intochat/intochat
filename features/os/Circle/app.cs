/*
# Circle

A circle is a brain of its own that several people belong to: it has its own chat, the apps its members
share into it and a share of its creator's compute. This app lives in each person's own brain and keeps
the index of the circles they are in; everything said or shared happens inside the circle brain, in its
own room app. Inviting someone and accepting an invitation are a person's own acts, done in the circle
brain itself (its /brains/{circle} route), never by a script.

The main layout lists the person's circle brains, each with its name where this brain created it, and how to
open it. It is rebuilt from the circle index, never patched.

Inputs are plain text. Circle names are 1 to 40 of a-z, 0-9 and "-".
- create: the name. Creates a new circle brain with this person as its creator and only member.

## Scenario: Creating a circle lists it

Invoking "create" with "friends" creates a circle brain and lists "friends" with its brain id.
Presentation: startup publishes one sphere and its main content using stable neuron identities.
Workspace navigation owns focus; composition never changes it.

*/

using IntoChat.OS.Apps;

static class App
{
    public const string Name = "circle";
    public const string Version = "2.0.19";

    public static readonly AppManifest Manifest = new(
        Title: "Circle",
        Description: "A feed of published apps, maker profiles and circles to build together. Preview an exact release, then fork it or bring it into a circle.",
        Operations:
        [
            new("open", "Compose and publish the circle index in main."),
            new("create", "Create a circle brain named by the input."),
            new("navigate", "Browse feed, profile, circles, or an exact published release using navigation JSON."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg");
}
