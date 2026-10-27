using IntoChat.OS.Modules.Mcp;
using IntoChat.OS.Modules.Microsoft.Aspire.Deployment;
using IntoChat.OS.Modules.Microsoft.Aspire.Verification;
using IntoChat.OS.Modules.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Aspire.Tests;

public sealed class CandidateVerifierFacts(AspireHost host)
{
    private const string Sha = "sha0001";
    private const string ReadKey = "neurons_call IntoChat.OS.Modules.Apps.IApp owner/apps/calculator Read";
    private static readonly NeuronExpectation Read = new("IntoChat.OS.Modules.Apps.IApp", "owner/apps/calculator", "Read", "[]", "calculator");

    // The aspire CLI is a process-level seam; the candidate's MCP is the scripted server on loopback.
    private static async Task<(ITestBrain Brain, FakeClusterHost Cluster)> StartAsync(CancellationToken ct)
    {
        var cluster = new FakeClusterHost();
        var brain = await Compose(TestBrain.Create(), cluster).StartAsync(ct);
        return (brain, cluster);
    }

    internal static TestBrain Compose(TestBrain brain, FakeClusterHost cluster)
        => brain.WithModule<AspireModule>().WithModule<McpModule>().WithModule<ToolsModule>()
            .ConfigureServices(services => services.AddSingleton<IClusterHost>(cluster));

    // The service the module runs, read from the host and driven as the owner, as the candidate neuron drives it.
    private static async Task<VerificationReport> Verify(ITestBrain brain, string endpoint, VerificationPlan plan, CancellationToken ct)
    {
        using var caller = brain.Owner.AsCaller();
        return await brain.Services.GetRequiredService<ICandidateVerifier>().Verify(brain.Owner.BrainId, Sha, new(endpoint, "http://ui"), plan, ct);
    }

    private static Task<VerificationReport> Verify(ITestBrain brain, ScriptedMcpServer server, VerificationPlan plan, CancellationToken ct)
        => Verify(brain, server.Endpoint, plan, ct);

    [Fact]
    public async Task ACandidateAnsweringForAnotherBrainFailsTheContextCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await ScriptedMcpServer.StartAsync(ct);
        server.Answers["neurons_context"] = """{"principalId":"owner","brainId":"another"}""";
        var brain = host.Brain;

        var report = await Verify(brain, server, new([], [], TimeSpan.FromMinutes(1)), ct);

        Assert.False(report.Passed);
        Assert.Equal("context", report.FirstFailure);
    }

    // The candidate's features reconcile on its brain's activation, asynchronously: the check reconciles them
    // itself, so the plan's calls meet the candidate's apps, not the copied versions.
    [Fact]
    public async Task TheActivationCheckReconcilesTheBrainsFeaturesBeforeListingItsApps()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await ScriptedMcpServer.StartAsync(ct);
        var brain = host.Brain;

        var report = await Verify(brain, server, new([], [], TimeSpan.FromMinutes(1)), ct);

        Assert.True(report.Passed);
        Assert.Equal([$"neurons_call IntoChat.OS.Modules.Apps.IFeatureReconciler {brain.Owner.BrainId}/apps-reconciler Reconcile", "neurons_list IntoChat.OS.Modules.Apps.IApp"], server.Calls.Skip(1));
    }

    [Fact]
    public async Task ACallAnsweringTheExpectedTextPasses()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await ScriptedMcpServer.StartAsync(ct);
        server.Answers[ReadKey] = """{"name":"calculator"}""";
        var brain = host.Brain;

        var report = await Verify(brain, server, new([Read], [], TimeSpan.FromMinutes(1)), ct);

        Assert.True(report.Passed);
        Assert.Equal(["context", "activation", "call IntoChat.OS.Modules.Apps.IApp owner/apps/calculator Read"], report.Checks.Select(check => check.Name));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AMissingSpanFailsWithItsCount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await ScriptedMcpServer.StartAsync(ct);
        var (brain, cluster) = await StartAsync(ct);
        await using var _ = brain;
        cluster.SpanCounts[$"candidate-{Sha} seen"] = 2;

        var report = await Verify(brain, server, new([], ["seen", "missing"], TimeSpan.FromMinutes(1)), ct);

        Assert.Equal("span missing", report.FirstFailure);
        Assert.Equal("0 span(s)", report.Checks[^1].Detail);
        Assert.Contains(report.Checks, check => check is { Name: "span seen", Passed: true });
    }

    [Fact]
    public async Task VerificationStopsAtTheFirstFailedCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await ScriptedMcpServer.StartAsync(ct);
        server.Answers[ReadKey] = """{"name":"other"}""";
        var brain = host.Brain;

        var report = await Verify(brain, server, new([Read, Read], ["seen"], TimeSpan.FromMinutes(1)), ct);

        Assert.Equal(3, report.Checks.Length);
        Assert.Equal("call IntoChat.OS.Modules.Apps.IApp owner/apps/calculator Read", report.FirstFailure);
        Assert.Equal(1, server.Calls.Count(call => call == ReadKey));
    }

    [Fact]
    public async Task ACandidateThatCannotBeReachedFailsTheConnectCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;

        var report = await Verify(brain, "http://127.0.0.1:1", new([], [], TimeSpan.FromMinutes(1)), ct);

        Assert.Equal("connect", report.FirstFailure);
        Assert.Single(report.Checks);
    }

    [Fact]
    public async Task AContextAnswerWithoutABrainIdFailsTheContextCheckByName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await ScriptedMcpServer.StartAsync(ct);
        server.Answers["neurons_context"] = "[]";
        var brain = host.Brain;

        var report = await Verify(brain, server, new([], [], TimeSpan.FromMinutes(1)), ct);

        Assert.Equal("context", report.FirstFailure);
    }

    [Fact]
    public async Task AnAppListErrorFailsTheActivationCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await ScriptedMcpServer.StartAsync(ct);
        server.Failures.Add("neurons_list IntoChat.OS.Modules.Apps.IApp");
        var brain = host.Brain;

        var report = await Verify(brain, server, new([Read], [], TimeSpan.FromMinutes(1)), ct);

        Assert.Equal("activation", report.FirstFailure);
        Assert.Equal(2, report.Checks.Length);
    }
}
