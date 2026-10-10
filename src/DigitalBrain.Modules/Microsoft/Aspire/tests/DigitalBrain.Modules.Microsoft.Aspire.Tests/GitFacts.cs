using DigitalBrain.Microsoft.Aspire.Candidates;

namespace DigitalBrain.Microsoft.Aspire.Tests;

public sealed class GitFacts : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("aspire-git-").FullName;
    private readonly SystemProcessRunner processes = new();

    private async Task<string> GitAsync(string directory, params string[] args)
    {
        var result = await processes.RunAsync("git", ["-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", .. args], directory, null, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.Error);
        return result.Output.Trim();
    }

    // Promotion fast-forwards the branch a person has checked out: the files must follow the ref, or the next
    // commit there would revert the promoted change.
    [Fact]
    public async Task FastForwardingTheCheckedOutBranchBringsItsWorkingTreeAlong()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Path.Combine(root, "repo");
        Directory.CreateDirectory(repository);
        await GitAsync(repository, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repository, "a.txt"), "a", ct);
        await GitAsync(repository, "add", "-A");
        await GitAsync(repository, "commit", "-m", "base");
        var candidate = Path.Combine(root, "candidate");
        await GitAsync(repository, "worktree", "add", "--detach", candidate, "HEAD");
        await File.WriteAllTextAsync(Path.Combine(candidate, "b.txt"), "b", ct);
        await GitAsync(candidate, "add", "-A");
        await GitAsync(candidate, "commit", "-m", "candidate");
        var sha = await GitAsync(candidate, "rev-parse", "HEAD");

        await new Git(processes, repository).FastForward("main", sha, ct);

        Assert.Equal(sha, await GitAsync(repository, "rev-parse", "HEAD"));
        Assert.True(File.Exists(Path.Combine(repository, "b.txt")));
        Assert.Equal("", await GitAsync(repository, "status", "--porcelain"));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
        Directory.Delete(root, recursive: true);
    }
}
