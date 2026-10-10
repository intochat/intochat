/*
# Settings

The primary Settings surface presents credential-free connections and installed app bindings.
Refresh is an explicit control above a scrolling list. The workspace shell routes its Settings
entry to this installed app. Profile > Workspace configuration retains working appearance,
profile, model, account authorization and advanced controls.

The existing brain preference read/save operations and durable values remain available for
callers, but their fields are not presented as shell appearance settings: they are separate
from the shell's configuration. The preference behavior retains validation, version-checked
writes and explicit conflict/reload recovery for those operations.

Connection refresh reads the brain connection registry and installed app bindings. It distinguishes
connected accounts, available connector capabilities, deployment integration configuration and
missing app configuration. Cards show provider, connection identity, status and consuming apps,
never credentials. Loading, empty and retryable error states replace the projected list. Repeated
refresh uses stable card keys and does not duplicate connections. A separate behavior owns each
of surface composition, preference persistence and connection discovery.

Connection status and membership changes publish a credential-free catalog invalidation signal.
Settings observes it and pending-authorization requests, then invokes the same serialized refresh
operation used by the button. Repeated delivery rebuilds stable card keys without duplicates.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "settings";
    public const string Version = "1.1.6";

    public static readonly AppManifest Manifest = new(
        Title: "Settings",
        Description: "Available connections and the connections used by your apps.",
        Operations:
        [
            new("open", "Compose the settings surface and publish the current preferences to {app}/preferences; input \"docked\" skips the window."),
            new("read", "Publish the current preferences to {app}/preferences."),
            new("refresh-connections", "Rebuild this brain's credential-free connection summaries and installed app bindings as cards."),
            new("save-preferences", "Save DisplayName and Theme with the Baseline snapshot from preferences/baseline. Reject stale drafts and preserve invalid drafts."),
            new("reload-preferences", "Discard editable drafts and load the stored preferences."),
            new("reconcile-preferences", "Refresh clean drafts from stored preferences; retain unsaved edits and report conflicts."),
        ],
        Settings: [],
        Slots: []);
}
