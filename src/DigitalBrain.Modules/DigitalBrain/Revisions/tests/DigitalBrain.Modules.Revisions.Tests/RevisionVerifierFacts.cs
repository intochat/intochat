using DigitalBrain.Revisions;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Revisions.Tests;

public sealed class RevisionVerifierFacts
{
    private const string AspireJson = "{\"Resources\":{\"OperatingSystem\":{\"Name\":\"OperatingSystem\",\"Type\":\"Project\",\"State\":\"Running\",\"Health\":\"Healthy\",\"Urls\":[]},\"digitalbrain-mcp\":{\"Name\":\"digitalbrain-mcp\",\"Type\":\"Project\",\"State\":\"Running\",\"Health\":null,\"Urls\":[]}}}";

    private static RevisionVerifier Subject(FakeLauncher launcher)
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        return new RevisionVerifier(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), launcher);
    }

    [Fact]
    public async Task EveryExpectationIsCheckedAgainstTheChildAndReported()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var child = await ScriptedMcpServer.StartAsync(ct);
        child.Answers["neurons_get DigitalBrain.Microsoft.Aspire.IAspire DigitalBrain.OS"] = AspireJson;
        child.Answers["neurons_call DigitalBrain.Apps.IApp owner/apps/calculator Read"] = "{\"Name\":\"calculator\",\"Manifest\":{\"Operations\":[{\"Name\":\"open\"},{\"Name\":\"percent\"}]}}";
        var launcher = new FakeLauncher { SpanCounts = { ["Neuron/Read"] = 3 } };
        var plan = new VerificationPlan(
            [new("DigitalBrain.Apps.IApp", "owner/apps/calculator", "Read", "[]", "\"Name\":\"percent\"")],
            ["OperatingSystem"], ["Neuron/Read"], TimeSpan.FromMinutes(1));
        var report = await Subject(launcher).Verify("owner", child.Endpoint, plan, ct);
        Assert.True(report.Passed, string.Join("; ", report.Checks.Select(check => check.Name + ": " + check.Detail)));
        Assert.Equal(["context", "resource OperatingSystem", "call DigitalBrain.Apps.IApp owner/apps/calculator Read", "span Neuron/Read"], report.Checks.Select(check => check.Name));
        Assert.Equal(["neurons_context", "neurons_get DigitalBrain.Microsoft.Aspire.IAspire DigitalBrain.OS", "neurons_call DigitalBrain.Apps.IApp owner/apps/calculator Read"], child.Calls);
    }

    [Fact]
    public async Task AMismatchedExpectationNamesTheCheckThatFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var child = await ScriptedMcpServer.StartAsync(ct);
        child.Answers["neurons_call DigitalBrain.Apps.IApp owner/apps/calculator Read"] = "{\"Manifest\":{\"Operations\":[{\"Name\":\"open\"}]}}";
        var plan = new VerificationPlan([new("DigitalBrain.Apps.IApp", "owner/apps/calculator", "Read", "[]", "\"Name\":\"percent\"")], [], [], TimeSpan.FromMinutes(1));
        var report = await Subject(new FakeLauncher()).Verify("owner", child.Endpoint, plan, ct);
        Assert.False(report.Passed);
        Assert.Equal("call DigitalBrain.Apps.IApp owner/apps/calculator Read", report.FirstFailure);
        Assert.Contains("\"Name\":\"percent\"", report.Checks.Last().Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnhealthyResourceFailsBeforeAnyCallIsMade()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var child = await ScriptedMcpServer.StartAsync(ct);
        child.Answers["neurons_get DigitalBrain.Microsoft.Aspire.IAspire DigitalBrain.OS"] = AspireJson;
        var plan = new VerificationPlan([new("DigitalBrain.Apps.IApp", "owner/apps/calculator", "Read", "[]", "*")], ["digitalbrain-mcp"], [], TimeSpan.FromMinutes(1));
        var report = await Subject(new FakeLauncher()).Verify("owner", child.Endpoint, plan, ct);
        Assert.Equal("resource digitalbrain-mcp", report.FirstFailure);
        Assert.DoesNotContain(child.Calls, call => call.StartsWith("neurons_call", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheChildMustAnswerForTheSameBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var child = await ScriptedMcpServer.StartAsync(ct);
        child.Answers["neurons_context"] = "{\"principalId\":\"owner\",\"brainId\":\"other\"}";
        var report = await Subject(new FakeLauncher()).Verify("owner", child.Endpoint, new([], [], [], TimeSpan.FromMinutes(1)), ct);
        Assert.Equal("context", report.FirstFailure);
    }

    [Fact]
    public async Task AMissingSpanIsAFailedCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var child = await ScriptedMcpServer.StartAsync(ct);
        var report = await Subject(new FakeLauncher()).Verify("owner", child.Endpoint, new([], [], ["Neuron/Promote"], TimeSpan.FromMinutes(1)), ct);
        Assert.Equal("span Neuron/Promote", report.FirstFailure);
    }
}