namespace IntoChat.OS.Apps.AgentApp;

/// <summary>Persisted screen state for a runtime pure-data agent app.</summary>
public sealed record AgentAppState(
    string Title,
    string Description,
    string[] Starters,
    string? PendingId = null,
    string? PendingText = null,
    string? ActiveRunId = null,
    string? Error = null,
    bool PendingReseat = false)
{
    public AgentAppState() : this(string.Empty, string.Empty, []) { }
}
