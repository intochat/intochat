using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps.Facets;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class AppNeuron : IViewFacet, IVfsFacet, IConsentFacet
{
    // IViewFacet
    Task<string> IViewFacet.RenderMainJsonAsync()
    {
        var title = State.Manifest?.Title ?? Name();
        Node node = new Card(title, State.Spec, []);
        return Task.FromResult(JsonSerializer.Serialize(node, RuntimeProtocol.Json));
    }

    Task<string?> IViewFacet.RenderSideJsonAsync() => Task.FromResult<string?>(null);

    async Task<string> IViewFacet.PresentJsonAsync(long expectedProgramRevision, string presentationJson)
    {
        var presentation = JsonSerializer.Deserialize<Presentation>(presentationJson, RuntimeProtocol.Json)!;
        var snapshot = await Present(expectedProgramRevision, presentation);
        return JsonSerializer.Serialize(snapshot, RuntimeProtocol.Json);
    }

    // IVfsFacet
    Task<string[]> IVfsFacet.ListFilesAsync(string path)
    {
        var files = State.Files;
        var normalized = string.IsNullOrWhiteSpace(path) ? "" : path.TrimStart('/');
        var result = files.Keys
            .Where(k => string.IsNullOrEmpty(normalized) || k.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return Task.FromResult(result);
    }

    Task<string?> IVfsFacet.ReadTextAsync(string path)
    {
        var files = State.Files;
        var normalized = string.IsNullOrWhiteSpace(path) ? "" : path.TrimStart('/');
        return Task.FromResult(files.TryGetValue(normalized, out var content) ? content : null);
    }

    Task IVfsFacet.WriteTextAsync(string path, string content)
    {
        var files = new Dictionary<string, string>(State.Files, StringComparer.OrdinalIgnoreCase);
        var normalized = string.IsNullOrWhiteSpace(path) ? "" : path.TrimStart('/');
        files[normalized] = content;
        State.Files = files;
        return Task.CompletedTask;
    }

    Task<bool> IVfsFacet.ExistsAsync(string path)
    {
        var files = State.Files;
        var normalized = string.IsNullOrWhiteSpace(path) ? "" : path.TrimStart('/');
        return Task.FromResult(files.ContainsKey(normalized));
    }

    // IConsentFacet
    private readonly HashSet<string> _consents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, bool> _stagedActions = new();

    Task<bool> IConsentFacet.HasConsentAsync(string capabilityId) =>
        Task.FromResult(_consents.Contains(capabilityId));

    Task IConsentFacet.RequestConsentAsync(string capabilityId, string reason)
    {
        _consents.Add(capabilityId);
        return Task.CompletedTask;
    }

    Task<bool> IConsentFacet.ReviewStagedActionAsync(Guid operationId, bool approved)
    {
        _stagedActions[operationId] = approved;
        return Task.FromResult(approved);
    }
}
