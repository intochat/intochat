using System.Text.Json;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Flame;
using DigitalBrain.Flutter;
using DigitalBrain.Framework;

namespace DigitalBrain.Apps;

internal sealed partial class AppNeuron
{
    public async Task<AppSnapshot> Present(long expectedProgramRevision, Presentation presentation)
    {
        RequireExecutingAssignment(expectedProgramRevision);
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(presentation.Sphere);
        ArgumentNullException.ThrowIfNull(presentation.Sphere.BlackboardKeys);
        foreach (var slot in new[] { presentation.Side, presentation.Main }.OfType<UiChildRef>())
        {
            if (string.IsNullOrWhiteSpace(slot.Name) || !slot.Name.StartsWith(Snapshot.BrainScopeId + "/", StringComparison.Ordinal)
                || slot.Name.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            { throw new ArgumentException("A slot names a neuron of this brain.", nameof(presentation)); }
        }
        if (presentation.Sphere.BlackboardKeys.Any(key => string.IsNullOrWhiteSpace(key) || FlameSchema.ReservedBlackboardKeys.Contains(key, StringComparer.Ordinal)))
        { throw new ArgumentException("Declare nonempty keys; running and attention are reserved blackboard keys.", nameof(presentation)); }
        if (presentation.Sphere.Tree is { } tree) { FlameSchema.CheckTree(tree, presentation.Sphere.BlackboardKeys); }
        var current = Snapshot.Presentation;
        if (current is { Root: { } root, Error: null } && JsonSerializer.Serialize(root) == JsonSerializer.Serialize(presentation))
        { return Describe(Snapshot); }
        var keys = presentation.Sphere.BlackboardKeys.Concat(FlameSchema.ReservedBlackboardKeys).ToHashSet(StringComparer.Ordinal);
        var blackboard = (current?.Blackboard ?? []).Where(pair => keys.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal);
        await SavePresentation(new(Snapshot.ProgramRevision, (current?.Revision ?? 0) + 1, presentation, null, blackboard));
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> FailPresentation(long expectedProgramRevision, string error)
    {
        RequireExecutingAssignment(expectedProgramRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        var current = Snapshot.Presentation;
        if (current is { Root: null } && current.Error == error) { return Describe(Snapshot); }
        await SavePresentation(new(Snapshot.ProgramRevision, (current?.Revision ?? 0) + 1, null, error, new(current?.Blackboard ?? [], StringComparer.Ordinal)));
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> WriteBlackboard(long expectedProgramRevision, Dictionary<string, string> values)
    {
        RequireExecutingAssignment(expectedProgramRevision);
        ArgumentNullException.ThrowIfNull(values);
        var current = Snapshot.Presentation ?? throw new InvalidOperationException("Publish a presentation before writing its blackboard.");
        foreach (var key in values.Keys)
        {
            if (FlameSchema.ReservedBlackboardKeys.Contains(key, StringComparer.Ordinal)
                || current.Root?.Sphere.BlackboardKeys.Contains(key, StringComparer.Ordinal) != true)
            { throw new ArgumentException($"'{key}' is not a script-writable declared blackboard key.", nameof(values)); }
        }
        await WriteValues(current, values);
        return Describe(Snapshot);
    }

    public async Task Stamp(string key, string value)
    {
        if (CallerContextStamper.TryGet(out var caller) && caller.Kind != CallerKind.Platform)
        { throw new UnauthorizedAccessException("Only the module may stamp reserved blackboard keys."); }
        RequireCreated();
        if (!FlameSchema.ReservedBlackboardKeys.Contains(key, StringComparer.Ordinal))
        { throw new ArgumentException("Only running and attention are module-owned keys.", nameof(key)); }
        var current = Snapshot.Presentation ?? new(Snapshot.ProgramRevision, 0, null, null, new(StringComparer.Ordinal));
        await WriteValues(current, new() { [key] = value });
    }

    private async Task WriteValues(AppPresentation current, Dictionary<string, string> values)
    {
        if (values.All(pair => current.Blackboard.TryGetValue(pair.Key, out var held) && held == pair.Value)) { return; }
        var blackboard = new Dictionary<string, string>(current.Blackboard, StringComparer.Ordinal);
        foreach (var (key, value) in values) { blackboard[key] = value; }
        await SavePresentation(current with { Revision = current.Revision + 1, Blackboard = blackboard });
    }

    private Task SavePresentation(AppPresentation presentation)
    {
        var next = Snapshot.Clone();
        next.Presentation = presentation;
        return Save(next, new PresentationChanged(this.GetPrimaryKeyString(), next.ProgramRevision, presentation.Revision));
    }

    private void RequireExecutingAssignment(long expectedProgramRevision)
    {
        if (!CallerContextStamper.TryGet(out var caller) || caller.Kind != CallerKind.App
            || !string.Equals(caller.AppId, this.GetPrimaryKeyString(), StringComparison.Ordinal))
        { throw new UnauthorizedAccessException("Only this app's executing assignment may publish its presentation."); }
        RequireCreated();
        if (expectedProgramRevision != Snapshot.ProgramRevision)
        { throw new InvalidOperationException("The program was replaced; this assignment may not publish."); }
    }
}
