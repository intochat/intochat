using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps.Facets;

internal sealed class AppViewFacet(AppNeuron app, Func<AppState> stateAccessor) : IViewFacet
{
    public Task<string> RenderMainJsonAsync()
    {
        var state = stateAccessor();
        var title = state.Manifest?.Title ?? "App View";
        Node node = new Card(title, state.Spec, []);
        return Task.FromResult(JsonSerializer.Serialize(node, RuntimeProtocol.Json));
    }

    public Task<string?> RenderSideJsonAsync() => Task.FromResult<string?>(null);

    public async Task<string> PresentJsonAsync(long expectedProgramRevision, string presentationJson)
    {
        var presentation = JsonSerializer.Deserialize<Presentation>(presentationJson, RuntimeProtocol.Json)!;
        var snapshot = await app.Present(expectedProgramRevision, presentation);
        return JsonSerializer.Serialize(snapshot, RuntimeProtocol.Json);
    }
}

internal sealed class AppVfsFacet(Func<AppState> stateAccessor) : IVfsFacet
{
    public Task<string[]> ListFilesAsync(string path)
    {
        var files = stateAccessor().Files;
        var normalized = string.IsNullOrWhiteSpace(path) ? "" : path.TrimStart('/');
        var result = files.Keys
            .Where(k => string.IsNullOrEmpty(normalized) || k.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<string?> ReadTextAsync(string path)
    {
        var files = stateAccessor().Files;
        var normalized = string.IsNullOrWhiteSpace(path) ? "" : path.TrimStart('/');
        return Task.FromResult(files.TryGetValue(normalized, out var content) ? content : null);
    }

    public Task WriteTextAsync(string path, string content)
    {
        var state = stateAccessor();
        var files = new Dictionary<string, string>(state.Files, StringComparer.OrdinalIgnoreCase);
        var normalized = string.IsNullOrWhiteSpace(path) ? "" : path.TrimStart('/');
        files[normalized] = content;
        state.Files = files;
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path)
    {
        var files = stateAccessor().Files;
        var normalized = string.IsNullOrWhiteSpace(path) ? "" : path.TrimStart('/');
        return Task.FromResult(files.ContainsKey(normalized));
    }
}

internal sealed class AppConsentFacet : IConsentFacet
{
    private readonly HashSet<string> _consents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, bool> _stagedActions = new();

    public Task<bool> HasConsentAsync(string capabilityId) =>
        Task.FromResult(_consents.Contains(capabilityId));

    public Task RequestConsentAsync(string capabilityId, string reason)
    {
        _consents.Add(capabilityId);
        return Task.CompletedTask;
    }

    public Task<bool> ReviewStagedActionAsync(Guid operationId, bool approved)
    {
        _stagedActions[operationId] = approved;
        return Task.FromResult(approved);
    }
}
