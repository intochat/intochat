/*
# Files

Browse supported storage connections. The stable surface is composed at startup without changing
workspace visibility or focus. Opening the app mounts its declared entry; the `open` operation
returns the listing to Storages. Each connected Windows PC is listed explicitly, then its allowed
roots, then the selected folder's contents. Other connectors do not imply file support.

The file neuron at `{connection}/files` enforces that machine's allowed roots. Breadcrumbs navigate
only through the chosen storage and allowed-root hierarchy. A name filter beside the listing
narrows results before the 40-entry display limit; the surface reports when matching entries are
omitted. Selection exposes an Open action. Opening an entry browses it when it can be listed,
otherwise attempts a text preview. Changing folders clears the previous location's name filter.
The preview identifies the document in its heading and breadcrumbs; selecting a parent breadcrumb
returns to browsing. No rename, delete or cloud-drive capability is fabricated.

Reading a `.cs` file renders C# source; if its parent folder matches an installed app, that app's
name, version, status and recent activity appear above it. Unavailable storage, empty folders,
nonmatching filters and failed text previews are observable states. With no supported connections,
the surface explains how to connect a Windows PC and allow a folder.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "files";
    public const string Version = "1.0.8";

    public static readonly AppManifest Manifest = new(
        Title: "Files",
        Description: "Browse connected Windows PCs, allowed folders and text files.",
        Operations:
        [
            new("open", "Show supported storage connections in Files."),
            new("browse", "List the folder at the input path or location ID (empty input lists storages)."),
            new("read", "Show the text of the file at the input path; a .cs file is shown as C# source."),
        ],
        Settings: [],
        Slots: [],
        Entry: new("surface", "surface"),
        Icon: "icon.svg",
        Toolbar:
        [
            new("open", Icon: "folder", Label: "Storages"),
        ]);
}
