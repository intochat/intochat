using System.Xml.Linq;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Microsoft.Aspire.Candidates;
using IntoChat.OS.Connections.Identity.Configuration;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Microsoft.Aspire.Tests;

public sealed class DockerCandidateHostFacts : IDisposable
{
    private static readonly string RepositoryRoot = FindRepository();
    private readonly string workdir = Path.Combine(Path.GetTempPath(), "candidate-host-" + Guid.NewGuid().ToString("N"), ".intochat-os");
    private readonly RecordingProcessRunner processes = new();

    public DockerCandidateHostFacts() => Directory.CreateDirectory(workdir);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(workdir)!, recursive: true);

    [Fact]
    public async Task PublishingUsesEachComponentsInferredImageAndKeepsPortableOutput()
    {
        processes.Answer($"dotnet msbuild {ProjectPath("IntoChat.OS.Kernel")}", new(0, "mcr.microsoft.com/dotnet/aspnet:11.0-test-os\r\n", ""));
        processes.Answer($"dotnet msbuild {ProjectPath("IntoChat.OS.Mcp")}", new(0, "mcr.microsoft.com/dotnet/aspnet:11.0-test-mcp\n", ""));

        var build = await Host().Publish("abc1234", TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(workdir, "builds", "abc1234"), build);
        foreach (var (folder, assembly, image) in new[] { ("os", "Kernel", "intochat-os-candidate"), ("mcp", "Mcp", "intochat-os-candidate-mcp") })
        {
            var dockerfile = Path.Combine(workdir, $"Candidate.{folder}.Dockerfile");
            Assert.Equal($"FROM mcr.microsoft.com/dotnet/aspnet:11.0-test-{folder}\nWORKDIR /app\nCOPY . .\nENTRYPOINT [\"dotnet\", \"IntoChat.OS.{assembly}.dll\"]\n",
                await File.ReadAllTextAsync(dockerfile, TestContext.Current.CancellationToken));
            Assert.Contains($"dotnet publish {ProjectPath($"IntoChat.OS.{assembly}")} -c Release -o {Path.Combine(build, folder)}", processes.Commands);
            Assert.Contains($"docker build -t {image}:abc1234 -f {dockerfile} {Path.Combine(build, folder)}", processes.Commands);
        }
        Assert.All(processes.DotnetDirectories, directory => Assert.Equal(Path.Combine(workdir, "worktrees", "abc1234"), directory));
        Assert.All(processes.Commands.Where(command => command.StartsWith("dotnet msbuild", StringComparison.Ordinal)), command =>
        {
            Assert.Contains("-p:Configuration=Release", command, StringComparison.Ordinal);
            Assert.Contains("-p:RuntimeIdentifier=linux-x64", command, StringComparison.Ordinal);
            Assert.Contains("-p:EnableSdkContainerSupport=true", command, StringComparison.Ordinal);
            Assert.DoesNotContain("PublishProfile", command, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FailedImageInferencePreservesTheSdkErrorAndDoesNotBuildAnImage()
    {
        processes.Answer("dotnet msbuild", new(1, "", "error NETSDK1082: runtime pack unavailable"));

        var error = await Assert.ThrowsAsync<CandidateHostException>(() => Host().Publish("abc1234", TestContext.Current.CancellationToken));

        Assert.Equal("publish", error.Stage);
        Assert.Contains("NETSDK1082", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(processes.Commands, command => command.StartsWith("docker build", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n")]
    [InlineData("mcr.microsoft.com/dotnet/aspnet:11.0\nRUN echo unsafe")]
    [InlineData("mcr.microsoft.com/dotnet/aspnet:11.0 AS other")]
    [InlineData("--platform=linux/amd64")]
    [InlineData("image\\")]
    public async Task InvalidImageInferenceCannotBecomeADockerfileInstruction(string output)
    {
        processes.Answer("dotnet msbuild", new(0, output, ""));

        var error = await Assert.ThrowsAsync<CandidateHostException>(() => Host().Publish("abc1234", TestContext.Current.CancellationToken));

        Assert.Equal("publish", error.Stage);
        Assert.DoesNotContain(processes.Commands, command => command.StartsWith("docker build", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(workdir, "Candidate.os.Dockerfile")));
    }

    // A stale runtime image fails before either managed entry point can print its marker.
    [Fact(Timeout = 180_000)]
    public async Task PublishedComponentsStartWithTheCandidatesSdkRuntime()
    {
        var ct = TestContext.Current.CancellationToken;
        var sha = Guid.NewGuid().ToString("N");
        var worktree = Path.Combine(workdir, "worktrees", sha);
        Directory.CreateDirectory(worktree);
        File.Copy(Path.Combine(RepositoryRoot, "global.json"), Path.Combine(worktree, "global.json"));
        var components = new[] { (Folder: "os", Assembly: "IntoChat.OS.Kernel", Image: $"intochat-os-candidate:{sha}"),
            (Folder: "mcp", Assembly: "IntoChat.OS.Mcp", Image: $"intochat-os-candidate-mcp:{sha}") };
        var runner = new SystemProcessRunner();
        foreach (var component in components)
        {
            var project = Path.Combine(worktree, ProjectPath(component.Assembly));
            var directory = Path.GetDirectoryName(project)!;
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(project,
                $"<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net{System.Environment.Version.Major}.{System.Environment.Version.Minor}</TargetFramework><EnableSdkContainerSupport>false</EnableSdkContainerSupport></PropertyGroup></Project>", ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "Program.cs"), $"System.Console.WriteLine(\"candidate-{component.Folder}-started\");", ct);
            var existing = await runner.RunAsync("docker", ["image", "inspect", component.Image], worktree, null, ct);
            Assert.NotEqual(0, existing.ExitCode);
        }
        using var http = new HttpClient();
        var host = new DockerCandidateHost(runner, new ConfigurationBuilder().Build(), _ => Task.FromResult(workdir),
            _ => throw new InvalidOperationException("Publish needs no candidate secrets"), _ => false, http);
        TestContext.Current.TestOutputHelper?.WriteLine($"Owned fixture: {worktree}; images: {string.Join(", ", components.Select(component => component.Image))}");
        try
        {
            var build = await host.Publish(sha, ct);
            Assert.Equal(Path.Combine(workdir, "builds", sha), build);
            var results = new List<(string Folder, ProcessResult Result)>();
            foreach (var component in components)
            {
                Assert.True(File.Exists(Path.Combine(build, component.Folder, component.Assembly + ".dll")));
                var name = $"candidate-runtime-{sha}-{component.Folder}";
                var result = await runner.RunAsync("docker", ["run", "--rm", "--name", name, "--network", "none", "--read-only", component.Image], worktree, null, ct);
                TestContext.Current.TestOutputHelper?.WriteLine($"{component.Folder}: exit {result.ExitCode}\n{result.Output}\n{result.Error}");
                results.Add((component.Folder, result));
            }
            Assert.True(results.All(result => result.Result.ExitCode == 0), string.Join("\n", results.Select(result =>
                $"{result.Folder}: exit {result.Result.ExitCode}\n{result.Result.Output}\n{result.Result.Error}")));
            foreach (var (folder, result) in results) { Assert.Equal($"candidate-{folder}-started", result.Output.Trim()); }
        }
        finally
        {
            foreach (var component in components)
            {
                await runner.RunAsync("docker", ["rm", "-f", $"candidate-runtime-{sha}-{component.Folder}"], worktree, null, CancellationToken.None);
                var removed = await runner.RunAsync("docker", ["image", "rm", component.Image], worktree, null, CancellationToken.None);
                TestContext.Current.TestOutputHelper?.WriteLine($"Owned image cleanup {component.Image}: exit {removed.ExitCode}\n{removed.Output}\n{removed.Error}");
            }
        }
    }

    // The candidate host reads no Integration from configuration: the model's values and the live grain-state
    // connection arrive as the aspire Integration's secrets; every other Integration is the candidate's own.
    [Fact]
    public async Task NoLiveIntegrationValueReachesTheCandidateButTheModels()
    {
        var transcription = IntoChatOSConfiguration.ModuleOptionsSection("ai") + ":Default:Transcription";
        var live = new Dictionary<string, string?>
        {
            [IntegrationSchema.Key("openrouter", "ApiKey")] = "live-config-model-key",
            [IntegrationSchema.Key("github", "PrivateKeyPem")] = "pem",
            [IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=live-postgres",
            [IntegrationSchema.Key("postgres", "AdminConnection")] = "Host=live-postgres-admin",
            [IntegrationSchema.Key("supabase", "ConnectionString")] = "Host=live-supabase",
            [IntegrationSchema.Key("clickhouse", "ConnectionString")] = "Host=live-clickhouse",
            [IntegrationSchema.Key("aspire", "Workdir")] = "E:/repo/.intochat-os",
            [IntegrationSchema.Key("csharp", "RunTokenKey")] = "live-run-token",
            [IntoChatOSConfiguration.ModuleOptionsSection("ai") + ":DefaultModel"] = "deepseek",
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

        Assert.Equal("/app/home", host.LastRunEnvironment[DockerCandidateHost.Env(IntoChatOSNames.HomeKey)]);
        var forbidden = live.Where(pair => pair.Key.StartsWith(IntegrationSchema.SectionName, StringComparison.Ordinal) || pair.Key.StartsWith("ConnectionStrings:", StringComparison.Ordinal))
            .Select(pair => pair.Value!).ToArray();
        foreach (var environment in new[] { host.LastRunEnvironment, host.LastMcpEnvironment })
        {
            Assert.All(environment, pair => Assert.DoesNotContain(forbidden, value => pair.Value.Contains(value, StringComparison.Ordinal)));
        }
        Assert.Equal("model-key", host.LastRunEnvironment[DockerCandidateHost.IntegrationVariable("openrouter", "ApiKey")]);
        Assert.Equal("https://jev", host.LastRunEnvironment[DockerCandidateHost.IntegrationVariable("typesafe", "Endpoint")]);
        Assert.DoesNotContain(host.LastMcpEnvironment.Values, value => value is "model-key" or "https://jev");
        Assert.Equal(32, Convert.FromBase64String(host.LastRunEnvironment[IntoChatOSNames.MasterKeyEnvironmentVariable]).Length);
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
            host.LastMcpEnvironment[DockerCandidateHost.ConnectionVariable(IntoChatOSNames.Clustering)]);
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

    private static string ProjectPath(string name)
        => XDocument.Load(Path.Combine(RepositoryRoot, "Intochat.OS.slnx")).Descendants("Project")
            .Select(element => element.Attribute("Path")!.Value.Replace('\\', '/'))
            .Single(path => Path.GetFileNameWithoutExtension(path) == name);

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Intochat.OS.slnx")))
        { directory = directory.Parent; }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Intochat.OS.slnx was not found.");
    }

    private DockerCandidateHost Host(Dictionary<string, string?>? live = null, CandidateSecrets? secrets = null)
        => new(processes, new ConfigurationBuilder().AddInMemoryCollection(live ?? []).Build(),
            _ => Task.FromResult(workdir), _ => Task.FromResult(secrets ?? new CandidateSecrets(new Dictionary<string, string>())),
            _ => false, new HttpClient(), (_, _, _) => Task.CompletedTask);

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        private readonly List<(string Prefix, ProcessResult Result)> answers = [];

        public List<string> Commands { get; } = [];
        public List<string> DotnetDirectories { get; } = [];

        public void Answer(string prefix, ProcessResult result) => answers.Add((prefix, result));

        public Task<ProcessResult> RunAsync(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
        {
            var command = $"{file} {string.Join(' ', args)}";
            Commands.Add(command);
            if (file == "dotnet") { DotnetDirectories.Add(workingDirectory); }
            return Task.FromResult(answers.FirstOrDefault(answer => command.StartsWith(answer.Prefix, StringComparison.Ordinal)).Result ?? new(0, "", ""));
        }

        public int Detach(string file, string[] args, string workingDirectory)
            => throw new NotSupportedException();
    }
}
