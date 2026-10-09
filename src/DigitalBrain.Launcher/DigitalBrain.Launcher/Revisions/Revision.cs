namespace DigitalBrain.Launcher.Revisions;

public sealed record Revision(string Sha, string Branch, string Worktree, int UiPort, int McpPort, string State)
{
    public string McpEndpoint => $"http://localhost:{McpPort}";
    public string UiEndpoint => $"http://localhost:{UiPort}";
    public string ServiceId => "intochat-r" + Sha;
    public string Container => "brain-r" + Sha;
}

public sealed record RevisionsDocument(string? Current, Revision[] Revisions)
{
    public (int UiPort, int McpPort) NextBlock(int firstBlockPort, int blockSize)
    {
        var highest = Revisions.Length == 0 ? firstBlockPort - blockSize : Revisions.Max(revision => revision.UiPort);
        return (highest + blockSize, highest + blockSize + 1);
    }

    public Revision Require(string sha) => Revisions.SingleOrDefault(revision => revision.Sha == sha)
        ?? throw new KeyNotFoundException($"Revision {sha} is not known to the launcher.");

    public RevisionsDocument With(Revision revision) => this with
    {
        Revisions = [.. Revisions.Where(known => known.Sha != revision.Sha), revision],
    };
}
