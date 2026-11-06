namespace IntoChat.OS.Modules.Microsoft.CSharp.Contracts.Runtime;

/// <summary>
/// What the brain posts to the sandbox host to open (or reopen) its session: the host dials EdgeUrl back.
/// </summary>
public sealed record SessionStart(
    string Brain,
    string Session,
    string Token,
    string EdgeUrl);

/// <summary>
/// Sent from the runtime to the sandbox to request tail logs for a behavior.
/// </summary>
public sealed record LogsRequestEvent(
    string Behavior,
    int Tail,
    string Request)
{
    public const string Kind = "logs";
}
