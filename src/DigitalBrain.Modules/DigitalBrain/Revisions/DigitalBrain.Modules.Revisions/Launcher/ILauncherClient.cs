namespace DigitalBrain.Revisions;

public sealed record LauncherRevision(string Sha, string Worktree, string McpEndpoint, string UiEndpoint, string State);

// The launcher is the only process that touches worktrees, ports and aspire run; the brain asks.
public interface ILauncherClient
{
    Task<LauncherRevision> Propose(string branch, string summary, IReadOnlyDictionary<string, string> files, CancellationToken ct);
    Task<LauncherRevision> Start(string sha, CancellationToken ct);
    Task<LauncherRevision> WaitHealthy(string sha, TimeSpan budget, CancellationToken ct);
    Task<LauncherRevision> Promote(string sha, CancellationToken ct);
    Task Discard(string sha, CancellationToken ct);
}