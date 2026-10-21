/*
# Files

Browse every connected storage. A storage is any connection whose connector declares the
`storage` capability; today that is a Windows PC and Google Drive. Startup publishes one
presentation with storages and their roots in side and the filter, listing and text preview in
main. The `open` operation returns the listing to Storages. Each storage connection is listed
explicitly with its kind and status, then its roots (a PC's allowed folders, My Drive), then the
selected folder's contents. Connectors without the capability do not imply file support.

Every storage is reached through the same IStorage neuron on its connection key, with the same
operations: roots, list, read text and breadcrumb ancestors. Each storage enforces its own
confinement; a Windows PC refuses anything outside its allowed roots. Breadcrumbs come from the
storage's own ancestors, so they navigate only through the chosen storage's hierarchy. A name
filter beside the listing narrows results before the 40-entry display limit; the listing reports
when matching entries are omitted. Selection exposes an Open action. Opening a folder browses it;
opening a file attempts a text preview (a Google document previews as plain text). Changing
folders clears the previous location's name filter. The preview identifies the document in its
heading and breadcrumbs; selecting a parent breadcrumb returns to browsing. The Up toolbar action
browses the parent breadcrumb; Storages returns to the storage list. Activating a storage or root
in side browses that location. Files is read only: no write, rename or delete is fabricated.

Reading a `.cs` file renders C# source; if its parent folder matches an installed app, that app's
name, version, status and recent activity appear above it. Unavailable storage, empty folders,
nonmatching filters and failed text previews are observable states. With no storage connected,
the side collection explains how to connect a Windows PC and allow a folder, or connect Google Drive.
*/

using IntoChat.OS.Apps;

static class App
{
    public const string Name = "files";
    public const string Version = "2.1.0";

    public static readonly AppManifest Manifest = new(
        Title: "Files",
        Description: "Browse every connected storage, such as Windows PCs and Google Drive, and preview text files.",
        Operations:
        [
            new("open", "Show every connected storage in Files."),
            new("browse", "List the folder at the input path or location ID (empty input lists storages)."),
            new("read", "Show the text of the file at the input path; a .cs file is shown as C# source."),
            new("up", "Browse the parent breadcrumb of the current location."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg",
        Toolbar:
        [
            new("up", Icon: "arrow-up", Label: "Up"),
            new("open", Icon: "folder", Label: "Storages"),
        ]);
}
