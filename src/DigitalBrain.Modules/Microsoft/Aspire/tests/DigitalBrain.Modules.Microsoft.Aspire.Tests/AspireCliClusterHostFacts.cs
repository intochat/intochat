using DigitalBrain.Microsoft.Aspire.Candidates;
using DigitalBrain.Microsoft.Aspire.Cluster;

namespace DigitalBrain.Microsoft.Aspire.Tests;

// The cluster host keeps {Workdir}/live.json and drives the live AppHost through the aspire CLI.
public sealed class AspireCliClusterHostFacts : IDisposable
{
    private readonly string workdir = Directory.CreateTempSubdirectory("live-build-").FullName;

    public void Dispose() => Directory.Delete(workdir, recursive: true);

    [Fact]
    public async Task SetLiveRefusesABuildThatWasNotPublished()
    {
        var host = new AspireCliClusterHost(new RecordingRunner(""), _ => Task.FromResult(workdir));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => host.SetLive("missing", TestContext.Current.CancellationToken));
        Assert.Equal(string.Empty, await host.CurrentSha(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SilosAreTheOperatingSystemResourcesTheCliDescribes()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(workdir, "builds", "abc1234"));
        var runner = new RecordingRunner("""
            info: something
            {"resources":[
              {"name":"OperatingSystem-b-x1","displayName":"OperatingSystem-b","state":"Running","healthStatus":"Healthy"},
              {"name":"OperatingSystem-a-x2","displayName":"OperatingSystem-a","state":"Starting"},
              {"name":"OperatingSystem-front-x3","displayName":"OperatingSystem-front","state":"Running","healthStatus":"Healthy"},
              {"name":"OperatingSystem-rebuilder-x4","displayName":"OperatingSystem-rebuilder","state":"NotStarted"},
              {"name":"digitalbrain-mcp-a-y2","displayName":"digitalbrain-mcp-a","state":"Running","healthStatus":"Healthy"}]}
            """);
        var host = new AspireCliClusterHost(runner, _ => Task.FromResult(workdir));
        await host.SetLive("abc1234", ct);

        var silos = await host.Silos(ct);

        Assert.Equal([new SiloView("OperatingSystem-a", "abc1234", "Starting", null), new SiloView("OperatingSystem-b", "abc1234", "Running", "Healthy")], silos);
        Assert.Equal(["describe", "--format", "Json", "--apphost"], runner.Calls.Single().Take(4));
    }

    [Fact]
    public async Task RestartingRestartsTheMcpServersBeforeTheSilosAndTheSiloThatAskedLast()
    {
        var runner = new RecordingRunner("""
            {"resources":[
              {"name":"OperatingSystem-a-x1","displayName":"OperatingSystem-a"},{"name":"OperatingSystem-b-x2","displayName":"OperatingSystem-b"},
              {"name":"OperatingSystem-front-x3","displayName":"OperatingSystem-front"},
              {"name":"digitalbrain-mcp-b-y1","displayName":"digitalbrain-mcp-b"},{"name":"digitalbrain-mcp-a-y2","displayName":"digitalbrain-mcp-a"}]}
            """);
        var host = new AspireCliClusterHost(runner, _ => Task.FromResult(workdir), "OperatingSystem-a");

        await host.RestartSilos(TestContext.Current.CancellationToken);

        Assert.Equal([["describe", "--format", "Json"], ["resource", "digitalbrain-mcp-a", "restart"], ["resource", "digitalbrain-mcp-b", "restart"], ["resource", "OperatingSystem-b", "restart"]],
            runner.Calls.Select(call => call.Take(3).ToArray()));
        Assert.Equal([["resource", "OperatingSystem-a", "restart"]], runner.Detached.Select(call => call.Take(3).ToArray()));
    }

    [Fact]
    public async Task InstancesRollMcpServersFirstAndTheCallingSiloRestartsDetached()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new RecordingRunner("""
            {"resources":[
              {"displayName":"OperatingSystem-b"},{"displayName":"OperatingSystem-a"},{"displayName":"OperatingSystem-front"},
              {"displayName":"digitalbrain-mcp-b"},{"displayName":"digitalbrain-mcp-a"}]}
            """);
        var host = new AspireCliClusterHost(runner, _ => Task.FromResult(workdir), "OperatingSystem-a");

        var instances = await host.Instances(ct);
        await host.RestartInstance("OperatingSystem-b", ct);
        await host.RestartInstance("OperatingSystem-a", ct);

        Assert.Equal(["digitalbrain-mcp-a", "digitalbrain-mcp-b", "OperatingSystem-a", "OperatingSystem-b"], instances);
        Assert.Equal(["resource", "OperatingSystem-b", "restart"], runner.Calls[^1].Take(3));
        Assert.Equal([["resource", "OperatingSystem-a", "restart"]], runner.Detached.Select(call => call.Take(3).ToArray()));
    }

    [Fact]
    public async Task AnInstanceThatExitsWhileStartingNeverBecomesReady()
    {
        var runner = new RecordingRunner("""{"resources":[{"displayName":"OperatingSystem-b","state":"Exited"}]}""");
        var host = new AspireCliClusterHost(runner, _ => Task.FromResult(workdir), "OperatingSystem-a");

        await Assert.ThrowsAsync<InstanceNeverReadyException>(() => host.WaitInstance("OperatingSystem-b", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AnInstanceIsReachedOnItsOwnHttpUrl()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""{"urls":[{"name":"https","url":"https://localhost:7001"},{"name":"http","url":"http://localhost:5001"}]}""");

        Assert.Equal(new Uri("http://localhost:5001"), AspireCliClusterHost.Endpoint(json.RootElement));
    }

    [Fact]
    public async Task SpansCountTheNamedSpansOfTheService()
    {
        var runner = new RecordingRunner("""
            [{"name":"features.load","source":"DigitalBrain.OS"},{"name":"features.load","source":"DigitalBrain.OS"},
             {"name":"features.load","source":"DigitalBrain.OS-mcp"},{"name":"features.load","source":"DigitalBrain.Mcp"},{"name":"other","source":"DigitalBrain.OS"}]
            """);
        var host = new AspireCliClusterHost(runner, _ => Task.FromResult(workdir));

        Assert.Equal(3, await host.Spans("DigitalBrain.OS", "features.load", TestContext.Current.CancellationToken));
        Assert.Equal(["otel", "spans", "--format", "Json", "--search", "features.load", "--limit", "10000"], runner.Calls.Single().Take(8));
    }

    [Fact]
    public void TheJsonStartsAtTheFirstLineThatOpensADocument()
        => Assert.Equal("[{\"a\":\"[x]\"}]", AspireCliClusterHost.JsonPart("info: started [ok]\n[{\"a\":\"[x]\"}]"));

    [Fact]
    public void OutputWithoutJsonIsAnError()
        => Assert.Throws<InvalidOperationException>(() => AspireCliClusterHost.JsonPart("Resource 'x' not found."));

    private sealed class RecordingRunner(string output) : IProcessRunner
    {
        public List<string[]> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
        {
            Calls.Add(args);
            return Task.FromResult(new ProcessResult(0, output, ""));
        }

        public List<string[]> Detached { get; } = [];

        public int Detach(string file, string[] args, string workingDirectory)
        {
            Detached.Add(args);
            return 1;
        }
    }
}
