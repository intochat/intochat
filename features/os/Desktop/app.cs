/*
# Desktop
The v2 root: windows side by side above a dock (apps left; usage and profile right).
On narrow screens the focused window fills the screen. One startup signal: the
desktop's `start` request composes it from the workspace state and installed apps;
start.cs alone handles it. The layout text "classic" keeps the v1 Workspace as the root.

## Scenario: Starting shows the dock
Invoking "start" publishes the desktop view with a dock row, Ino pinned first.

## Scenario: Windows side by side
Clicking a dock app (`open:<app>`) adds its window beside the others; `focus:<id>` and
`close:<id>` act on one window; restore.cs re-renders on every workspace change, so a
restarted desktop shows the same windows and focus. Below 900 logical pixels the shell sends
the `narrow` request (true; false when wider again) and only the focused window shows.

## Scenario: Apps gallery
The dock holds Ino, pinned and open apps only. `gallery` shows or hides the gallery
(Built-in, Mine, From others): installed entries Open, others Get (`get:<publisher>/<name>`),
which installs for real and opens the app in a window, or shows why not.

## Scenario: Connect
Connections (Gmail and Salesforce first) sit under From others. Connect is a button whose
activation hands the shell {kind: connections, connector}: the shell's connection window signs
in as the user (no app starts a login). After the callback stores the connection, connected.cs
re-renders and the card reads Connected. A connector this host has not configured shows
"Not available on this host."

## Scenario: Classic layout
More holds Settings (`settings` opens the Settings app in a window) and Classic layout, a button
whose activation hands the shell {kind: reconnect} with the `layout` request: layout-switch.cs
stores "classic" or "desktop" in the desktop's layout text and the shell connects again, so the
next root is the v1 Workspace (or the desktop). Open views are the same workspace either way.
*/
using IntoChat.OS.Apps;
static class App
{
    public const string Name = "desktop";
    public const string Version = "0.1.0";
    public static readonly AppManifest Manifest = new(
        Title: "Desktop", Description: "Your apps in windows, with a dock.",
        Operations: [new("start", "Compose the desktop from the open windows and installed apps."),
            new("layout", "Choose the layout the next connect shows: desktop or classic."),
            new("narrow", "The shell's width hint: true below 900 logical pixels, false otherwise.")],
        Settings: [], Slots: [], Icon: "icon.svg");
}
