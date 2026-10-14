/*
# BrainView

The brain's behavior-run panel. Home is the brain's field; this app publishes its detail panel
as main content without a separate scene. The panel shows the latest ten brain-wide behavior
runs, newest first, including each behavior key, outcome, duration and trace. Behavior runs remain
recorded in {app}/runs/recent; composition restores the visible list from that durable history.

## Scenario: Opening the brain view shows the panel

Startup and "open" compose the /panel column in /main and publish it with the app sphere.
*/
using DigitalBrain.Apps;

static class App
{
    public const string Name = "brainview";
    public const string Version = "1.0.4";
    public static readonly AppManifest Manifest = new(
        Title: "Brain",
        Description: "Inspect the brain's behavior-run panel.",
        Operations: [new("open", "Compose and publish the behavior-run panel.")],
        Settings: [], Slots: [],
        Icon: "icon.svg");
}
