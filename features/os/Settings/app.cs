/*
# Settings

The primary Settings layout presents credential-free connections and installed app bindings.
Refresh is an explicit control above a scrolling list. The workspace shell routes its Settings
action to this installed app. Profile > Workspace configuration retains working appearance,
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

## Scenario: Opening Settings shows the connections surface

Invoking "open" labels the Refresh connections button, composes the main column with that button
above the connections list and publishes the preferences neuron in the main layout.

## Scenario: Read publishes the stored preferences

Invoking "read" sets the preferences neuron to {"displayName", "theme"} from the stored values, with
a theme of system, light or dark.

## Scenario: Refresh lists connections and app bindings without duplicates

Invoking "refresh-connections" sets the connections layout to a list of cards: the intro, one card
per connection (or "No connections yet"), "App connections" with one card per installed app, and
"Available connector types" with one card per connector kind. Card bodies never carry credentials.
Invoking it again yields the same card keys, each once.

## Scenario: The Refresh connections button rebuilds the list

Clicking the Refresh connections button posts "refresh-connections": the connections layout's
revision advances and its card keys stay the same.

## Scenario: Reload then save stores the preferences

Invoking "reload-preferences" copies the stored name and theme into the drafts and the baseline and
reports "Stored preferences loaded.". Invoking "save-preferences" with DisplayName, Theme and that
Baseline stores the edited fields and reports "Preferences saved."; "read" then publishes them.
Delivering that same save again (at-least-once) still reports "Preferences saved.".

## Scenario: A stale or invalid save is rejected

Invoking "save-preferences" with a Baseline older than the stored values reports "Preferences
changed elsewhere. Reload preferences before saving your changes." and a theme other than system,
light or dark reports the validation message; the stored preferences stay unchanged. Invoking
"reconcile-preferences" while the draft holds unsaved edits keeps the draft.
Presentation: startup publishes one sphere and its main content using stable neuron identities.
Workspace navigation owns focus; composition never changes it.

*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "settings";
    public const string Version = "1.1.9";

    public static readonly AppManifest Manifest = new(
        Title: "Settings",
        Description: "Available connections and the connections used by your apps.",
        Operations:
        [
            new("open", "Compose the settings surface and publish the current preferences to {app}/preferences."),
            new("read", "Publish the current preferences to {app}/preferences."),
            new("refresh-connections", "Rebuild this brain's credential-free connection summaries and installed app bindings as cards."),
            new("save-preferences", "Save DisplayName and Theme with the Baseline snapshot from preferences/baseline. Reject stale drafts and preserve invalid drafts."),
            new("reload-preferences", "Discard editable drafts and load the stored preferences."),
            new("reconcile-preferences", "Refresh clean drafts from stored preferences; retain unsaved edits and report conflicts."),
        ],
        Settings: [],
        Slots: []);
}
