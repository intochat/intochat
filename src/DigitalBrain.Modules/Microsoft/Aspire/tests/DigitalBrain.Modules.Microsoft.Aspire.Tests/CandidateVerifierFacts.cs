using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Mcp;
using DigitalBrain.Tools;
using DigitalBrain.Microsoft.Aspire.Cluster;
using DigitalBrain.Microsoft.Aspire.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Microsoft.Aspire.Tests;

public sealed class CandidateVerifierFacts
{
    private const string BrainId = "verifier-facts";
    private const string Sha = "sha0001";
    private const string ReadKey = "neurons_call DigitalBrain.Apps.IApp owner/apps/calculator Read";
    private static readonly NeuronExpectation Read = new("DigitalBrain.Apps.IApp", "owner/apps/calculator", "Read", "[]", "calculator");

    private static async Task<(ModuleBrain Brain, FakeClusterHost Cluster)> StartAsync(CancellationToken ct)
    {
        var cluster = new FakeClusterHost();
        var brain = await ModuleTest.Create().WithModule<AspireModule>().WithModule<McpModule>().WithModule<ToolsModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<IClusterHost>(cluster))
            .StartAsync(ct);
        CallerContextStamper.Stamp(new()
        { PrincipalId = "owner", AccountId = "owner", BrainId = BrainId, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
        return (brain, cluster);
    }

    private static Task<VerificationReport> Verify(ModuleBrain brain, ScriptedMcpServer server, VerificationPlan plan, CancellationToken ct)
        => brain.SiloServices.GetRequiredService<ICandidateVerifier>().Verify(BrainId, Sha, new(server.Endpoint, "http://ui"), plan, ct);

    [Fact]
    public async Task ACandidateAnsweringForAnotherBrainFailsTheContextCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await ScriptedMcpServer.StartAsync(ct);
        server.Answers["neurons_context"] = """{"principalId":"owner","brainId":"another"}""";
        var (brain, _) = await StartAsync(ct);
        await using var _ = brain;

        var report = await Verify(brain, server, new([], [], TimeSpan.FromMinutes(1)), ct);

        Assert.False(report.Passed);
        Assert.Equal("context", report.FirstFailure);
    }

    [Fact]
    public async Task ACallAnsweringTheExpectedTextPasses()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await ScriptedMcpServer.StartAsync(ct);
        server.Answers[ReadKey] = """{"name":"calculator"}""";
        var (brain, _) = await StartAsync(ct);
        await using var _ = brain;

        var report = await Verify(brain, server, new([Read], [], TimeSpan.FromMinutes(1)), ct);

        Assert.True(report.Passed);
        Assert.Equal(["context", "activation", "call DigitalBrain.Apps.IApp owner/apps/calculator Read"], report.Checks.Select(check => check.Name));
    }

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
        var (brain, _) = await StartAsync(ct);
        await using var _ = brain;

        var report = await Verify(brain, server, new([Read, Read], ["seen"], TimeSpan.FromMinutes(1)), ct);

        Assert.Equal(3, report.Checks.Length);
        Assert.Equal("call DigitalBrain.Apps.IApp owner/apps/calculator Read", report.FirstFailure);
        Assert.Equal(1, server.Calls.Count(call => call == ReadKey));
    }
}
