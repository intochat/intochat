using DigitalBrain.Errors;
using System.Text.Json;
using IntoChat.OS;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flutter;

namespace IntoChat.OS.Modules.Apps;

internal static class FilesRequests
{
    public static AppBundle Bundle { get; } = new(
        "files", "3.0.1",
        new AppManifest(
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
                new("ask-ino", "Open Ino with the selected file's neuron:// reference pre-filled."),
            ],
            Settings: [], Slots: [],
            Toolbar: [new("up", Icon: "arrow-up", Label: "Up"), new("open", Icon: "folder", Label: "Storages")],
            Requires: new(Capabilities: ["storage@1"])),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal));

    public static Presentation Presentation(string appKey)
        => new(new Sphere("icon.svg", []), Side: new(UIVocabulary.ViewType, appKey + "/side"), Main: new(UIVocabulary.ViewType, appKey + "/main"),
            Views: [
                new("full", new(UIVocabulary.ViewType, appKey + "/full")),
                new("view", new(UIVocabulary.ViewType, appKey + "/full")),
            ]);

    public static async Task<string?> Forward(IFilesWorkspace workspace, AppRequested request, CancellationToken ct)
    {
        try
        {
            var payload = string.IsNullOrWhiteSpace(request.Input) ? new Forwarded()
                : JsonSerializer.Deserialize<Forwarded>(request.Input, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            var readOnly = request.Operation is "open" or "storage" or "browse" or "select" or "search" or "more" or "preview" or "up" or "ask-ino";
            if (!readOnly && payload.ExpectedRevision is null)
            { return "Read the current Files workspace revision before changing files."; }
            await workspace.Request(request.Operation, payload.Input ?? new(), payload.ExpectedRevision ?? await workspace.Revision(), request.RequestId, ct);
            return null;
        }
        catch (StaleActionException) { return "Files changed. Read the current view before trying again."; }
        catch (JsonException) { return "The Files request needs an ExpectedRevision and Input payload."; }
    }

    private sealed record Forwarded(long? ExpectedRevision = null, FilesWorkspaceInput? Input = null);
}
