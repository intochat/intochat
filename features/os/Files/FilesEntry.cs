using System.Text.Json;
using IntoChat.OS;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flutter;

internal static class FilesEntry
{
    public static Presentation Presentation(string appKey)
        => new(new Sphere("icon.svg", []), Side: new(UIVocabulary.ViewType, appKey + "/side"), Main: new(UIVocabulary.ViewType, appKey + "/main"),
            Views: [new("full", new(UIVocabulary.ViewType, appKey + "/full"))]);

    public static async Task<string?> Forward(IFilesWorkspace workspace, AppRequested request, CancellationToken ct)
    {
        try
        {
            var payload = string.IsNullOrWhiteSpace(request.Input) ? new ForwardedRequest()
                : JsonSerializer.Deserialize<ForwardedRequest>(request.Input, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            var readOnly = request.Operation is "open" or "storage" or "browse" or "select" or "search" or "more" or "preview" or "up";
            if (!readOnly && payload.ExpectedRevision is null)
            { return "Read the current Files workspace revision before changing files."; }
            await workspace.Request(request.Operation, payload.Input ?? new(), payload.ExpectedRevision ?? await workspace.Revision(), request.RequestId, ct);
            return null;
        }
        catch (StaleActionException) { return "Files changed. Read the current view before trying again."; }
        catch (JsonException) { return "The Files request needs an ExpectedRevision and Input payload."; }
    }

    private sealed record ForwardedRequest(long? ExpectedRevision = null, FilesWorkspaceInput? Input = null);
}
