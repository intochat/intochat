using System.Text.Json;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Flutter.Views;

[GrainType(UIVocabulary.ViewType)]
internal sealed class ViewNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<ViewState> store)
    : Neuron<ViewState>(store), IView
{
    public Task Publish(UiView view)
    {
        RequireWriter();
        var next = ViewValidation.Clone(view, this.GetPrimaryKeyString());
        if (Snapshot.View is { } current)
        {
            if (next.Generation < current.Generation || next.Generation == current.Generation && next.Version < current.Version) { return Task.CompletedTask; }
            if (next.Generation == current.Generation && next.Version == current.Version)
            {
                if (JsonSerializer.Serialize(next, ViewValidation.Json) != JsonSerializer.Serialize(current, ViewValidation.Json)) { throw new InvalidOperationException("A published version cannot change."); }
                return Task.CompletedTask;
            }
        }
        return Save(new() { View = next }, new ViewChanged(this.GetPrimaryKeyString(), next.Generation, next.Version));
    }

    [ReadOnly] public Task<UiView?> Read() => Task.FromResult(Snapshot.View is { } view ? ViewValidation.Clone(view, this.GetPrimaryKeyString()) : null);

    public Task Activate(long generation, string key, string expectedAction, Guid requestId)
    {
        if (requestId == Guid.Empty) { throw new ArgumentException("A request id is required.", nameof(requestId)); }
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAction);
        var view = Snapshot.View;
        var node = view is null ? null : Find(view.Root, key);
        if (view?.Generation != generation || node is not { Kind: UiNodeKind.Button, Enabled: true } || !string.Equals(node.Action, expectedAction, StringComparison.Ordinal))
        { throw new StaleActionException("This action is no longer available. Read the view again."); }
        return PublishAsync(new ViewAction(this.GetPrimaryKeyString(), node.Key, node.Action!, requestId, view.Generation));
    }

    private static UiNode? Find(UiNode root, string key) => root.Key == key ? root : root.Children.Select(child => Find(child, key)).FirstOrDefault(node => node is not null);

    private void RequireWriter()
    {
        if (!CallerContextStamper.TryGet(out var caller) || caller.Kind == CallerKind.Platform) { return; }
        var name = this.GetPrimaryKeyString();
        if (!BrainKey.BelongsTo(name, caller.BrainId) || caller.Kind == CallerKind.App &&
            (string.IsNullOrEmpty(caller.AppId) || !name.StartsWith(caller.AppId + "/", StringComparison.Ordinal)))
        { throw new UnauthorizedAccessException("Only the owning brain and app may publish this view."); }
    }
}

[GenerateSerializer, Alias("ui.view-state")]
[ExportableState]
internal sealed class ViewState
{
    [Id(0)] public UiView? View { get; set; }
}

internal static class ViewValidation
{
    internal static readonly JsonSerializerOptions Json = new() { MaxDepth = 128 };
    // Only leaf controls can be embedded: layouts and views can themselves refer to other nodes.
    // A viewport is the one window: it shows an app's view chosen by its owner (the desktop's windows).
    private static readonly HashSet<string> ReferenceKinds = new(StringComparer.Ordinal)
    { "viewport", "chat", "composer", "text", "button", "textfield", "select", "fileinput", "imagecanvas", "form", "table", "chart", "voiceinput", "webbrowser", "toggle", "progress", "infobar", "image" };

    public static UiView Clone(UiView view, string name)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.Generation < 0 || view.Version < 1) { throw new ArgumentException("Generation must be nonnegative and version positive."); }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var path = new HashSet<UiNode>(ReferenceEqualityComparer.Instance);
        var count = 0;
        var copy = view with { Root = Copy(view.Root, 1) };
        if (JsonSerializer.SerializeToUtf8Bytes(copy, Json).Length > 1_048_576) { throw new ArgumentException("A view is limited to 1 MiB."); }
        return copy;

        UiNode Copy(UiNode node, int depth)
        {
            if (node is null || depth > 32 || ++count > 512 || !path.Add(node)) { throw new ArgumentException("A view must be an acyclic tree of at most 512 nodes and depth 32."); }
            if (!Enum.IsDefined(node.Kind) || string.IsNullOrWhiteSpace(node.Key) || node.Key.Length > 256 || !keys.Add(node.Key)) { throw new ArgumentException("View nodes require unique nonempty keys and known kinds."); }
            if (node.Children is null || !double.IsFinite(node.Gap) || node.Gap < 0) { throw new ArgumentException("Invalid view layout."); }
            if (node.Extents is { } extents && (extents.Length != node.Children.Length || extents.Any(value => !double.IsFinite(value) || value < 0))) { throw new ArgumentException("Extents must match children and be finite nonnegative values."); }
            if (node.Kind is UiNodeKind.Text or UiNodeKind.Button or UiNodeKind.Reference && node.Children.Length != 0) { throw new ArgumentException("Leaf nodes cannot have children."); }
            if (node.Kind == UiNodeKind.Button && (string.IsNullOrWhiteSpace(node.Text) || string.IsNullOrWhiteSpace(node.Action))) { throw new ArgumentException("Buttons require text and a canonical action."); }
            if (node.Kind != UiNodeKind.Button && node.Action is not null) { throw new ArgumentException("Only buttons have actions."); }
            if (node.Kind == UiNodeKind.Reference)
            {
                if (node.Reference is not { } reference || !ReferenceKinds.Contains(reference.Kind) || string.IsNullOrWhiteSpace(reference.Name) || !BrainKey.BelongsTo(reference.Name, BrainKey.BrainOf(name)))
                { throw new ArgumentException("References must name a supported leaf control in the same brain."); }
            }
            else if (node.Reference is not null) { throw new ArgumentException("Only reference nodes may embed controls."); }
            var result = node with { Children = node.Children.Select(child => Copy(child, depth + 1)).ToArray(), Extents = node.Extents?.ToArray(), Reference = node.Reference is { } childRef ? childRef with { } : null };
            path.Remove(node);
            return result;
        }
    }
}
