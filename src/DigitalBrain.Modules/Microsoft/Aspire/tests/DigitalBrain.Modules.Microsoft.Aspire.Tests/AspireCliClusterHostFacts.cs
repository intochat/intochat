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
              {"name":"OperatingSystem-x1","displayName":"OperatingSystem","state":"Running","healthStatus":"Healthy"},
              {"name":"digitalbrain-mcp-y2","displayName":"digitalbrain-mcp","state":"Running","healthStatus":"Healthy"}]}
            """);
        var host = new AspireCliClusterHost(runner, _ => Task.FromResult(workdir));
        await host.SetLive("abc1234", ct);

        var silos = await host.Silos(ct);

        Assert.Equal([new SiloView("OperatingSystem-x1", "abc1234", "Running", "Healthy")], silos);
        Assert.Equal(["describe", "--format", "Json", "--apphost"], runner.Calls.Single().Take(4));
    }

    [Fact]
    public async Task RestartingRestartsTheMcpServerBeforeTheSiloThatAsked()
    {
        var runner = new RecordingRunner("");
        var host = new AspireCliClusterHost(runner, _ => Task.FromResult(workdir));

        await host.RestartSilos(TestContext.Current.CancellationToken);

        Assert.Equal([["resource", "digitalbrain-mcp", "restart"], ["resource", "OperatingSystem", "restart"]],
            runner.Calls.Select(call => call.Take(3).ToArray()));
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

        public IRunningProcess Start(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string> environment)
            => throw new NotSupportedException();
    }
}
