using System.Collections.Concurrent;
using DigitalBrain.Modules.Configuration;
using IntoChat.Aspire;
using IntoChat.Aspire.Candidates;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Tests.Unit.Components.Aspire;

public sealed class CandidateGitWorkspaceFacts : IDisposable
{
    private readonly string _workdir;

    public CandidateGitWorkspaceFacts()
    {
        _workdir = Path.Combine(Path.GetTempPath(), "intochat-git-ws-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workdir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_workdir))
        {
            try
            {
                Directory.Delete(_workdir, recursive: true);
            }
            catch
            {
                // Best-effort temp test directory cleanup.
            }
        }
    }

    [Fact]
    public async Task Prepare_StagesFilesCommitsAndMovesWorktree()
    {
        var runner = new RecordingGitRunner(shortSha: "abc1234");
        var workspace = new CandidateGitWorkspace(runner, _ => Task.FromResult(_workdir));

        var prepared = await workspace.Prepare(
            "deadbeef",
            "candidate fix",
            new Dictionary<string, string> { ["files/Sample.cs"] = "// content" },
            Ct);

        Assert.Equal("abc1234", prepared.Sha);
        Assert.Equal(Path.Combine(_workdir, "worktrees", "abc1234"), prepared.Worktree);
        Assert.Contains(runner.Calls, c => c.Args.Contains("worktree") && c.Args.Contains("add"));
        Assert.Contains(runner.Calls, c => c.Args.Contains("commit") && c.Args.Contains("candidate fix"));
        Assert.Contains(runner.Calls, c => c.Args.Contains("worktree") && c.Args.Contains("move") && c.Args.Contains(prepared.Worktree));
    }

    [Fact]
    public async Task Prepare_RejectsPathTraversalAndRemovesStagingWorktree()
    {
        var runner = new RecordingGitRunner(shortSha: "abc1234");
        var workspace = new CandidateGitWorkspace(runner, _ => Task.FromResult(_workdir));

        var ex = await Assert.ThrowsAsync<CandidateHostException>(() =>
            workspace.Prepare(
                "deadbeef",
                "escape attempt",
                new Dictionary<string, string> { ["../escape.txt"] = "nope" },
                Ct));

        Assert.Equal("prepare", ex.Stage);
        Assert.Contains("leaves the worktree", ex.Message, StringComparison.Ordinal);
        Assert.Contains(runner.Calls, c => c.Args.Contains("worktree") && c.Args.Contains("remove"));
    }

    [Fact]
    public async Task CommitAndUncommit_AdvanceAndRetreatLiveRef()
    {
        var runner = new RecordingGitRunner(shortSha: "abc1234")
        {
            ResolvedTargetSha = "abc1234567890abcdef1234567890abcdef12345",
            ResolvedCurrentRef = "01234567890abcdef1234567890abcdef1234567",
        };
        var workspace = new CandidateGitWorkspace(runner, _ => Task.FromResult(_workdir));

        var previous = await workspace.Commit("abc1234", Ct);
        Assert.Equal("01234567890abcdef1234567890abcdef1234567", previous);
        Assert.Contains(runner.Calls, c => c.Args.Contains("update-ref") && c.Args.Contains(CandidateGitWorkspace.LiveRef) && c.Args.Contains(runner.ResolvedTargetSha));

        await workspace.Uncommit("abc1234", previous, Ct);
        Assert.Contains(runner.Calls, c => c.Args.Contains("update-ref") && c.Args.Contains(CandidateGitWorkspace.LiveRef) && c.Args.Contains(previous!));
    }

    [Fact]
    public void CopySafeHostConfiguration_CopiesSafeSectionsRewritesOtlpAndScrubsSecretsAndCustodyKeys()
    {
        var aspireSection = ModuleConfiguration.ModuleOptionsSection("aspire");
        var cadenceKey = CandidateGitWorkspace.ExportCadence[0];
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{aspireSection}:CommandTimeout"] = "00:02:00",
                [CandidateGitWorkspace.AspNetCoreEnvironmentKey] = "Staging",
                [CandidateEnvironment.OtlpHttpEndpoint] = "http://localhost:18889",
                [cadenceKey] = "500",
                [IntoChatOSNames.MasterKeyConfigurationKey] = "host-master-secret",
                [PresenceProof.TokenHashConfigurationKey] = "host-presence-hash",
                ["INTOCHAT_PRESENCE_TOKEN"] = "host-presence-token",
                ["SSH_AUTH_SOCK"] = "/tmp/ssh-agent.sock",
                ["GITHUB_TOKEN"] = "ghp_secret_value",
                ["AWS_SECRET_ACCESS_KEY"] = "aws-secret",
                ["AZURE_CLIENT_SECRET"] = "azure-secret",
                ["NUGET_API_KEY"] = "nuget-secret",
                ["DOCKER_AUTH_CONFIG"] = "docker-secret",
            })
            .Build();

        var containerEnv = CandidateGitWorkspace.CopySafeHostConfiguration(config, rewriteLoopbackForContainer: true);
        Assert.Equal("00:02:00", containerEnv[CandidateGitWorkspace.Env($"{aspireSection}:CommandTimeout")]);
        Assert.Equal("Staging", containerEnv[CandidateGitWorkspace.AspNetCoreEnvironmentKey]);
        Assert.Equal("Open", containerEnv[CandidateGitWorkspace.Env(AuthKeys.Posture)]);
        Assert.Equal("true", containerEnv[CandidateGitWorkspace.Env(AuthKeys.AllowOpenOnNetwork)]);
        Assert.Equal("http://host.docker.internal:18889", containerEnv[CandidateGitWorkspace.OtlpExporterEndpointKey]);
        Assert.Equal("http/protobuf", containerEnv[CandidateGitWorkspace.OtlpExporterProtocolKey]);
        Assert.Equal("500", containerEnv[cadenceKey]);

        // Every host custody key and ambient secret must be excluded.
        Assert.False(containerEnv.ContainsKey(IntoChatOSNames.MasterKeyEnvironmentVariable));
        Assert.False(containerEnv.ContainsKey(PresenceProof.TokenHashEnvironmentVariable));
        Assert.False(containerEnv.ContainsKey("INTOCHAT_PRESENCE_TOKEN"));
        Assert.False(containerEnv.ContainsKey("SSH_AUTH_SOCK"));
        Assert.False(containerEnv.ContainsKey("GITHUB_TOKEN"));
        Assert.False(containerEnv.ContainsKey("AWS_SECRET_ACCESS_KEY"));
        Assert.False(containerEnv.ContainsKey("AZURE_CLIENT_SECRET"));
        Assert.False(containerEnv.ContainsKey("NUGET_API_KEY"));
        Assert.False(containerEnv.ContainsKey("DOCKER_AUTH_CONFIG"));

        // Local process mode preserves loopback OTLP endpoint and does not enable AllowOpenOnNetwork.
        var processEnv = CandidateGitWorkspace.CopySafeHostConfiguration(config, rewriteLoopbackForContainer: false);
        Assert.Equal("http://localhost:18889", processEnv[CandidateGitWorkspace.OtlpExporterEndpointKey]);
        Assert.False(processEnv.ContainsKey(CandidateGitWorkspace.Env(AuthKeys.AllowOpenOnNetwork)));
    }

    private sealed record RecordedCall(string File, string[] Args);

    private sealed class RecordingGitRunner(string shortSha) : IProcessRunner
    {
        private readonly ConcurrentQueue<RecordedCall> _calls = new();

        public IReadOnlyList<RecordedCall> Calls => [.. _calls];
        public string ResolvedTargetSha { get; set; } = "abc1234567890abcdef1234567890abcdef12345";
        public string? ResolvedCurrentRef { get; set; }

        public Task<ProcessResult> RunAsync(
            string file,
            string[] args,
            string workingDirectory,
            IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct)
        {
            _calls.Enqueue(new RecordedCall(file, args));
            if (file == "git" && args.Contains("worktree") && args.Contains("add"))
            {
                var pathIndex = Array.IndexOf(args, "--detach") + 1;
                if (pathIndex > 0 && pathIndex < args.Length)
                {
                    Directory.CreateDirectory(args[pathIndex]);
                }

                return Task.FromResult(new ProcessResult(0, "", ""));
            }

            if (file == "git" && args.Contains("--short=7"))
            {
                return Task.FromResult(new ProcessResult(0, shortSha + "\n", ""));
            }

            if (file == "git" && args.Contains("rev-parse") && args.Contains("--quiet"))
            {
                return ResolvedCurrentRef is not null
                    ? Task.FromResult(new ProcessResult(0, ResolvedCurrentRef + "\n", ""))
                    : Task.FromResult(new ProcessResult(1, "", ""));
            }

            if (file == "git" && args.Contains("rev-parse") && args.Contains("--verify"))
            {
                return Task.FromResult(new ProcessResult(0, ResolvedTargetSha + "\n", ""));
            }

            return Task.FromResult(new ProcessResult(0, "", ""));
        }

        public int Detach(string file, string[] args, string workingDirectory) => 1;
    }
}
