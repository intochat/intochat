using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps.Facets;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class AppNeuron : IViewFacet, IVfsFacet, IConsentFacet
{
    // IViewFacet
    Task<Node> IViewFacet.RenderMainAsync()
    {
        var title = State.Manifest?.Title ?? Name();
        return Task.FromResult<Node>(new Card(title, State.Spec, []));
    }

    Task<Node?> IViewFacet.RenderSideAsync() => Task.FromResult<Node?>(null);

    Task<AppSnapshot> IViewFacet.PresentAsync(long expectedProgramRevision, Presentation presentation) =>
        Present(expectedProgramRevision, presentation);

    // IVfsFacet
    Task<IReadOnlyList<string>> IVfsFacet.ListFilesAsync(string path)
    {
        var files = State.Files;
        var normalized = string.IsNullOrWhiteSpace(path) ? "" : path.TrimStart('/');
        var result = files.Keys
            .Where(k => string.IsNullOrEmpty(normalized) || k.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(result);
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
