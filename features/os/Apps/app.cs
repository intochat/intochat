/*
# Apps

Apps is a value tree the app publishes to {app}/view: the library of installed and available apps with their
connections, or one app's detail with what it asks for. Every control that installs, updates, removes, opens or
binds is a button or select neuron the view references by name, so the tree carries labels and row indexes only.
The durable view state {app}/view-state holds what is shown, the remove confirmation and a toast.

## Scenario: The library
"open" with "" shows the library: Installed, then Available, each row with the app's title, its display name, its
version and a status badge (Installed, Update available, Available, Unavailable) and Details. The publisher the OS
ships as shows as intochat/<app>; a person's apps show as <username>/<app>. Connections follow, with Connect.

## Scenario: An app's detail
"open" with "app:<publisher>/<app>[@<version>]" or "installed:<name>", or Details on a row, shows the app: who made
it, its installed and latest versions, and "This app asks for" its data (semantic types), its connections, secrets
and capabilities. Storage access is shown as "Files and folders". Each connection slot picks one of this brain's
connections of its kind, or adds a new one for this app; the choice is bound on install. An update that adds
requirements says "This update asks for more:" and lists them.
A pinned release that is gone says "This release is no longer available." and offers no Install.

## Scenario: Install, update, open and remove
Install hands the shell the one install request, with the consent digest of what the panel shows and the chosen
bindings; the shell sends it as the person. A stale consent installs nothing and the panel shows again. Remove asks
"Remove {title}?" in a modal; its Remove alone confirms. Open opens the app's window.

## Scenario: Share
Copy link copies the app's absolute link on this host's public origin. Without one the detail says "Sharing isn't
set up on this host."

## Rule: Trust
Only the unedited Apps the OS ships, through its own app proxy and for its own brain, may set an install, update or
remove activation. A link names an app; it never installs one.

Actions the view does not own (copy: actions and older clients' actions) are ignored.
*/

using IntoChat.OS.Modules.Apps;

static class App
{
    public const string Name = "apps";
    public const string Version = "1.0.1";

    public static readonly AppManifest Manifest = new(
        Title: "Apps",
        Description: "Your apps: install, update, share and remove them.",
        Operations:
        [
            new("open", "Show the library for \"\", one app for app:<publisher>/<app>[@<version>], or an installed app for installed:<name>."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg");
}
