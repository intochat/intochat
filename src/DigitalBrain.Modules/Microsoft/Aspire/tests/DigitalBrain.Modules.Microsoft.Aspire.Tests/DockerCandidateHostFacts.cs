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
    public async Task TheCandidateContainerGetsNoIntegrationButTheModel()
    {
        var host = Host(new Dictionary<string, string?>
        {
            ["DigitalBrain:Integrations:openrouter:ApiKey"] = "model-key",
            ["DigitalBrain:Integrations:typesafe:Endpoint"] = "https://jev",
            ["DigitalBrain:Integrations:github:PrivateKeyPem"] = "pem",
            ["DigitalBrain:Integrations:postgres:ConnectionString"] = "Host=live",
            ["DigitalBrain:Integrations:clickhouse:ConnectionString"] = "live",
            ["DigitalBrain:Integrations:aspire:Workdir"] = "E:/repo/.digitalbrain",
            ["DigitalBrain:Integrations:csharp:RunTokenKey"] = "token",
            ["DigitalBrain:Modules:ai:Options:DefaultModel"] = "deepseek",
            ["DigitalBrain:MasterKey"] = "master",
            ["ConnectionStrings:grainstate"] = "UseDevelopmentStorage=true",
        });

        await host.Run("abc1234", TestContext.Current.CancellationToken);

        var integrations = host.LastRunEnvironment.Keys.Where(key => key.StartsWith("DigitalBrain__Integrations__", StringComparison.Ordinal)).Order().ToArray();
        Assert.Equal(["DigitalBrain__Integrations__openrouter__ApiKey", "DigitalBrain__Integrations__typesafe__Endpoint"], integrations);
        Assert.Equal("intochat-cabc1234", host.LastRunEnvironment["Orleans__ServiceId"]);
        Assert.Contains("candidate-abc1234-azurite:10000", host.LastRunEnvironment["ConnectionStrings__grainstate"], StringComparison.Ordinal);
        var run = processes.Commands.Single(command => command.StartsWith("docker run -d --name candidate-abc1234 ", StringComparison.Ordinal));
        Assert.DoesNotContain("model-key", run, StringComparison.Ordinal);
        Assert.True(host.LastRunEnvironment.TryGetValue("DigitalBrain__MasterKey", out var masterKey));
        Assert.NotEqual("master", masterKey);
        Assert.All(host.LastRunEnvironment.Where(pair => pair.Key.StartsWith("ConnectionStrings__", StringComparison.Ordinal)),
            pair => Assert.Contains("candidate-abc1234-azurite", pair.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void TheStorageCopyLeavesEveryIntegrationBehind()
    {
        Assert.True(DockerCandidateHost.CarriesIntegration("registration-integration/github"));
        Assert.True(DockerCandidateHost.CarriesIntegration("integration.registration/integration/postgres.json"));
        Assert.True(DockerCandidateHost.CarriesIntegration("registration-integration/openrouter"));
        Assert.False(DockerCandidateHost.CarriesIntegration("app.state/alice/apps/notes"));
    }

    [Fact]
    public async Task RemoveDeletesTheContainerItsAzuriteAndItsNetwork()
    {
        processes.Answer("docker network rm", new(1, "", "Error response from daemon: network candidate-abc1234 not found"));

        await Host().Remove("abc1234", TestContext.Current.CancellationToken);

        Assert.Equal(["docker rm -f candidate-abc1234 candidate-abc1234-azurite", "docker network rm candidate-abc1234"], processes.Commands);
    }

    [Fact]
    public async Task OrphansRemovesOnlyUnknownCandidates()
    {
        processes.Answer("docker ps -a", new(0, "candidate-abc1234\ncandidate-abc1234-azurite\ncandidate-def5678\ncandidate-def5678-azurite\ncandidate-spike-azurite-1\n", ""));

        var removed = await Host().Orphans(["abc1234"], TestContext.Current.CancellationToken);

        Assert.Equal(["candidate-def5678", "candidate-def5678-azurite"], removed);
        Assert.Contains("docker rm -f candidate-def5678 candidate-def5678-azurite", processes.Commands);
        Assert.DoesNotContain(processes.Commands, command => command.Contains("abc1234", StringComparison.Ordinal) || command.Contains("spike", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailingBuildRaisesAGateErrorWithTheOutputTail()
    {
        var project = Path.Combine(workdir, "worktrees", "abc1234", "src", "Mod", "Mod.X");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "Mod.X.csproj"), "<Project />", TestContext.Current.CancellationToken);
        processes.Answer("git diff", new(0, "src/Mod/Mod.X/Thing.cs\n", ""));
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

        public IRunningProcess Start(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string> environment)
            => throw new NotSupportedException();
    }
}
