using System.Net.Http.Json;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework.Tests.E2E;

// The module option is the only content-capture switch. Both hosts run Production to catch
// accidental environment/profile gates; each fact reads only what its own turn exported.
[Collection("ProductionBrainFixture")]
public sealed class ContentCaptureOnFacts(ProductionBrainFixture host) : BrainFact(host)
{
    [Fact(Timeout = 300_000)]
    public async Task TheModuleSettingExportsMessageContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = Brain;
        var workspace = User.BrainId;
        await UserConnections.AuthorizeDatabaseAsync(brain, User, ct);
        await LeadData.SeedAsync(brain, "Capture", ct);
        // A fresh account installs its persistent scripts asynchronously; measure the request after their initial projections.
        await AppRuntimeReadiness.WaitAsync(brain, User, ct);
        using var operation = Telemetry.BeginOperation(nameof(TheModuleSettingExportsMessageContent));
        await SendAsync(brain, workspace, operation, ct, User);
        var capture = await operation.CompleteAsync(ct);
        Assert.Contains(capture.Spans, span => span.Text.Contains("Show active leads", StringComparison.Ordinal));
        Assert.Contains(capture.Spans, span => span.Data.Attributes.Any(attribute => attribute.Key.StartsWith("gen_ai.usage", StringComparison.Ordinal)));
    }

    // A request reaches the app's synapse; the answer is the transcript neuron, so the wait is on the
    // neuron, never on an HTTP body.
    internal static async Task SendAsync(ITestBrain brain, string workspace, TelemetryOperation operation, CancellationToken ct, Person? person = null)
    {
        using var caller = person?.AsCaller();
        var chat = brain.Get<IChat>(workspace + "/apps/ino/transcript");
        await using var changes = await brain.Observe<ChatChanged>(chat, ct);
        using var request = operation.Request(HttpMethod.Post, $"/brains/{workspace}/apps/ino/request",
            JsonContent.Create(new { operation = "send", input = "Show active leads" }));
        using var response = await operation.SendAsync(person?.Http ?? brain.HttpClient, request, ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        ChatProjection? projected;
        while ((projected = (await chat.Read()).Projection) is null
            || projected.Messages.LastOrDefault()?.Status != "completed")
        { await changes.NextAsync(ct: ct); }
        await WaitIdleAsync(brain, person, workspace, ct);
    }

    internal static async Task WaitIdleAsync(ITestBrain brain, Person? person, string workspace, CancellationToken ct)
    {
        using var caller = person?.AsCaller();
        await TestWait.UntilAsync(token => brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(workspace)).Inspect(token),
            state => state.IsIdle, TimeSpan.FromSeconds(90), ct);
    }
}

[Collection("ProductionCaptureOffFixture")]
public sealed class ContentCaptureOffFacts(ProductionCaptureOffFixture host) : BrainFact(host)
{
    [Fact(Timeout = 300_000)]
    public async Task TheModuleSettingKeepsMessageContentOutOfExports()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = Brain;
        var workspace = User.BrainId;
        await UserConnections.AuthorizeDatabaseAsync(brain, User, ct);
        await LeadData.SeedAsync(brain, "Capture", ct);
        // A fresh account installs its persistent scripts asynchronously; measure the request after their initial projections.
        await AppRuntimeReadiness.WaitAsync(brain, User, ct);
        using var operation = Telemetry.BeginOperation(nameof(TheModuleSettingKeepsMessageContentOutOfExports));
        await ContentCaptureOnFacts.SendAsync(brain, workspace, operation, ct, User);
        var capture = await operation.CompleteAsync(ct);
        Assert.Contains(capture.Spans, span => span.IsGenAi);
        Assert.DoesNotContain(capture.PrivacySpans, span => span.Text.Contains("Show active leads", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Logs, log => log.Text.Contains("Show active leads", StringComparison.Ordinal));
    }
}
