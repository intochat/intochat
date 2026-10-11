/*
# Circle

Circles are small groups of brains that talk and share. Each circle has members, apps its owner shares
with it, a chat, and a share of the owner's compute that members' calls may draw on. The platform's own
share is fixed at 25% and is only shown.

The surface lists the circles this brain is in with their members, shared apps and compute share, the
invitations waiting for an answer, and a transcript per circle. Every part is rebuilt from the circle
and chat neurons, never patched, so a repeated or late signal produces the same surface. The toolbar
holds the compute-share slider (0 to 25) for the selected circle.

Inputs are plain text. Circle names are 1 to 40 of a-z, 0-9 and "-".
- create: the name. Makes this brain the owner of a new circle and selects it.
- invite: "name brainId". The owner invites another brain.
- accept: the name of a circle this brain was invited to.
- say: "name text" speaks in a circle; a bare text speaks in the selected one.
- share: "name app" shares one of the owner's apps with the circle.
- set-share: "name percent", or a bare percent for the selected circle (what the slider posts).

## Scenario: Creating a circle shows it

Invoking "create" with "friends" lists a circle "friends" with this brain as its only member.

## Scenario: An invitation waits for an answer

When another brain invites this one, the surface shows the invitation with who sent it; "accept"
joins the circle and its members and chat appear.

## Scenario: A line spoken anywhere appears in every transcript

Invoking "say" in a circle adds the line to the transcript of every member brain.

## Scenario: The slider sets the compute share

Releasing the slider at 10 sets the selected circle's share to 10 percent; the surface shows it next to the fixed line "Platform share: 25%".

## Scenario: Sharing an app lists it on the circle

Invoking "share" with "friends calculator" lists calculator under the circle's shared apps; naming an app this
brain does not have is refused on the status line and shares nothing.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "circle";
    public const string Version = "1.0.2";

    public static readonly AppManifest Manifest = new(
        Title: "Circle",
        Description: "Groups of brains that chat, share apps and share compute.",
        Operations:
        [
            new("open", "Compose the circle surface and open its window; input \"docked\" skips the window."),
            new("create", "Create a circle named by the input; this brain owns it."),
            new("invite", "Invite a brain to a circle you own; input \"name brainId\"."),
            new("accept", "Accept the invitation to the circle named by the input."),
            new("say", "Speak in a circle; input \"name text\", or just the text for the selected circle."),
            new("share", "Share one of your apps with a circle you own; input \"name app\"."),
            new("set-share", "Set the compute share (0-25) of a circle you own; input \"name percent\", or a bare percent for the selected circle."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg",
        Toolbar:
        [
            new("set-share", Kind: "slider", Label: "Compute share", Min: 0, Max: 25),
        ]);
}
