namespace IntoChat.OS.Client.Runtime;

// The sandbox host sets this around a behavior's entry point, so IntoChatOSClient.ConnectAsync inside
// the behavior resolves to its session, its id, its settings and its stop signal.
public static class AmbientSession
{
    private static readonly AsyncLocal<Scope?> Current = new();

    internal static Scope? Value => Current.Value;

    public static IDisposable Enter(SessionConnection session, string behavior, IReadOnlyDictionary<string, string> settings, CancellationToken stopping, string? assignmentHash = null, string? assignmentRevision = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(behavior);
        ArgumentNullException.ThrowIfNull(settings);
        var previous = Current.Value;
        Current.Value = new(session, behavior, new Dictionary<string, string>(settings, StringComparer.Ordinal), stopping, new BehaviorTrace(), assignmentHash, assignmentRevision);
        return new Exit(previous);
    }

    internal sealed record Scope(SessionConnection Session, string Behavior, Dictionary<string, string> Settings, CancellationToken Stopping, BehaviorTrace Trace, string? AssignmentHash, string? AssignmentRevision);

    private sealed class Exit(Scope? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
