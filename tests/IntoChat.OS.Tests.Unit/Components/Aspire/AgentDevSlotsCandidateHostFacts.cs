using System.Collections.Concurrent;
using System.Net;
using IntoChat.Aspire;
using IntoChat.Aspire.Candidates;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Tests.Unit.Components.Aspire;

public sealed class AgentDevSlotsCandidateHostFacts : IDisposable
{
    private readonly string _workdir;

    public AgentDevSlotsCandidateHostFacts()
    {
        _workdir = Path.Combine(Path.GetTempPath(), "intochat-devslots-host-" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task Run_EnforcesUnprivilegedUid1000_ScrubbedEphemeralHome_AndSecretContainment()
    {
        var runner = new RecordingDockerRunner();
        using var http = new HttpClient(new StaticOkHandler());
        var aspireSection = DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection("aspire");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.MasterKeyConfigurationKey] = "host-live-master-secret",
                [PresenceProof.TokenHashConfigurationKey] = "host-presence-hash",
                ["INTOCHAT_PRESENCE_TOKEN"] = "host-presence-token",
                ["SSH_AUTH_SOCK"] = "/tmp/host-ssh-agent.sock",
                ["GITHUB_TOKEN"] = "ghp_host_leaked_token",
                ["AWS_SECRET_ACCESS_KEY"] = "aws-leaked-secret",
                [$"{aspireSection}:CommandTimeout"] = "00:02:00",
            })
            .Build();

        var modelKeyName = DockerCandidateHost.IntegrationVariable("openai", "ApiKey");
        var slotOptions = new CandidateSlotPoolOptions
        {
            MaxSlots = 2,
            BasePort = 5900,
            EnableWarmSlotReuse = true,
            CandidateUserId = 1000,
            CandidateGroupId = 1000,
        };
        await using var pool = new CandidateSlotLeasePool(slotOptions, isPortInUse: _ => false);
        await using var host = new AgentDevSlotsCandidateHost(
            runner,
            config,
            pool,
            _ => Task.FromResult(_workdir),
            _ => Task.FromResult(new CandidateSecrets(new Dictionary<string, string>
            {
                [modelKeyName] = "explicit-candidate-openai-key",
            })),
            http,
            slotOptions,
            dependenciesReady: (_, _) => Task.CompletedTask);

        var running = await host.Run("a1b2c3d", Ct);

        Assert.Equal("a1b2c3d", running.Sha);
        Assert.Equal("devslot-slot-00-candidate-a1b2c3d", running.Container);
        Assert.True(pool.TryGetActiveLease("a1b2c3d", out var lease));
        Assert.NotNull(lease);
        Assert.True(Directory.Exists(lease.EphemeralHome));

        // Verify unprivileged UID 1000 and hardening flags on candidate container run.
        var args = host.LastRunArguments;
        AssertFlagPair(args, "--user", "1000:1000");
        AssertFlagPair(args, "--security-opt", "no-new-privileges:true");
        AssertFlagPair(args, "--cap-drop", "ALL");
        AssertFlagPair(args, "--memory", AgentDevSlotsCandidateHost.CandidateMemoryLimit);
        AssertFlagPair(args, "--cpus", AgentDevSlotsCandidateHost.CandidateCpuLimit);
        AssertFlagPair(args, "-v", $"{lease.EphemeralHome}:{AgentDevSlotsCandidateHost.ContainerHomePath}");
        AssertFlagPair(args, "-p", $"{lease.Ports.HttpPort}:5080");

        // Dynamic port must never collide with live 5080/5081 or legacy 5190.
        Assert.NotEqual(5080, lease.Ports.HttpPort);
        Assert.NotEqual(5081, lease.Ports.HttpPort);
        Assert.NotEqual(5190, lease.Ports.HttpPort);
        Assert.Equal($"http://localhost:{lease.Ports.HttpPort}", running.Endpoints.Ui);

        // Secrets must never appear in CLI arguments (only `-e KEY`).
        Assert.DoesNotContain(args, arg => arg.Contains("explicit-candidate-openai-key", StringComparison.Ordinal));
        Assert.DoesNotContain(args, arg => arg.Contains("host-live-master-secret", StringComparison.Ordinal));

        // Verify scrubbed environment: explicit CandidateSecrets + Aspire connection strings only.
        var env = host.LastRunEnvironment;
        Assert.Equal(AgentDevSlotsCandidateHost.ContainerHomePath, env["HOME"]);
        Assert.Equal("/dev/null", env["GIT_CONFIG_GLOBAL"]);
        Assert.Equal("1", env["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal(AgentDevSlotsCandidateHost.ContainerIntoChatHomePath, env[DockerCandidateHost.Env(IntoChatOSNames.HomeKey)]);
        Assert.Equal("explicit-candidate-openai-key", env[modelKeyName]);

        // Fresh master key (never host master key) and zero ambient secrets.
        Assert.True(env.TryGetValue(IntoChatOSNames.MasterKeyEnvironmentVariable, out var candidateMasterKey));
        Assert.False(string.IsNullOrWhiteSpace(candidateMasterKey));
        Assert.NotEqual("host-live-master-secret", candidateMasterKey);
        Assert.False(env.ContainsKey("INTOCHAT_PRESENCE_TOKEN"));
        Assert.False(env.ContainsKey(PresenceProof.TokenHashEnvironmentVariable));
        Assert.False(env.ContainsKey("SSH_AUTH_SOCK"));
        Assert.False(env.ContainsKey("GITHUB_TOKEN"));
        Assert.False(env.ContainsKey("AWS_SECRET_ACCESS_KEY"));

        // Aspire service connection strings point to slot-isolated databases.
        Assert.Contains(lease.PostgresDatabaseName, env[DockerCandidateHost.IntegrationVariable("postgres", "ConnectionString")], StringComparison.Ordinal);
        Assert.Contains(lease.ClickHouseDatabaseName, env[DockerCandidateHost.IntegrationVariable("clickhouse", "ConnectionString")], StringComparison.Ordinal);
        Assert.Contains(AgentDevSlotsCandidateHost.AzuriteOf(lease.SlotId), env[DockerCandidateHost.IntegrationVariable("orleans", "GrainState")], StringComparison.Ordinal);

        await host.WaitHealthy("a1b2c3d", TimeSpan.FromSeconds(5), Ct);

        var homeBeforeRemove = lease.EphemeralHome;
        await host.Remove("a1b2c3d", Ct);
        Assert.False(Directory.Exists(homeBeforeRemove));
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public async Task WarmSlotReuse_SkipsSidecarBoot_ResetsLeaseDatabases_AndIsolatesNewLeaseState()
    {
        var runner = new RecordingDockerRunner();
        using var http = new HttpClient(new StaticOkHandler());
        var config = new ConfigurationBuilder().Build();
        var slotOptions = new CandidateSlotPoolOptions
        {
            MaxSlots = 2,
            BasePort = 6000,
            EnableWarmSlotReuse = true,
        };
        await using var pool = new CandidateSlotLeasePool(slotOptions, isPortInUse: _ => false);
        await using var host = new AgentDevSlotsCandidateHost(
            runner,
            config,
            pool,
            _ => Task.FromResult(_workdir),
            http: http,
            options: slotOptions,
            dependenciesReady: (_, _) => Task.CompletedTask);

        // Candidate 1 -> cold start.
        await host.Run("111aaaa", Ct);
        Assert.True(pool.TryGetActiveLease("111aaaa", out var lease1));
        Assert.NotNull(lease1);
        var home1 = lease1.EphemeralHome;
        var pgDb1 = lease1.PostgresDatabaseName;
        var chDb1 = lease1.ClickHouseDatabaseName;
        var grainContainer1 = lease1.GrainStateContainerName;

        Assert.Equal(1, host.ColdStarts);
        Assert.Equal(0, host.WarmReuseHits);

        var sidecarStartsAfterFirst = runner.Calls.Count(c =>
            c.File == "docker" && c.Args.Contains("run") && c.Args.Contains($"{AgentDevSlotsCandidateHost.DevSlotLabelKey}.role=sidecar"));
        Assert.Equal(3, sidecarStartsAfterFirst);

        // Remove candidate 1 -> drops lease 1 DBs, deletes ephemeral home, keeps warm sidecars.
        await host.Remove("111aaaa", Ct);
        Assert.False(Directory.Exists(home1));
        Assert.Contains(runner.Calls, c => c.File == "docker" && c.Args.Contains("exec") && c.Args.Any(a => a.Contains($"DROP DATABASE IF EXISTS \"{pgDb1}\"", StringComparison.Ordinal)));
        Assert.Contains(runner.Calls, c => c.File == "docker" && c.Args.Contains("exec") && c.Args.Any(a => a.Contains($"DROP DATABASE IF EXISTS {chDb1}", StringComparison.Ordinal)));
        Assert.DoesNotContain(runner.Calls, c => c.File == "docker" && c.Args.Contains("rm") && c.Args.Contains(AgentDevSlotsCandidateHost.PostgresOf(lease1.SlotId)));

        // Candidate 2 -> warm hit! Zero new sidecar containers, brand-new isolated DBs & home.
        await host.Run("222bbbb", Ct);
        Assert.True(pool.TryGetActiveLease("222bbbb", out var lease2));
        Assert.NotNull(lease2);

        Assert.Equal(1, host.ColdStarts);
        Assert.Equal(1, host.WarmReuseHits);
        Assert.Equal(lease1.SlotId, lease2.SlotId);
        Assert.NotEqual(home1, lease2.EphemeralHome);
        Assert.NotEqual(pgDb1, lease2.PostgresDatabaseName);
        Assert.NotEqual(chDb1, lease2.ClickHouseDatabaseName);
        Assert.NotEqual(grainContainer1, lease2.GrainStateContainerName);

        var sidecarStartsAfterSecond = runner.Calls.Count(c =>
            c.File == "docker" && c.Args.Contains("run") && c.Args.Contains($"{AgentDevSlotsCandidateHost.DevSlotLabelKey}.role=sidecar"));
        Assert.Equal(3, sidecarStartsAfterSecond);

        await host.Remove("222bbbb", Ct);
    }

    [Fact]
    public async Task WarmSlotReuse_RecyclesColdAutomatically_WhenWarmSidecarProbeFails()
    {
        var runner = new RecordingDockerRunner();
        using var http = new HttpClient(new StaticOkHandler());
        var config = new ConfigurationBuilder().Build();
        var slotOptions = new CandidateSlotPoolOptions
        {
            MaxSlots = 1,
            BasePort = 6100,
            EnableWarmSlotReuse = true,
        };
        await using var pool = new CandidateSlotLeasePool(slotOptions, isPortInUse: _ => false);
        await using var host = new AgentDevSlotsCandidateHost(
            runner,
            config,
            pool,
            _ => Task.FromResult(_workdir),
            http: http,
            options: slotOptions,
            dependenciesReady: (_, _) => Task.CompletedTask,
            warmHealthProbe: (_, _) => Task.FromResult(false));

        await host.Run("aaa1111", Ct);
        await host.Remove("aaa1111", Ct);
        Assert.Equal(1, pool.Snapshot().WarmCount);

        // Warm probe returns false -> host tears down stale sidecars and boots fresh cold stack.
        await host.Run("bbb2222", Ct);
        Assert.Equal(2, host.ColdStarts);
        Assert.Equal(0, host.WarmReuseHits);

        await host.Remove("bbb2222", Ct);
    }

    [Fact]
    public async Task PreWarmSlotsAsync_AndDrainWarmSlotsAsync_ManageWarmSidecarsCleanly()
    {
        var runner = new RecordingDockerRunner();
        using var http = new HttpClient(new StaticOkHandler());
        var config = new ConfigurationBuilder().Build();
        var slotOptions = new CandidateSlotPoolOptions
        {
            MaxSlots = 2,
            BasePort = 6200,
            EnableWarmSlotReuse = true,
        };
        await using var pool = new CandidateSlotLeasePool(slotOptions, isPortInUse: _ => false);
        await using var host = new AgentDevSlotsCandidateHost(
            runner,
            config,
            pool,
            _ => Task.FromResult(_workdir),
            http: http,
            options: slotOptions,
            dependenciesReady: (_, _) => Task.CompletedTask);

        var warmed = await host.PreWarmSlotsAsync(2, Ct);
        Assert.Equal(2, warmed);
        Assert.Equal(2, pool.Snapshot().WarmCount);
        Assert.Equal(0, pool.ActiveCount);

        await host.Run("ccc3333", Ct);
        Assert.Equal(1, host.WarmReuseHits);
        await host.Remove("ccc3333", Ct);

        await host.DrainWarmSlotsAsync(Ct);
        Assert.Equal(0, pool.Snapshot().WarmCount);
        Assert.Contains(runner.Calls, c => c.File == "docker" && c.Args.Contains("network") && c.Args.Contains("rm"));
    }

    [Fact]
    public async Task ColdMode_TearsDownSidecarsAndNetworkOnRemove()
    {
        var runner = new RecordingDockerRunner();
        using var http = new HttpClient(new StaticOkHandler());
        var config = new ConfigurationBuilder().Build();
        var slotOptions = new CandidateSlotPoolOptions
        {
            MaxSlots = 2,
            BasePort = 6300,
            EnableWarmSlotReuse = false,
        };
        await using var pool = new CandidateSlotLeasePool(slotOptions, isPortInUse: _ => false);
        await using var host = new AgentDevSlotsCandidateHost(
            runner,
            config,
            pool,
            _ => Task.FromResult(_workdir),
            http: http,
            options: slotOptions,
            dependenciesReady: (_, _) => Task.CompletedTask);

        await host.Run("ddd4444", Ct);
        Assert.True(pool.TryGetActiveLease("ddd4444", out var lease));
        Assert.NotNull(lease);

        await host.Remove("ddd4444", Ct);

        Assert.Equal(0, pool.Snapshot().WarmCount);
        Assert.Contains(runner.Calls, c => c.File == "docker" && c.Args.Contains("rm") && c.Args.Contains(AgentDevSlotsCandidateHost.PostgresOf(lease.SlotId)));
        Assert.Contains(runner.Calls, c => c.File == "docker" && c.Args.Contains("network") && c.Args.Contains("rm") && c.Args.Contains(AgentDevSlotsCandidateHost.NetworkOf(lease.SlotId)));
    }

    [Fact]
    public async Task RunFailure_RollsBackLeaseAndDeletesEphemeralHome()
    {
        var runner = new RecordingDockerRunner
        {
            FailCandidateRun = true,
        };
        using var http = new HttpClient(new StaticOkHandler());
        var config = new ConfigurationBuilder().Build();
        var slotOptions = new CandidateSlotPoolOptions
        {
            MaxSlots = 1,
            BasePort = 6400,
            EnableWarmSlotReuse = false,
        };
        await using var pool = new CandidateSlotLeasePool(slotOptions, isPortInUse: _ => false);
        await using var host = new AgentDevSlotsCandidateHost(
            runner,
            config,
            pool,
            _ => Task.FromResult(_workdir),
            http: http,
            options: slotOptions,
            dependenciesReady: (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<CandidateHostException>(() => host.Run("eee5555", Ct));
        Assert.Equal("run", ex.Stage);
        Assert.Equal(0, pool.ActiveCount);
        Assert.Equal(1, pool.AvailableCount);

        var slotHomesDir = Path.Combine(_workdir, slotOptions.EphemeralHomeRootName);
        if (Directory.Exists(slotHomesDir))
        {
            Assert.Empty(Directory.EnumerateDirectories(slotHomesDir));
        }
    }

    [Fact]
    public async Task Orphans_RemovesUnknownDevSlotCandidates_AndPreservesKnownAndSidecars()
    {
        var runner = new RecordingDockerRunner
        {
            ListedContainers = "devslot-slot-00-candidate-aaa1111\ndevslot-slot-01-candidate-bbb2222\ndevslot-slot-00-postgres\n",
        };
        using var http = new HttpClient(new StaticOkHandler());
        var config = new ConfigurationBuilder().Build();
        await using var pool = new CandidateSlotLeasePool(
            new CandidateSlotPoolOptions { MaxSlots = 2, BasePort = 6500 },
            isPortInUse: _ => false);
        await using var host = new AgentDevSlotsCandidateHost(
            runner,
            config,
            pool,
            _ => Task.FromResult(_workdir),
            http: http,
            dependenciesReady: (_, _) => Task.CompletedTask);

        var removed = await host.Orphans(["aaa1111"], Ct);

        Assert.Equal(["devslot-slot-01-candidate-bbb2222"], removed);
        Assert.DoesNotContain(runner.Calls, c => c.Args.Contains("devslot-slot-00-candidate-aaa1111") && c.Args.Contains("rm"));
        Assert.DoesNotContain(runner.Calls, c => c.Args.Contains("devslot-slot-00-postgres") && c.Args.Contains("rm"));
    }

    private static void AssertFlagPair(IReadOnlyList<string> args, string flag, string expectedValue)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == flag && args[i + 1] == expectedValue)
            {
                return;
            }
        }

        Assert.Fail($"Expected flag pair '{flag} {expectedValue}' in docker arguments: {string.Join(' ', args)}");
    }

    private sealed record RecordedCall(string File, string[] Args, IReadOnlyDictionary<string, string>? Environment);

    private sealed class RecordingDockerRunner : IProcessRunner
    {
        private readonly ConcurrentQueue<RecordedCall> _calls = new();

        public IReadOnlyList<RecordedCall> Calls => [.. _calls];
        public bool FailCandidateRun { get; set; }
        public string ListedContainers { get; set; } = "";

        public Task<ProcessResult> RunAsync(
            string file,
            string[] args,
            string workingDirectory,
            IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct)
        {
            _calls.Enqueue(new RecordedCall(file, args, environment));
            if (file == "docker" && FailCandidateRun && args.Contains("run") && args.Contains("--user"))
            {
                return Task.FromResult(new ProcessResult(125, "", "simulated container launch error"));
            }

            if (file == "docker" && args is ["inspect", "-f", "{{.State.Running}}", _])
            {
                return Task.FromResult(new ProcessResult(0, "true\n", ""));
            }

            if (file == "docker" && args is ["ps", "-a", ..])
            {
                return Task.FromResult(new ProcessResult(0, ListedContainers, ""));
            }

            return Task.FromResult(new ProcessResult(0, "", ""));
        }

        public int Detach(string file, string[] args, string workingDirectory) => 1;
    }

    private sealed class StaticOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
