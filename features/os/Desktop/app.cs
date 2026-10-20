/*
# Desktop
The v2 root: windows side by side above a dock (apps left; usage and profile right).
On narrow screens the focused window fills the screen. One startup signal: the
desktop's `start` request composes it from the workspace state and installed apps;
start.cs alone handles it. Layout "classic" keeps the v1 Workspace as the root.

## Scenario: Starting shows the dock
Invoking "start" publishes the desktop view with a dock row, Ino pinned first.

## Scenario: Windows side by side
Clicking a dock app (`open:<app>`) adds its window beside the others; `focus:<id>` and
`close:<id>` act on one window; restore.cs re-renders on every workspace change, so a
restarted desktop shows the same windows and focus. `narrow:<true|false>` shows only the
focused window.
*/
using IntoChat.OS.Apps;
static class App
{
    public const string Name = "desktop";
    public const string Version = "0.1.0";
    public static readonly AppManifest Manifest = new(
        Title: "Desktop", Description: "Your apps in windows, with a dock.",
        Operations: [new("start", "Compose the desktop from the open windows and installed apps.")],
        Settings: [new("Layout", "desktop or classic", "desktop")], Slots: [], Icon: "icon.svg");
}
