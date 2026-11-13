using System.Text.Json;

namespace IntoChat.OS.Modules.Apps.Presentations;

internal sealed class AppPresentation(AppState state)
{
    private static readonly string[] ReservedBlackboardKeys = ["running", "attention"];
    public AppState State => state;

    public AppState? Publish(IntoChat.OS.Modules.Apps.Presentation spec)
    {
        var current = state.Presentation;
        if (current is { Root: { } root, Error: null } && JsonSerializer.Serialize(root) == JsonSerializer.Serialize(spec))
        {
            return null;
        }

        var declaredKeys = spec.Sphere?.BlackboardKeys ?? [];
        var keys = declaredKeys.Concat(ReservedBlackboardKeys).ToHashSet(StringComparer.Ordinal);
        var blackboard = (current?.Blackboard ?? []).Where(pair => keys.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal);
        var next = state.Clone();
        next.Presentation = new(state.ProgramRevision, (current?.Revision ?? 0) + 1, spec, null, blackboard);
        return next;
    }

    public static Signals.PresentationChanged? ApplyActivity(AppState next, string key, string state)
    {
        var running = state is "working" or "reading" or "searching" or "listening" ? "true" : "false";
        var attention = state == "attention" ? "true" : "false";
        var current = next.Presentation ?? new(next.ProgramRevision, 0, null, null, new(StringComparer.Ordinal));
        var board = new Dictionary<string, string>(current.Blackboard, StringComparer.Ordinal)
        {
            ["running"] = running,
            ["attention"] = attention,
        };
        if (board.Count == current.Blackboard.Count && board.All(pair => current.Blackboard.TryGetValue(pair.Key, out var held) && held == pair.Value))
        {
            return null;
        }

        next.Presentation = current with { Revision = checked(current.Revision + 1), Blackboard = board };
        return new(key, next.ProgramRevision, next.Presentation.Revision);
    }
}
