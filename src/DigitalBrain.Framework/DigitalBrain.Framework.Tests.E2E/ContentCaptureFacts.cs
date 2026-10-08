using System.Net.Http.Json;
using DigitalBrain.Testing.E2E;
using DigitalBrain.Testing.E2E.Workspace;
using static DigitalBrain.Testing.E2E.Diagnostics.TraceAssertions;

namespace DigitalBrain.Framework.Tests.E2E;

// The module option is the only content-capture switch. Both hosts run Production to catch
// accidental environment/profile gates; each fact reads only what its own turn exported.
public sealed class ContentCaptureOnFacts(ProductionBrainFixture host)
{
    [Fact(Timeout = 300_000)]
    public async Task TheModuleSettingExportsMessageContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await host.LeaseAsync(ct);
        var before = host.Collector.Snapshot().Count;
        await UserConnections.AuthorizeDatabaseAsync(brain, brain.WorkspaceId, ct);
        await LeadData.SeedAsync(brain, "Capture", ct);
        using var response = await brain.HttpClient.PostAsJsonAsync($"/brains/{brain.WorkspaceId}/apps/assistant/ask",
            new { operation = "send", input = "Show active leads" }, ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        await WaitForAsync(() => host.Collector.Snapshot().Skip(before).Any(span => TextOf(span).Contains("Show active leads", StringComparison.Ordinal)), TimeSpan.FromSeconds(60), ct);
        Assert.Contains(host.Collector.Snapshot().Skip(before), span => span.Attributes.Keys.Any(key => key.StartsWith("gen_ai.usage", StringComparison.Ordinal)));
        Assert.Empty(host.Collector.Errors());
    }
}

public sealed class ContentCaptureOffFacts(ProductionCaptureOffFixture host)
{
    [Fact(Timeout = 300_000)]
    public async Task TheModuleSettingKeepsMessageContentOutOfExports()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await host.LeaseAsync(ct);
        var before = host.Collector.Snapshot().Count;
        await UserConnections.AuthorizeDatabaseAsync(brain, brain.WorkspaceId, ct);
        await LeadData.SeedAsync(brain, "Capture", ct);
        using var response = await brain.HttpClient.PostAsJsonAsync($"/brains/{brain.WorkspaceId}/apps/assistant/ask",
            new { operation = "send", input = "Show active leads" }, ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        await WaitForAsync(() => host.Collector.Snapshot().Skip(before).Any(IsGenAiSpan), TimeSpan.FromSeconds(60), ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        Assert.DoesNotContain(host.Collector.Snapshot().Skip(before), span => TextOf(span).Contains("Show active leads", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Collector.LogSnapshot(), log => TextOf(log).Contains("Show active leads", StringComparison.Ordinal));
        Assert.Empty(host.Collector.Errors());
    }
}
