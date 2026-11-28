using System.Diagnostics;
using System.Text;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntoChat.OS.Modules.Microsoft.GitHub.Tests.Unit;

public sealed class GitCommandRunnerFacts : IDisposable
{
    private readonly string _tempDir;

    public GitCommandRunnerFacts()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "intochat-gitrunner-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Fact]
    public async Task PushWithEphemeralTokenAsync_InjectsHardenedConfigAndRedactsSecretsOnFailure()
    {
        const string token = "github_pat_ephemeral_runner_secret_12345";
        var basicAuth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{token}"));
        var sha = new string('c', 40);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".git"), "gitdir: ./.git-real\n", Ct);

        ProcessStartInfo? captured = null;
        var exitCode = 0;
        var stdErr = "";
        var runner = new GitCommandRunner(
            new WorkTreeOptions(),
            NullLogger.Instance,
            (psi, _) =>
            {
                captured = psi;
                return Task.FromResult((exitCode, "", stdErr));
            });
        var repo = new GitHubRepository("b/connection/github/main", 0, 0, 12345, "intochat", "os", "pat");

        await runner.PushWithEphemeralTokenAsync(repo, _tempDir, "feat/unified-runner", sha, force: true, token, Ct);

        Assert.NotNull(captured);
        Assert.Equal("git", captured.FileName);
        Assert.Contains("--force", captured.ArgumentList);
        Assert.Contains("--no-verify", captured.ArgumentList);
        Assert.Contains("core.fsmonitor=false", captured.ArgumentList);
        Assert.Contains("core.hooksPath=/dev/null", captured.ArgumentList);
        Assert.Contains("protocol.file.allow=never", captured.ArgumentList);
        Assert.DoesNotContain(captured.ArgumentList, arg => arg.Contains(token, StringComparison.Ordinal));
        Assert.DoesNotContain(captured.ArgumentList, arg => arg.Contains(basicAuth, StringComparison.Ordinal));
        Assert.Equal("1", captured.Environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("http.https://github.com/.extraheader", captured.Environment["GIT_CONFIG_KEY_0"]);
        Assert.Equal($"AUTHORIZATION: basic {basicAuth}", captured.Environment["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("/dev/null", captured.Environment["GIT_CONFIG_GLOBAL"]);
        Assert.Equal("1", captured.Environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal("0", captured.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("never", captured.Environment["GCM_INTERACTIVE"]);

        exitCode = 128;
        stdErr = $"fatal: authentication failed with {token} and {basicAuth}";
        var ex = await Assert.ThrowsAsync<GitHubUnavailableException>(() =>
            runner.PushWithEphemeralTokenAsync(repo, _tempDir, "feat/unified-runner", sha, force: false, token, Ct));
        Assert.DoesNotContain(token, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(basicAuth, ex.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushWithEphemeralTokenAsync_WrapsTimeoutAsGitHubUnavailableException()
    {
        var sha = new string('d', 40);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".git"), "gitdir: ./.git-real\n", Ct);

        var runner = new GitCommandRunner(
            new WorkTreeOptions { CommandTimeout = TimeSpan.FromSeconds(5) },
            NullLogger.Instance,
            (_, _) => Task.FromException<(int, string, string)>(new TimeoutException("timed out")));
        var repo = new GitHubRepository("b/connection/github/main", 0, 0, 12345, "intochat", "os", "pat");

        var ex = await Assert.ThrowsAsync<GitHubUnavailableException>(() =>
            runner.PushWithEphemeralTokenAsync(repo, _tempDir, "feat/timeout", sha, force: false, "ghs_secret", Ct));
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RedactToken_HandlesEmptyAndRedactsBothRawAndBase64Tokens()
    {
        Assert.Equal("", GitCommandRunner.RedactToken("", "tok", "b64"));
        Assert.Equal(
            "err [REDACTED] and [REDACTED]",
            GitCommandRunner.RedactToken("err ghs_secret and dG9rZW4=", "ghs_secret", "dG9rZW4="));
    }
}
