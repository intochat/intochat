using System.Net.Http.Json;
using DigitalBrain.Flutter.Chat;
using static DigitalBrain.Testing.Aspire.Diagnostics.TraceAssertions;

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
        var workspace = "workspace-" + Guid.NewGuid().ToString("N");
        await UserConnections.AuthorizeDatabaseAsync(brain, workspace, ct);
        await LeadData.SeedAsync(brain, "Capture", ct);
        await SendAsync(brain, workspace, ct);
        await WaitForAsync(() => host.Collector.Snapshot().Skip(before).Any(span => TextOf(span).Contains("Show active leads", StringComparison.Ordinal)), TimeSpan.FromSeconds(60), ct);
        Assert.Contains(host.Collector.Snapshot().Skip(before), span => span.Attributes.Keys.Any(key => key.StartsWith("gen_ai.usage", StringComparison.Ordinal)));
        Assert.Empty(host.Collector.Errors());
    }

    // A request reaches the app's synapse; the answer is the transcript neuron, so the wait is on the
    // neuron, never on an HTTP body.
    internal static async Task SendAsync(ITestBrain brain, string workspace, CancellationToken ct)
    {
        var chat = brain.Get<IChat>(workspace + "/apps/assistant/transcript");
        await using var changes = await brain.Observe<ChatChanged>(chat, ct);
        using var response = await brain.HttpClient.PostAsJsonAsync($"/brains/{workspace}/apps/assistant/request",
            new { operation = "send", input = "Show active leads" }, ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        ChatProjection? projected;
        while ((projected = (await chat.Read()).Projection) is null
            || projected.Messages.LastOrDefault()?.Status != "completed")
        { await changes.NextAsync(ct: ct); }
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
        var workspace = "workspace-" + Guid.NewGuid().ToString("N");
        await UserConnections.AuthorizeDatabaseAsync(brain, workspace, ct);
        await LeadData.SeedAsync(brain, "Capture", ct);
        await ContentCaptureOnFacts.SendAsync(brain, workspace, ct);
        await WaitForAsync(() => host.Collector.Snapshot().Skip(before).Any(IsGenAiSpan), TimeSpan.FromSeconds(60), ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        Assert.DoesNotContain(host.Collector.Snapshot().Skip(before), span => TextOf(span).Contains("Show active leads", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Collector.LogSnapshot(), log => TextOf(log).Contains("Show active leads", StringComparison.Ordinal));
        Assert.Empty(host.Collector.Errors());
    }
}
