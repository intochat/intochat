using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.Aspire.Candidates;
using DigitalBrain.Authority.Identity.Configuration;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Microsoft.Aspire.Tests;

public sealed class DockerCandidateHostFacts : IDisposable
{
    private readonly string workdir = Path.Combine(Path.GetTempPath(), "candidate-host-" + Guid.NewGuid().ToString("N"), ".digitalbrain");
    private readonly RecordingProcessRunner processes = new();

    public DockerCandidateHostFacts() => Directory.CreateDirectory(workdir);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(workdir)!, recursive: true);

    // The candidate host reads no Integration from configuration: the model's values and the live grain-state
    // connection arrive as the aspire Integration's secrets; every other Integration is the candidate's own.
    [Fact]
    public async Task NoLiveIntegrationValueReachesTheCandidateButTheModels()
    {
        var transcription = DigitalBrainConfiguration.ModuleOptionsSection("ai") + ":Default:Transcription";
        var live = new Dictionary<string, string?>
        {
            [IntegrationSchema.Key("openrouter", "ApiKey")] = "live-config-model-key",
            [IntegrationSchema.Key("github", "PrivateKeyPem")] = "pem",
            [IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=live-postgres",
            [IntegrationSchema.Key("postgres", "AdminConnection")] = "Host=live-postgres-admin",
            [IntegrationSchema.Key("supabase", "ConnectionString")] = "Host=live-supabase",
            [IntegrationSchema.Key("clickhouse", "ConnectionString")] = "Host=live-clickhouse",
            [IntegrationSchema.Key("aspire", "Workdir")] = "E:/repo/.digitalbrain",
            [IntegrationSchema.Key("csharp", "RunTokenKey")] = "live-run-token",
            [DigitalBrainConfiguration.ModuleOptionsSection("ai") + ":DefaultModel"] = "deepseek",
            [transcription] = "IWhisperLargeV3Turbo",
            [AuthOptions.PostureKey] = "Owner",
            ["ConnectionStrings:grainstate"] = "live-grainstate",
            ["ConnectionStrings:clustering"] = "live-clustering",
            [DockerCandidateHost.ExportCadence[0]] = "1000",
        };
        var model = new Dictionary<string, string>
        {
            [DockerCandidateHost.IntegrationVariable("openrouter", "ApiKey")] = "model-key",
            [DockerCandidateHost.IntegrationVariable("typesafe", "Endpoint")] = "https://jev",
        };
        var host = Host(live, new CandidateSecrets(model));

        await host.Run("abc1234", TestContext.Current.CancellationToken);

        Assert.Equal("/app/brain-store", host.LastRunEnvironment[DockerCandidateHost.Env(DigitalBrainNames.BrainStorageDirectoryConfigurationKey)]);
        var forbidden = live.Where(pair => pair.Key.StartsWith(IntegrationSchema.SectionName, StringComparison.Ordinal) || pair.Key.StartsWith("ConnectionStrings:", StringComparison.Ordinal))
            .Select(pair => pair.Value!).ToArray();
        foreach (var environment in new[] { host.LastRunEnvironment, host.LastMcpEnvironment })
        {
            Assert.All(environment, pair => Assert.DoesNotContain(forbidden, value => pair.Value.Contains(value, StringComparison.Ordinal)));
        }
        Assert.Equal("model-key", host.LastRunEnvironment[DockerCandidateHost.IntegrationVariable("openrouter", "ApiKey")]);
        Assert.Equal("https://jev", host.LastRunEnvironment[DockerCandidateHost.IntegrationVariable("typesafe", "Endpoint")]);
        Assert.DoesNotContain(host.LastMcpEnvironment.Values, value => value is "model-key" or "https://jev");
        Assert.Equal(32, Convert.FromBase64String(host.LastRunEnvironment[DigitalBrainNames.MasterKeyEnvironmentVariable]).Length);
        Assert.Equal(32, Convert.FromBase64String(host.LastRunEnvironment[DockerCandidateHost.IntegrationVariable("csharp", "RunTokenKey")]).Length);
        Assert.Contains("candidate-abc1234-postgres", host.LastRunEnvironment[DockerCandidateHost.IntegrationVariable("postgres", "ConnectionString")], StringComparison.Ordinal);
        Assert.Contains("candidate-abc1234-postgres", host.LastRunEnvironment[DockerCandidateHost.IntegrationVariable("postgres", "AdminConnection")], StringComparison.Ordinal);
        Assert.Contains("candidate-abc1234-postgres", host.LastRunEnvironment[DockerCandidateHost.IntegrationVariable("supabase", "ConnectionString")], StringComparison.Ordinal);
        Assert.DoesNotContain(DockerCandidateHost.Env(transcription), host.LastRunEnvironment.Keys);
        Assert.Contains("candidate-abc1234-clickhouse", host.LastRunEnvironment[DockerCandidateHost.IntegrationVariable("clickhouse", "ConnectionString")], StringComparison.Ordinal);
        Assert.Equal("Open", host.LastRunEnvironment[DockerCandidateHost.Env(AuthOptions.PostureKey)]);
        Assert.Equal("1000", host.LastRunEnvironment[DockerCandidateHost.ExportCadence[0]]);
        // The candidate silo stores through the orleans integration only; its MCP client finds it by the same tables.
        var siloConfiguration = EnvironmentProjection.Configuration(host.LastRunEnvironment);
        var mcpConfiguration = EnvironmentProjection.Configuration(host.LastMcpEnvironment);
        Assert.DoesNotContain(siloConfiguration.Keys, key => key.StartsWith("Orleans:", StringComparison.Ordinal)
            && key is not ("Orleans:ServiceId" or "Orleans:ClusterId" or "Orleans:EnableDistributedTracing"));
        Assert.DoesNotContain(siloConfiguration.Keys, key => key.StartsWith("ConnectionStrings:", StringComparison.Ordinal));
        foreach (var field in new[] { "Clustering", "GrainState", "Reminders" })
        { Assert.Contains("candidate-abc1234", host.LastRunEnvironment[DockerCandidateHost.IntegrationVariable("orleans", field)], StringComparison.Ordinal); }
        Assert.Equal(host.LastRunEnvironment[DockerCandidateHost.IntegrationVariable("orleans", "Clustering")],
            host.LastMcpEnvironment[DockerCandidateHost.ConnectionVariable(DigitalBrainNames.Clustering)]);
        Assert.Equal(siloConfiguration["Orleans:ClusterId"], mcpConfiguration["Orleans:ClusterId"]);
        Assert.DoesNotContain(processes.Commands, command => command.Contains("model-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACandidateStartsWithoutAccessToLiveStorage()
    {
        var host = Host(secrets: new CandidateSecrets(new Dictionary<string, string>()));

        await host.Run("abc1234", TestContext.Current.CancellationToken);
        Assert.Contains(processes.Commands, command => command.Contains("--name candidate-abc1234 ", StringComparison.Ordinal));
        Assert.DoesNotContain(processes.Commands, command => command.Contains("--volume", StringComparison.Ordinal));
    }

    // Cleanup after a failed run is best effort: its own failure must not hide why the run failed.
    [Fact]
    public async Task AFailingCleanupDoesNotMaskTheRunsError()
    {
        processes.Answer("docker network create", new(1, "", "network create failed: pool overlaps"));
        processes.Answer("docker rm", new(1, "", "daemon unreachable"));

        var error = await Assert.ThrowsAsync<CandidateHostException>(() => Host().Run("abc1234", TestContext.Current.CancellationToken));

        Assert.Contains("pool overlaps", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveDeletesTheContainerItsAzuriteAndItsNetwork()
    {
        processes.Answer("docker network rm", new(1, "", "Error response from daemon: network candidate-abc1234 not found"));

        await Host().Remove("abc1234", TestContext.Current.CancellationToken);

        Assert.Equal(["docker rm -f -v candidate-abc1234 candidate-abc1234-mcp candidate-abc1234-azurite candidate-abc1234-postgres candidate-abc1234-clickhouse", "docker network rm candidate-abc1234"], processes.Commands);
    }

    [Fact]
    public async Task OrphansRemovesOnlyUnknownCandidates()
    {
        processes.Answer("docker ps -a", new(0, "candidate-abc1234\ncandidate-abc1234-azurite\ncandidate-def5678\ncandidate-def5678-mcp\ncandidate-spike-azurite-1\n", ""));
        processes.Answer("docker network ls", new(0, "candidate-abc1234\ncandidate-def5678\ncandidate-0badc0de\ncandidate-spike_default\n", ""));

        var removed = await Host().Orphans(["abc1234"], TestContext.Current.CancellationToken);

        Assert.Equal(["candidate-def5678", "candidate-def5678-mcp", "candidate-0badc0de"], removed);
        Assert.Contains("docker network rm candidate-0badc0de", processes.Commands);
        Assert.DoesNotContain(processes.Commands, command => command.Contains("abc1234", StringComparison.Ordinal) || command.Contains("spike", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailingBuildRaisesAGateErrorWithTheOutputTail()
    {
        var project = Path.Combine(workdir, "worktrees", "abc1234", "lib", "Mod", "Mod.X");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "Mod.X.csproj"), "<Project />", TestContext.Current.CancellationToken);
        processes.Answer("git -c core.longpaths=true diff", new(0, "lib/Mod/Mod.X/Thing.cs\n", ""));
        processes.Answer("dotnet build", new(1, string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i}")) + "\nThing.cs(3,1): error CS1002: ; expected", ""));

        var error = await Assert.ThrowsAsync<CandidateHostException>(() => Host().BuildGate("abc1234", TestContext.Current.CancellationToken));

        Assert.Equal("gate", error.Stage);
        Assert.Contains("error CS1002", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("line 1\n", error.Message, StringComparison.Ordinal);
    }

    private DockerCandidateHost Host(Dictionary<string, string?>? live = null, CandidateSecrets? secrets = null)
        => new(processes, new ConfigurationBuilder().AddInMemoryCollection(live ?? []).Build(),
            _ => Task.FromResult(workdir), _ => Task.FromResult(secrets ?? new CandidateSecrets(new Dictionary<string, string>())),
            _ => false, new HttpClient(), (_, _, _) => Task.CompletedTask);

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        private readonly List<(string Prefix, ProcessResult Result)> answers = [];

        public List<string> Commands { get; } = [];

        public void Answer(string prefix, ProcessResult result) => answers.Add((prefix, result));

        public Task<ProcessResult> RunAsync(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
        {
            var command = $"{file} {string.Join(' ', args)}";
            Commands.Add(command);
            return Task.FromResult(answers.FirstOrDefault(answer => command.StartsWith(answer.Prefix, StringComparison.Ordinal)).Result ?? new(0, "", ""));
        }

        public int Detach(string file, string[] args, string workingDirectory)
            => throw new NotSupportedException();
    }
}
