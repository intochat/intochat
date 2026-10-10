namespace DigitalBrain.Microsoft.Aspire.Candidates;

internal sealed class Git(IProcessRunner processes, string repository)
{
    public async Task<string> Run(string workingDirectory, CancellationToken ct, params string[] args)
    {
        var result = await processes.RunAsync("git", args, workingDirectory, null, ct);
        if (result.ExitCode != 0) { throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}"); }
        return result.Output.Trim();
    }

    public Task AddWorktree(string path, string baseSha, CancellationToken ct) => Run(repository, ct, "worktree", "add", "--detach", path, baseSha);
    public Task RepairWorktrees(CancellationToken ct) => Run(repository, ct, "worktree", "repair");
    public Task Stage(string worktree, CancellationToken ct) => Run(worktree, ct, "add", "-A");
    public Task Commit(string worktree, string message, CancellationToken ct) => Run(worktree, ct, "commit", "-m", message, "--allow-empty");
    public Task<string> ShortSha(string worktree, CancellationToken ct) => Run(worktree, ct, "rev-parse", "--short=7", "HEAD");
    public async Task<string[]> ChangedFiles(string worktree, string baseSha, CancellationToken ct)
        => (await Run(worktree, ct, "diff", "--name-only", baseSha, "HEAD")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public Task FastForward(string branch, string sha, CancellationToken ct) => Run(repository, ct, "update-ref", "refs/heads/" + branch, sha);
}
