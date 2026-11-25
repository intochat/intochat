namespace IntoChat.OS.Modules.Apps.Presentations;

// An app's presentation blackboard: the running and attention flags its activity sets.
internal static class AppPresentation
{
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
