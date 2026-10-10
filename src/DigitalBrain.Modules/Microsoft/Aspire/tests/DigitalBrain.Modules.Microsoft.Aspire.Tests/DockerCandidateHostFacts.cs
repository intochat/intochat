using DigitalBrain.Microsoft.Aspire.Candidates;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Microsoft.Aspire.Tests;

public sealed class DockerCandidateHostFacts : IDisposable
{
    private readonly string workdir = Path.Combine(Path.GetTempPath(), "candidate-host-" + Guid.NewGuid().ToString("N"), ".digitalbrain");
    private readonly RecordingProcessRunner processes = new();

    public DockerCandidateHostFacts() => Directory.CreateDirectory(workdir);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(workdir)!, recursive: true);

    [Fact]
    public async Task NoLiveIntegrationValueReachesTheCandidateButTheModels()
    {
        var live = new Dictionary<string, string?>
        {
            ["DigitalBrain:Integrations:openrouter:ApiKey"] = "model-key",
            ["DigitalBrain:Integrations:typesafe:Endpoint"] = "https://jev",
            ["DigitalBrain:Integrations:github:PrivateKeyPem"] = "pem",
            ["DigitalBrain:Integrations:postgres:ConnectionString"] = "Host=live-postgres",
            ["DigitalBrain:Integrations:postgres:AdminConnection"] = "Host=live-postgres-admin",
            ["DigitalBrain:Integrations:clickhouse:ConnectionString"] = "Host=live-clickhouse",
            ["DigitalBrain:Integrations:aspire:Workdir"] = "E:/repo/.digitalbrain",
            ["DigitalBrain:Integrations:csharp:RunTokenKey"] = "live-run-token",
            ["DigitalBrain:Modules:ai:Options:DefaultModel"] = "deepseek",
            ["DigitalBrain:MasterKey"] = "live-master",
            ["DigitalBrain:Auth:Posture"] = "Owner",
            ["ConnectionStrings:grainstate"] = "live-grainstate",
            ["ConnectionStrings:clustering"] = "live-clustering",
        };
        var host = Host(live);

        await host.Run("abc1234", TestContext.Current.CancellationToken);

        string[] model = ["model-key", "https://jev"];
        var forbidden = live.Where(pair => pair.Key.StartsWith("DigitalBrain:Integrations:", StringComparison.Ordinal) || pair.Key.StartsWith("ConnectionStrings:", StringComparison.Ordinal) || pair.Key == "DigitalBrain:MasterKey")
            .Select(pair => pair.Value!).Except(model).ToArray();
        foreach (var environment in new[] { host.LastRunEnvironment, host.LastMcpEnvironment })
        {
            Assert.All(environment, pair => Assert.DoesNotContain(forbidden, value => pair.Value.Contains(value, StringComparison.Ordinal)));
        }
        Assert.Equal("model-key", host.LastRunEnvironment["DigitalBrain__Integrations__openrouter__ApiKey"]);
        Assert.Equal("https://jev", host.LastRunEnvironment["DigitalBrain__Integrations__typesafe__Endpoint"]);
        Assert.DoesNotContain(host.LastMcpEnvironment.Keys, key => key.StartsWith("DigitalBrain__Integrations__", StringComparison.Ordinal));
        Assert.Equal(32, Convert.FromBase64String(host.LastRunEnvironment["DigitalBrain__MasterKey"]).Length);
        Assert.Equal(32, Convert.FromBase64String(host.LastRunEnvironment["DigitalBrain__Integrations__csharp__RunTokenKey"]).Length);
        Assert.Contains("candidate-abc1234-postgres", host.LastRunEnvironment["DigitalBrain__Integrations__postgres__ConnectionString"], StringComparison.Ordinal);
        Assert.Contains("candidate-abc1234-postgres", host.LastRunEnvironment["DigitalBrain__Integrations__postgres__AdminConnection"], StringComparison.Ordinal);
        Assert.Contains("candidate-abc1234-clickhouse", host.LastRunEnvironment["DigitalBrain__Integrations__clickhouse__ConnectionString"], StringComparison.Ordinal);
        Assert.Equal("Open", host.LastRunEnvironment["DigitalBrain__Auth__Posture"]);
        Assert.Equal(host.LastRunEnvironment["ConnectionStrings__clustering"], host.LastMcpEnvironment["ConnectionStrings__clustering"]);
        Assert.DoesNotContain(processes.Commands, command => command.Contains("model-key", StringComparison.Ordinal));
    }

    [Fact]
    public void TheStorageCopyLeavesEveryIntegrationBehind()
    {
        Assert.True(DockerCandidateHost.LeftBehind("registration-integration/github"));
        Assert.True(DockerCandidateHost.LeftBehind("integration.registration/integration/postgres.json"));
        Assert.True(DockerCandidateHost.LeftBehind("registration-integration/openrouter"));
        Assert.True(DockerCandidateHost.LeftBehind(".digitalbrain-deployment.json"));
        Assert.False(DockerCandidateHost.LeftBehind("app.state/alice/apps/notes"));
    }

    [Fact]
    public async Task RemoveDeletesTheContainerItsAzuriteAndItsNetwork()
    {
        processes.Answer("docker network rm", new(1, "", "Error response from daemon: network candidate-abc1234 not found"));

        await Host().Remove("abc1234", TestContext.Current.CancellationToken);

        Assert.Equal(["docker rm -f candidate-abc1234 candidate-abc1234-mcp candidate-abc1234-azurite candidate-abc1234-postgres candidate-abc1234-clickhouse", "docker network rm candidate-abc1234"], processes.Commands);
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
        var project = Path.Combine(workdir, "worktrees", "abc1234", "src", "Mod", "Mod.X");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "Mod.X.csproj"), "<Project />", TestContext.Current.CancellationToken);
        processes.Answer("git -c core.longpaths=true diff", new(0, "src/Mod/Mod.X/Thing.cs\n", ""));
        processes.Answer("dotnet build", new(1, string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i}")) + "\nThing.cs(3,1): error CS1002: ; expected", ""));

        var error = await Assert.ThrowsAsync<CandidateHostException>(() => Host().BuildGate("abc1234", TestContext.Current.CancellationToken));

        Assert.Equal("gate", error.Stage);
        Assert.Contains("error CS1002", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("line 1\n", error.Message, StringComparison.Ordinal);
    }

    private DockerCandidateHost Host(Dictionary<string, string?>? live = null)
        => new(processes, new ConfigurationBuilder().AddInMemoryCollection(live ?? []).Build(),
            _ => Task.FromResult(workdir), (_, _) => Task.CompletedTask, _ => false, new HttpClient(), (_, _, _) => Task.CompletedTask);

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
