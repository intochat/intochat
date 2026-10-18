/*
# BrainView

The brain's behavior-run panel. Home is the brain's field; this app publishes its detail panel
as main content without a separate scene. The panel shows the latest ten brain-wide behavior
runs, newest first, including each behavior key, outcome, duration and trace. Behavior runs remain
recorded in the brain runtime; the old {app}/runs/recent log remains readable for compatibility.
The script renders one immutable view tree from the history snapshot.

## Scenario: Opening the brain view shows the panel

Startup and "open" publish /view with the app sphere. The run subscriber owns its rendered content.
*/
using IntoChat.OS.Apps;

static class App
{
    public const string Name = "brainview";
    public const string Version = "1.1.1";
    public static readonly AppManifest Manifest = new(
        Title: "Brain",
        Description: "Inspect the brain's behavior-run panel.",
        Operations: [new("open", "Compose and publish the behavior-run panel.")],
        Settings: [], Slots: [],
        Icon: "icon.svg");
}
