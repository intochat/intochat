using System.Net.Http.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.Models;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Testing.E2E;
using DigitalBrain.Testing.E2E.Agent;
using DigitalBrain.Testing.E2E.Diagnostics;
using DigitalBrain.Testing.E2E.Workspace;
using static DigitalBrain.Testing.E2E.Diagnostics.TraceAssertions;

namespace DigitalBrain.Framework.Tests.E2E;

// The module option is the only content-capture switch. Check the exported spans/logs,
// including a Production host to catch accidental environment/profile gates.
public sealed class ContentCaptureFacts
{
    private const string OrdinaryContent = "Show active leads";

    [Theory(Timeout = 300_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModuleSettingControlsExportedContent(bool enabled)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var collector = TestTelemetryCollector.Start();
        await using var model = await ScriptedModelServer.StartAsync(ct);
        await using var brain = await CaptureTest.CreateAsync(model, collector, enabled, ct);
        await UserConnections.AuthorizeDatabaseAsync(brain, "capture", ct);
        await LeadData.SeedAsync(brain, "Capture", ct);
        // A person talks to the assistant app; its answer is the started run's id.
        using var response = await brain.HttpClient.PostAsJsonAsync("/brains/capture/apps/assistant/ask",
            new { operation = "send", input = OrdinaryContent }, ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        await WaitForAsync(() => collector.Snapshot().Any(IsGenAiSpan), ct);
        if (enabled)
        {
            await WaitForAsync(() => collector.Snapshot().Any(span => TextOf(span).Contains(OrdinaryContent, StringComparison.Ordinal)), ct);
            Assert.Contains(collector.Snapshot(), span => TextOf(span).Contains(OrdinaryContent, StringComparison.Ordinal));
        }
        else
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            Assert.DoesNotContain(collector.Snapshot(), span => TextOf(span).Contains(OrdinaryContent, StringComparison.Ordinal));
            Assert.DoesNotContain(collector.LogSnapshot(), log => TextOf(log).Contains(OrdinaryContent, StringComparison.Ordinal));
        }
        Assert.Contains(collector.Snapshot(), IsGenAiSpan);
        Assert.Contains(collector.Snapshot(), span => span.Attributes.Keys.Any(key => key.StartsWith("gen_ai.usage", StringComparison.Ordinal)));
        Assert.Empty(collector.Errors());
    }





    private static class CaptureTest
    {
        // Every boot already carries OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true from Aspire,
        // so the ambient SDK setting never overrides the explicit module setting in either case.
        public static Task<E2EBrain> CreateAsync(ScriptedModelServer model, TestTelemetryCollector collector,
            bool enableSensitiveData, CancellationToken ct)
            => ReferenceBrain.Create()
                .ConfigureModule<AIModule, AIOptions>(ai =>
                {
                    ai.WithModelEndpoint(AiProvider.OpenAI, model.Endpoint);
                    ai.Telemetry.EnableSensitiveData = enableSensitiveData;
                })
                .InEnvironment("Production")
                .ExportingTelemetryTo(collector)
                .StartAsync(ct);
    }
}
