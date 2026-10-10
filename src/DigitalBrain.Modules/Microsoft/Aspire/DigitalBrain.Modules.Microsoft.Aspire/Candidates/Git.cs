namespace DigitalBrain.Microsoft.Aspire.Candidates;

internal sealed class Git(IProcessRunner processes, string repository)
{
    public async Task<string> Run(string workingDirectory, CancellationToken ct, params string[] args)
    {
        // Worktrees nest the repository under .digitalbrain; Windows paths overflow without long-path support.
        var result = await processes.RunAsync("git", ["-c", "core.longpaths=true", .. args], workingDirectory, null, ct);
        if (result.ExitCode != 0) { throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}"); }
        return result.Output.Trim();
    }

    public Task AddWorktree(string path, string baseSha, CancellationToken ct) => Run(repository, ct, "worktree", "add", "--detach", path, baseSha);
    public Task MoveWorktree(string from, string to, CancellationToken ct) => Run(repository, ct, "worktree", "move", from, to);
    public async Task RemoveWorktree(string path, CancellationToken ct)
    {
        try { await Run(repository, ct, "worktree", "remove", "--force", path); }
        catch (InvalidOperationException) { }
    }
    public Task Stage(string worktree, CancellationToken ct) => Run(worktree, ct, "add", "-A");
    public Task Commit(string worktree, string message, CancellationToken ct) => Run(worktree, ct, "commit", "-m", message, "--allow-empty");
    public Task<string> ShortSha(string worktree, CancellationToken ct) => Run(worktree, ct, "rev-parse", "--short=7", "HEAD");
    public async Task<string[]> ChangedFiles(string worktree, string baseSha, CancellationToken ct)
        => (await Run(worktree, ct, "diff", "--name-only", baseSha, "HEAD")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    // Fast-forward only. The branch checked out in the repository moves with its working tree and index (a bare
    // ref move left them on the old commit, so the next commit there reverted the promotion); any other branch
    // moves by fetch, which refuses what is not a fast-forward.
    public async Task FastForward(string branch, string sha, CancellationToken ct)
    {
        var current = await Run(repository, ct, "rev-parse", "--abbrev-ref", "HEAD");
        if (current == branch) { await Run(repository, ct, "merge", "--ff-only", sha); }
        else { await Run(repository, ct, "fetch", ".", $"{sha}:refs/heads/{branch}"); }
    }
}
