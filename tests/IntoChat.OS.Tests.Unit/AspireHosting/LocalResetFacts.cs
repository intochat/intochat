using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class LocalResetFacts
{
    private const string Hash = "5ef44d53ff0123456789";
    private static readonly string Repo = Path.GetFullPath("/fake/repo");
    private static readonly string Local = Path.GetFullPath("/fake/local");
    private static readonly string Secrets = Path.GetFullPath("/fake/secrets/id");
    private static readonly string WorkDir = Path.Combine(Repo, ".intochat-os");
    private static readonly string Home = Path.GetFullPath("/fake/home");
    // reset clears <Home>/state, never the rest of the Home.
    private static readonly string Runtime = Path.Combine(Home, "state");

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "x";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class FakeFileSystem : IResetFileSystem
    {
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase) { WorkDir, Runtime, Secrets };
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase) { Path.Combine(Local, "IntoChatOS", "machine-id") };
        public HashSet<string> Reparse { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Deleted { get; } = [];
        public bool DirectoryExists(string path) => Directories.Contains(path);
        public bool FileExists(string path) => Files.Contains(path);
        public IReadOnlyList<(string Name, long Bytes)> Children(string directory) => [("brain-store", 10), ("x.log", 5)];
        public bool IsReparsePoint(string path) => Reparse.Contains(path);
        public void Delete(string path) => Deleted.Add(path);
    }

    private sealed class FakeDocker : IDockerCli
    {
        public List<string> Containers { get; } = ["Modules-storage-5ef44d53", "Modules-storage-aaaaaaaa", "unrelated"];
        public List<string> Volumes { get; } = ["intochat.os.apphost-5ef44d53ff-Modules-storage-data", "intochat.os.apphost-1111111111-Modules-storage-data", "intochat-os-csharp-cache", "other"];
        public List<string> Removed { get; } = [];
        public Task<IReadOnlyList<string>> ContainerNamesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(Containers);
        public Task<IReadOnlyList<string>> VolumeNamesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(Volumes);
        public Task RemoveContainerAsync(string name, CancellationToken cancellationToken) { Removed.Add("container:" + name); return Task.CompletedTask; }
        public Task RemoveVolumeAsync(string name, CancellationToken cancellationToken) { Removed.Add("volume:" + name); return Task.CompletedTask; }
    }

    private static (LocalReset Reset, FakeFileSystem Fs, FakeDocker Docker, StringWriter Out) Make(string environment = "Development", bool runMode = true, string answer = "yes", bool interactive = true)
    {
        var fs = new FakeFileSystem();
        var docker = new FakeDocker();
        var output = new StringWriter();
        return (new LocalReset(new FakeEnvironment(environment), runMode, Repo, Local, Home, Hash, Secrets, fs, docker, new StringReader(answer + "\n"), output, interactive), fs, docker, output);
    }

    [Fact]
    public async Task ProductionRefusesAndDeletesNothing()
    {
        var (reset, fs, docker, output) = Make("Production");
        Assert.Equal(1, await reset.RunAsync(["--yes"], TestContext.Current.CancellationToken));
        Assert.Empty(fs.Deleted); Assert.Empty(docker.Removed);
        Assert.Contains("refused", output.ToString());
    }

    [Fact]
    public async Task PublishModeRefusesAndDeletesNothing()
    {
        var (reset, fs, docker, _) = Make(runMode: false);
        Assert.Equal(1, await reset.RunAsync(["--yes"], TestContext.Current.CancellationToken));
        Assert.Empty(fs.Deleted); Assert.Empty(docker.Removed);
    }

    [Fact]
    public async Task ThePlanListsWorkdirHomeStateContainersAndVolumesByThisCheckoutsHash()
    {
        var (reset, _, _, _) = Make();
        var plan = await reset.BuildPlanAsync(false, false, TestContext.Current.CancellationToken);
        Assert.Equal(
            ["directory:" + WorkDir, "directory:" + Runtime, "container:Modules-storage-5ef44d53",
             "volume:intochat.os.apphost-5ef44d53ff-Modules-storage-data", "volume:intochat-os-csharp-cache"],
            plan.Select(t => t.Kind + ":" + t.Reference));
    }

    [Fact]
    public async Task ThePlanListsTheOllamaContainerUnderItsOldAndNewNames()
    {
        var (reset, _, docker, _) = Make();
        docker.Containers.AddRange(["Modules-ollama-5ef44d53", "ai-ollama-5ef44d53", "ai-ollama-aaaaaaaa"]);
        var plan = await reset.BuildPlanAsync(false, false, TestContext.Current.CancellationToken);
        var containers = plan.Where(t => t.Kind == "container").Select(t => t.Reference).ToArray();
        Assert.Contains("Modules-ollama-5ef44d53", containers);
        Assert.Contains("ai-ollama-5ef44d53", containers);
        Assert.DoesNotContain("ai-ollama-aaaaaaaa", containers);
    }

    [Fact]
    public async Task ThePlanListsSearxngAndOpenWebUIUnderTheirOldAndNewNames()
    {
        var (reset, _, docker, _) = Make();
        string[] expected = ["Modules-searxng-5ef44d53", "Modules-ollama-openwebui-5ef44d53", "web-searxng-5ef44d53", "ai-ollama-openwebui-5ef44d53"];
        docker.Containers.AddRange([.. expected, "web-searxng-aaaaaaaa"]);
        var plan = await reset.BuildPlanAsync(false, false, TestContext.Current.CancellationToken);
        var containers = plan.Where(t => t.Kind == "container").Select(t => t.Reference).ToArray();
        Assert.All(expected, name => Assert.Contains(name, containers));
        Assert.DoesNotContain("web-searxng-aaaaaaaa", containers);
    }

    [Fact]
    public async Task ListIsADryRunWithoutPrompt()
    {
        var (reset, fs, docker, output) = Make(answer: "yes");
        Assert.Equal(0, await reset.RunAsync(["--list"], TestContext.Current.CancellationToken));
        Assert.Empty(fs.Deleted); Assert.Empty(docker.Removed);
        Assert.DoesNotContain("Type yes", output.ToString());
    }

    [Fact]
    public async Task AnythingButYesAbortsAndDeletesNothing()
    {
        var (reset, fs, docker, output) = Make(answer: "no");
        Assert.Equal(0, await reset.RunAsync([], TestContext.Current.CancellationToken));
        Assert.Empty(fs.Deleted); Assert.Empty(docker.Removed);
        Assert.Contains("aborted", output.ToString());
    }

    [Fact]
    public async Task YesDeletesExactlyThePlanContainersBeforeVolumes()
    {
        var (reset, fs, docker, _) = Make();
        Assert.Equal(0, await reset.RunAsync([], TestContext.Current.CancellationToken));
        Assert.Equal([WorkDir, Runtime], fs.Deleted);
        Assert.Equal(["container:Modules-storage-5ef44d53", "volume:intochat.os.apphost-5ef44d53ff-Modules-storage-data", "volume:intochat-os-csharp-cache"], docker.Removed);
    }

    [Fact]
    public async Task SecretsAndMachineIdAreKeptByDefault()
    {
        var (reset, fs, _, _) = Make();
        await reset.RunAsync([], TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Secrets, fs.Deleted);
        Assert.DoesNotContain(fs.Deleted, p => p.EndsWith("machine-id"));
    }

    [Fact]
    public async Task IncludeFlagsAddSecretsAndMachineId()
    {
        var (reset, fs, _, _) = Make();
        await reset.RunAsync(["--include-secrets", "--include-machine-id"], TestContext.Current.CancellationToken);
        Assert.Contains(Secrets, fs.Deleted);
        Assert.Contains(fs.Deleted, p => p.EndsWith("machine-id"));
    }

    [Fact]
    public async Task ReparsePointsAreSkippedNotFollowed()
    {
        var (reset, fs, _, output) = Make();
        fs.Reparse.Add(Runtime);
        await reset.RunAsync([], TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Runtime, fs.Deleted);
        Assert.Contains("reparse point", output.ToString());
    }

    [Fact]
    public async Task NonInteractiveWithoutYesRefuses()
    {
        var (reset, fs, docker, _) = Make(interactive: false);
        Assert.Equal(1, await reset.RunAsync([], TestContext.Current.CancellationToken));
        Assert.Empty(fs.Deleted); Assert.Empty(docker.Removed);
    }

    [Fact]
    public async Task NonInteractiveWithYesProceeds()
    {
        var (reset, fs, _, _) = Make(interactive: false);
        Assert.Equal(0, await reset.RunAsync(["--yes"], TestContext.Current.CancellationToken));
        Assert.Equal(2, fs.Deleted.Count);
    }

    [Fact]
    public async Task AnotherCheckoutsContainersAndVolumesAreNeverTouched()
    {
        var (reset, _, docker, _) = Make();
        await reset.RunAsync([], TestContext.Current.CancellationToken);
        Assert.DoesNotContain(docker.Removed, r => r.Contains("aaaaaaaa") || r.Contains("1111111111") || r.Contains("other") || r.Contains("unrelated"));
    }
}
