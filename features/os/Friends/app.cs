/*
# Friends

Friends shows the circles you belong to. Profiles and posts are neurons in their author's personal
brain; the workspace derives Friend and Post spheres through current shared-circle membership.
Startup and open seed an empty profile as Me and publish circles in side and an explanation in main.
Post writes one card and one own-index entry under the operation request id, then publishes that
reference into each current circle through its guarded PublishPost operation. Retained request replay
reuses the same card, index entry and durable circle receipt. A removed member cannot publish or read
shared slots; only posts actually published in a current shared circle appear to another member.
*/
using DigitalBrain.Apps;

static class App
{
    public const string Name = "friends";
    public const string Version = "1.0.0";
    public static readonly AppManifest Manifest = new(
        Title: "Friends", Description: "People and posts from your circles.",
        Operations: [new("open", "Publish the Friends presentation."), new("post", "Post the input text to every circle you are in.")],
        Settings: [], Slots: [], Icon: "icon.svg",
        Toolbar: [new("post", Kind: AppToolbarKinds.Text, Label: "Post")]);
}
