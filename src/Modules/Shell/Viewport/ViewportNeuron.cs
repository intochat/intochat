using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.State;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.Flutter.Viewport;

[Neuron("viewport")]
internal sealed class ViewportNeuron
    : UiNeuron<ViewportStorage>, IViewport
{
    public Task<ViewportState> Read() => Task.FromResult(new ViewportState(FullKey, Snapshot.Revision, Snapshot.Target));

    public async Task Set(WorkspaceViewRef? target, long expectedRevision)
    {
        var name = FullKey;
        if (!Id.Key.StartsWith("apps/", StringComparison.Ordinal)) { throw new ArgumentException("A viewport belongs to an app."); }
        var afterApps = Id.Key["apps/".Length..];
        var appSegmentEnd = afterApps.IndexOf('/');
        var appSegment = appSegmentEnd < 0 ? afterApps : afterApps[..appSegmentEnd];
        if (!IntoChat.OS.Primitives.AppRef.TryOf(Id.Brain, appSegment, out var ownerRef))
        { throw new ArgumentException("A viewport belongs to an app."); }
        var owner = ownerRef.Key;
        RequireOwner(ownerRef);
        if (target is not null && (string.IsNullOrWhiteSpace(target.Id) || target.Id.Length > 128
            || string.IsNullOrWhiteSpace(target.Variant) || target.Variant.Length > 64 || target.Context != ""
            || string.IsNullOrWhiteSpace(target.App) || target.App.Length > 512
            || !IsSupportedTarget(Id.Brain, target.App)
            // Never its own app: that app's view may contain this viewport and recurse.
            || target.App == owner))
        { throw new ArgumentException("This viewport supports local app views without an external context."); }
        if (Snapshot.Revision != expectedRevision)
        { throw new RevisionConflictException("Read the current viewport before changing its target.", Snapshot.Revision); }
        if (Snapshot.Target == target) { return; }
        var next = new ViewportStorage { Revision = checked(Snapshot.Revision + 1), Target = target };
        await Save(next, new ViewportChanged(name, next.Revision));
    }

    private void RequireOwner(IntoChat.OS.Primitives.AppRef ownerRef)
    {
        var contextCaller = Context.Caller;
        if (contextCaller is not null)
        {
            if (contextCaller.Brain != Id.Brain)
            {
                throw new UnauthorizedAccessException("Only callers in the owning brain may change this viewport.");
            }

            if (contextCaller.Actor is DigitalBrain.Access.Actor.App appActor
                && appActor.Key != ownerRef.Key
                && !(contextCaller.Inside && contextCaller.Via == ownerRef.ScreenNeuron))
            {
                throw new UnauthorizedAccessException("Only the owning app may change this viewport.");
            }

            if (contextCaller.Inside
                && contextCaller.Via is { } via
                && via.Type.StartsWith("app.", StringComparison.Ordinal)
                && (via.Brain != Id.Brain || via != ownerRef.ScreenNeuron))
            {
                throw new UnauthorizedAccessException("Only the owning screen may change this viewport.");
            }
        }
    }

    private static bool IsSupportedTarget(BrainId brainId, string app)
    {
        if (IntoChat.OS.Primitives.AppRef.TryParse(brainId, app, out _)) { return true; }
        if (app.StartsWith(brainId.Value + "/units/", StringComparison.Ordinal))
        {
            return IntoChat.OS.Primitives.UnitAlias.TryFromAddress(brainId, app, out _);
        }
        return AppUiEndpoints.UnitAlias(brainId.Value, app) is { } alias
            && alias is not ("." or ".." or "units" or "profile" or "posts" or "main" or "confirmations");
    }
}

[GenerateSerializer, Alias("ui.viewport-storage")]
[ExportableState]
internal sealed class ViewportStorage
{
    [Id(0)] public long Revision { get; set; }
    [Id(1)] public WorkspaceViewRef? Target { get; set; }
}
