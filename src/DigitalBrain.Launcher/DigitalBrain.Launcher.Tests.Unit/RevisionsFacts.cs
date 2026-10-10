using System.Net;
using DigitalBrain.Launcher.Processes;
using DigitalBrain.Launcher.Proxy;
using DigitalBrain.Launcher.Revisions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Yarp.ReverseProxy.Configuration;
using LauncherRevisions = DigitalBrain.Launcher.Revisions.Revisions;

namespace DigitalBrain.Launcher.Tests.Unit;

public sealed class RevisionsFacts : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("launcher-").FullName;
    private readonly FakeProcessRunner _processes = new();
    private readonly FakeStorageCopier _storage = new();
    private readonly RevisionProxy _proxy = new(new InMemoryConfigProvider([], []), 5080, 5081);
    private readonly HealthProbe _health = new(_ => Task.FromResult(HttpStatusCode.OK));

    private LauncherRevisions Subject() => new(new LauncherOptions { Repository = _root, RevisionsPath = Path.Combine(_root, "revisions.json"), CurrentContainer = "digitalbrain-v2-state" },
        _processes, _storage, _proxy, _health, NullLogger<LauncherRevisions>.Instance);

    [Fact]
    public async Task ProposingWritesTheFilesIntoAFreshWorktreeAndCommitsThem()
    {
        var ct = TestContext.Current.CancellationToken;
        _processes.Answer = (_, args) => args[0] == "rev-parse" ? new(0, "abc1234\n", "") : new(0, "", "");
        var revision = await Subject().ProposeAsync("os-runtime", "Calculator: add percent", new Dictionary<string, string> { ["features/os/Calculator/app.cs"] = "// app" }, ct);
        Assert.Equal("abc1234", revision.Sha);
        Assert.Equal((5090, 5091), (revision.UiPort, revision.McpPort));
        Assert.Equal("created", revision.State);
        var worktree = Path.Combine(_root, ".revisions", "abc1234");
        Assert.Contains(_processes.Calls, call => call.StartsWith("git worktree add --detach", StringComparison.Ordinal) && call.Contains("HEAD", StringComparison.Ordinal));
        Assert.Contains(_processes.Calls, call => call.StartsWith("git add -A", StringComparison.Ordinal));
        Assert.Contains(_processes.Calls, call => call.StartsWith("git commit -m Calculator: add percent", StringComparison.Ordinal));
        Assert.Equal("abc1234", RevisionsFile.Read(Path.Combine(_root, "revisions.json")).Revisions.Single().Sha);
        Assert.Equal(worktree, revision.Worktree);
    }

    [Fact]
    public async Task StartingCopiesStorageThenRunsAspireWithTheRevisionIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        _processes.Answer = (_, args) => args[0] == "rev-parse" ? new(0, "abc1234\n", "") : new(0, "", "");
        var subject = Subject();
        await subject.ProposeAsync("os-runtime", "change", new Dictionary<string, string>(), ct);
        var started = await subject.StartAsync("abc1234", ct);
        Assert.Equal(("digitalbrain-v2-state", "brain-rabc1234"), _storage.Copies.Single());
        var process = _processes.Started.Single();
        Assert.Equal("intochat-rabc1234", process.Environment["Orleans__ServiceId"]);
        Assert.Equal("intochat-rabc1234", process.Environment["Orleans__ClusterId"]);
        Assert.Equal("brain-rabc1234", process.Environment["DigitalBrain__Orleans__GrainStateContainer"]);
        Assert.Equal("5090", process.Environment["DigitalBrain__Http__UiPort"]);
        Assert.Equal("5091", process.Environment["DigitalBrain__Http__McpPort"]);
        Assert.Contains(_processes.Calls, call => call.StartsWith("aspire run --non-interactive --project", StringComparison.Ordinal) && call.EndsWith("@" + started.Worktree, StringComparison.Ordinal));
        Assert.Equal("starting", started.State);
        Assert.Equal("healthy", (await subject.WaitHealthyAsync("abc1234", TimeSpan.FromSeconds(5), ct)).State);
    }

    [Fact]
    public async Task PromoteFlipsTheProxyThenStopsThePreviousCurrent()
    {
        var ct = TestContext.Current.CancellationToken;
        var shas = new Queue<string>(["aaaa111", "bbbb222"]);
        _processes.Answer = (_, args) => args[0] == "rev-parse" ? new(0, shas.Dequeue() + "\n", "") : new(0, "", "");
        var subject = Subject();
        await subject.ProposeAsync("os-runtime", "first", new Dictionary<string, string>(), ct);
        await subject.StartAsync("aaaa111", ct);
        await subject.PromoteAsync("aaaa111", ct);
        Assert.Equal((5090, 5091), _proxy.Target);
        await subject.ProposeAsync("os-runtime", "second", new Dictionary<string, string>(), ct);
        await subject.StartAsync("bbbb222", ct);
        var promoted = await subject.PromoteAsync("bbbb222", ct);
        Assert.Equal((5100, 5101), _proxy.Target);
        Assert.Equal("bbbb222", RevisionsFile.Read(Path.Combine(_root, "revisions.json")).Current);
        Assert.True(_processes.Started[0].Stopped);
        Assert.False(_processes.Started[1].Stopped);
        Assert.Equal("current", promoted.State);
        Assert.Equal("stopped", subject.List().Require("aaaa111").State);
        Assert.Contains(_processes.Calls, call => call.StartsWith("git update-ref refs/heads/os-runtime bbbb222", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscardStopsTheProcessAndKeepsTheWorktree()
    {
        var ct = TestContext.Current.CancellationToken;
        _processes.Answer = (_, args) => args[0] == "rev-parse" ? new(0, "abc1234\n", "") : new(0, "", "");
        var subject = Subject();
        await subject.ProposeAsync("os-runtime", "change", new Dictionary<string, string>(), ct);
        await subject.StartAsync("abc1234", ct);
        await subject.DiscardAsync("abc1234", ct);
        Assert.True(_processes.Started.Single().Stopped);
        Assert.Equal("stopped", subject.List().Require("abc1234").State);
        Assert.DoesNotContain(_processes.Calls, call => call.StartsWith("git worktree remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnhealthyChildWithinTheBudgetIsReportedNotRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        _processes.Answer = (_, args) => args[0] == "rev-parse" ? new(0, "abc1234\n", "") : new(0, "", "");
        var probes = 0;
        var subject = new LauncherRevisions(new LauncherOptions { Repository = _root, RevisionsPath = Path.Combine(_root, "revisions.json") },
            _processes, _storage, _proxy, new HealthProbe(_ => { probes++; return Task.FromResult(HttpStatusCode.ServiceUnavailable); }), NullLogger<LauncherRevisions>.Instance);
        await subject.ProposeAsync("os-runtime", "change", new Dictionary<string, string>(), ct);
        await subject.StartAsync("abc1234", ct);
        var error = await Assert.ThrowsAsync<TimeoutException>(() => subject.WaitHealthyAsync("abc1234", TimeSpan.FromMilliseconds(50), ct));
        Assert.Contains("abc1234", error.Message, StringComparison.Ordinal);
        Assert.Equal("failed", subject.List().Require("abc1234").State);
        Assert.True(probes >= 1);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
