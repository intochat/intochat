using System.Text.Json;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Flame;
using IntoChat.OS.Modules.Flutter.Views;

namespace IntoChat.OS.Modules.Apps.Presentations;

internal sealed class AppPresentation(AppState state)
{
    public AppState State => state;

    public UiNode Render(View view, string? brainId = null)
        => MapView(view, brainId ?? state.BrainScopeId, AppTrust.IsOsTrusted(state.Settings));

    public AppState? Publish(IntoChat.OS.Modules.Apps.Presentation spec)
    {
        var current = state.Presentation;
        if (current is { Root: { } root, Error: null } && JsonSerializer.Serialize(root) == JsonSerializer.Serialize(spec))
        {
            return null;
        }

        var declaredKeys = spec.Sphere?.BlackboardKeys ?? [];
        var keys = declaredKeys.Concat(FlameSchema.ReservedBlackboardKeys).ToHashSet(StringComparer.Ordinal);
        var blackboard = (current?.Blackboard ?? []).Where(pair => keys.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal);
        var next = state.Clone();
        next.Presentation = new(state.ProgramRevision, (current?.Revision ?? 0) + 1, spec, null, blackboard);
        return next;
    }

    public AppState? Fail(string error)
    {
        var current = state.Presentation;
        if (current is { Root: null } && current.Error == error)
        {
            return null;
        }

        var next = state.Clone();
        next.Presentation = new(state.ProgramRevision, (current?.Revision ?? 0) + 1, null, error, new(current?.Blackboard ?? [], StringComparer.Ordinal));
        return next;
    }

    public AppState? WithBlackboard(IReadOnlyDictionary<string, string> values)
    {
        var current = state.Presentation ?? new(state.ProgramRevision, 0, null, null, new(StringComparer.Ordinal));
        if (values.All(pair => current.Blackboard.TryGetValue(pair.Key, out var held) && held == pair.Value))
        {
            return null;
        }

        var blackboard = new Dictionary<string, string>(current.Blackboard, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            blackboard[key] = value;
        }

        var next = state.Clone();
        next.Presentation = current with { Revision = current.Revision + 1, Blackboard = blackboard };
        return next;
    }

    public static Signals.PresentationChanged? ApplyActivity(AppState next, string key, string state, Dictionary<string, string>? extra = null)
    {
        var running = state is "working" or "reading" or "searching" or "listening" ? "true" : "false";
        var attention = state == "attention" ? "true" : "false";
        var current = next.Presentation ?? new(next.ProgramRevision, 0, null, null, new(StringComparer.Ordinal));
        var declared = current.Root?.Sphere?.BlackboardKeys;
        if (extra is { Count: > 0 } && declared is null)
        {
            // The sphere is published by another behavior. Until it exists, the activity still commits
            // and keys that have nowhere to go wait for a later write.
            extra = null;
        }

        var board = new Dictionary<string, string>(current.Blackboard, StringComparer.Ordinal)
        {
            ["running"] = running,
            ["attention"] = attention,
        };
        if (extra is not null)
        {
            foreach (var (k, v) in extra)
            {
                board[k] = v;
            }
        }

        if (board.Count == current.Blackboard.Count && board.All(pair => current.Blackboard.TryGetValue(pair.Key, out var held) && held == pair.Value))
        {
            return null;
        }

        next.Presentation = current with { Revision = checked(current.Revision + 1), Blackboard = board };
        return new(key, next.ProgramRevision, next.Presentation.Revision);
    }

    internal static UiNode MapView(View view, string? brainId, bool osTrusted)
        => ViewMapper.MapView(view, brainId, osTrusted);

    internal static UiNode MapNode(string key, Node node, string? brainId, bool osTrusted)
        => ViewMapper.MapNode(key, node, brainId, osTrusted);

    internal static UiNode MapView(View view, string? brainId = null)
        => ViewMapper.MapView(view, brainId);

    internal static UiNode MapNode(string key, Node node, string? brainId = null)
        => ViewMapper.MapNode(key, node, brainId);
}
