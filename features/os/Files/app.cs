/*
# Files

Browse, search and manage every connected storage through storage@1. The durable Files workspace owns navigation,
selection, pagination and operation receipts. Names remain provider-neutral and each item belongs to its exact connection.
Writes fail on conflicts by default, deletes move to trash, and destructive changes wait for the owner's confirmation.
The workspace shows operation progress and cancellation and preserves its current folder across program restarts.

External mutating requests supply ExpectedRevision and Input in their JSON payload; read the workspace Revision first.
Replayed requests and stale revisions cannot produce a second write. Human UI actions carry this revision automatically.
*/
using IntoChat.OS.Modules.Apps;

static class App
{
    public const string Name = "files";
    public const string Version = "3.0.1";
    public static readonly AppManifest Manifest = new(
        Title: "Files",
        Description: "Browse, search, create and manage files across connected storages, with safe confirmations.",
        Operations:
        [
            new("open", "Show connected storages."),
            new("storage", "Show the roots of the storage at Input.Index."),
            new("browse", "Open the indexed folder in the current listing."),
            new("select", "Select the indexed item in the current listing."),
            new("search", "Search names in the current folder, or set Input.Everywhere for all storages."),
            new("more", "Load the next page of results."),
            new("preview", "Open the selected folder or preview the selected file."),
            new("up", "Browse the parent folder."),
            new("mkdir", "Create Input.Name; include ExpectedRevision."),
            new("write", "Save Input.Text as Input.Name; include ExpectedRevision."),
            new("rename", "Rename the selected item to Input.Name; include ExpectedRevision."),
            new("delete", "Move the selected item to trash after confirmation; include ExpectedRevision."),
            new("copy", "Remember the selected source for copying; include ExpectedRevision."),
            new("move", "Remember the selected source for moving; include ExpectedRevision."),
            new("paste", "Copy or move the saved source into this folder; include ExpectedRevision."),
            new("fail", "Use the default stop-on-conflict policy; include ExpectedRevision."),
            new("keep-both", "Keep both items on a name conflict; include ExpectedRevision."),
            new("replace", "Replace conflicts after confirmation; include ExpectedRevision."),
            new("dry-run", "Toggle dry-run planning; include ExpectedRevision."),
            new("cancel", "Cancel the indexed running operation; include ExpectedRevision."),
        ],
        Settings: [], Slots: [], Icon: "icon.svg",
        Toolbar: [new("up", Icon: "arrow-up", Label: "Up"), new("open", Icon: "folder", Label: "Storages")],
        Requires: new(Capabilities: ["storage@1"]));
}
