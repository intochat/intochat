/*
# Settings

Settings is a value tree the app publishes to {app}/view: tabs (General, AI models, API keys,
Connections, Variables and secrets, Notifications, Archived chats, Danger zone, then a disabled
Billing · Later) above the active tab's body. Every control that reads or writes a person's data
is a neuron the view references by name, so the tree carries labels and row indexes only. The
durable view state {app}/view-state holds the tab, the archived page, the delete confirmation and
the toast, so a reconnect shows the same tab.

## Scenario: Opening on a tab
Invoking "open" with a tab id shows that tab; an old section name maps as before (agents opens AI
models, profile or appearance opens General) and an empty or unknown one opens General. Desktop's
More > Settings, Classic's Settings and Ino's Add an API key hand the shell {kind: settings, tab?},
and the shell opens this app's window with "open" and that tab. At startup every preference and
entry control is configured and the buttons are armed before the view is presented.

## Scenario: General and Notifications
Display name, Theme, Compact spacing and Reduce motion are device preferences the renderer keeps
on the device; New tab opens and the four notifications are the brain's member preferences, which
the renderer writes as the person. Installed apps cannot write them.

## Scenario: AI models and API keys
AI models lists Auto first, then the brain's catalog; an unavailable model says "Not available.
Add a key in API keys or pick another model." and cannot be the default. API keys holds one key
control per key-based provider. Saving a key changes the key control (models-changed.cs), and the
models and the default-model choices are shown again.

## Scenario: Connections
Connections lists the brain's connections, then each connector this host offers that is not
connected. Connect hands the shell {kind: connections, brainId, connector}: the shell signs in as
the person. A connector the host has not configured says "Not available on this host." The
registry's change signals post "refresh-connections", which shows the list again.

## Scenario: Archived chats
Archived chats lists Ino's archived chats, at most 100 per page with Show more. Unarchive restores
the row and toasts "Chat restored."

## Scenario: Danger zone
Delete account asks "Delete your account?"; Open account page opens the host's public origin at
/account/profile. Without a public origin the button is disabled.

## Rule: Trust
Only the unedited Settings the OS ships may configure the preference and entry controls, and only
it unarchives a chat; an edited Settings, or another app, writes nothing.

## Rule: Key secrecy
A key or secret value never enters the view, a neuron state, a view action, a signal, a log line
or the status text. Values travel only from the renderer's secret field to
PUT /brains/{id}/entries/{name}, over the person's own session, which is owner-only.

## Scenario: Stored preferences for callers
The "read", "save-preferences", "reload-preferences" and "reconcile-preferences" operations stay
for callers: "read" publishes {"displayName", "theme"} to {app}/preferences; a save carries
DisplayName, Theme and the Baseline, is version-checked, rejects a stale or invalid draft and is
idempotent when redelivered.

Actions the view does not own (copy: actions and older clients' actions) are ignored.
*/

using IntoChat.OS.Modules.Apps;

static class App
{
    public const string Name = "settings";
    public const string Version = "2.0.1";

    public static readonly AppManifest Manifest = new(
        Title: "Settings",
        Description: "Your preferences, models, keys, connections and archived chats.",
        Operations:
        [
            new("open", "Show Settings on the tab the input names (general when empty or unknown)."),
            new("read", "Publish the current preferences to {app}/preferences."),
            new("refresh-connections", "Show the brain's connections and the host's connector offers again."),
            new("save-preferences", "Save DisplayName and Theme with the Baseline snapshot from preferences/baseline. Reject stale drafts and preserve invalid drafts."),
            new("reload-preferences", "Discard editable drafts and load the stored preferences."),
            new("reconcile-preferences", "Refresh clean drafts from stored preferences; retain unsaved edits and report conflicts."),
        ],
        Settings: [],
        Slots: []);
}
