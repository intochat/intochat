/*
# Desktop
The v2 root: windows side by side above a dock (apps left; usage and profile right).
On narrow screens the focused window fills the screen. One startup signal: the
desktop's `start` request composes it from the workspace state and installed apps;
start.cs alone handles it. The layout text "classic" keeps the v1 Workspace as the root.

## Scenario: Starting shows the dock
Invoking "start" publishes the desktop view with a dock row, Ino pinned first.

## Scenario: Windows side by side
Clicking a dock app (`open:<app>`) adds its window beside the others; `focus:<window>` and
`close:<window>/<tab>` act on one window; restore.cs re-renders on every workspace change, so a
restarted desktop shows the same windows and focus. Below 900 logical pixels the shell sends
the `narrow` request (true; false when wider again) and only the focused window shows.

## Scenario: Tabs and favourites
A window holds tabs (at most 8; at most 8 windows): its header shows the active tab's title with
New tab, favourite, pin and close icon buttons, and the tab strip beneath. `tab:<w>/<i>` selects a
tab; `newtab:<w>` (a window's +) and `newtab` (the dock, into the focused window) open the brain's
New tab app, Ino unless Settings chose another; `close:<w>/<i>` closes one tab, and the last tab
closes its window. `favourite:<w>` stars the active tab: favourites (at most 5 apps and 5 views)
follow Ino in the dock, hidden when empty and a menu when narrow; `view:<id>` opens a view
favourite as a new tab. A refusal (a cap reached) shows a toast above the dock until the next change.

## Scenario: Apps opens the Apps window
The dock holds Ino, pinned and open apps only. Its Apps button (`open:apps`) opens the Apps app's window beside the
others, or focuses it: the library, details, installs and connections live there. The desktop has no gallery.

The same holds for a workspace activation (Settings, an Apps detail, a finished install): while the desktop is the
root, the Workspace app hands a new window to the desktop's `open` request, so the cap and its toast apply there too.

## Scenario: Settings opens the Settings window on a tab
More's Settings is a button whose activation hands the shell {kind: workspace, brainId, application: settings,
operation: open, input: <tab>} (General when none): the root's activation receiver, the Workspace app, opens or
focuses the Settings window and sends the Settings app its "open" request with the tab. A legacy {kind: settings, tab}
from an older button is translated by the shell to the same activation.

## Scenario: Classic layout
More holds Settings (above) and Classic layout, a button
whose activation hands the shell {kind: reconnect} with the `layout` request: layout-switch.cs
stores "classic" or "desktop" in the desktop's layout text and the shell connects again, so the
next root is the v1 Workspace (or the desktop). Open views are the same workspace either way.
*/
using IntoChat.OS.Modules.Apps;
static class App
{
    public const string Name = "desktop";
    public const string Version = "0.3.0";
    public static readonly AppManifest Manifest = new(
        Title: "Desktop", Description: "Your apps in windows, with a dock.",
        Operations: [new("start", "Compose the desktop from the open windows and installed apps."),
            new("layout", "Choose the layout the next connect shows: desktop or classic."),
            new("open", "Open an app's window beside the others, or focus it (input: the app name). Enforces the window cap."),
            new("narrow", "The shell's width hint: true below 900 logical pixels, false otherwise.")],
        Settings: [], Slots: [], Icon: "icon.svg");
}
