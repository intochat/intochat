using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using Xunit;
using static IntoChat.OS.Modules.Apps.Tests.Integration.Flows.FlowTypeFacts;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

public sealed class VerifyFlowFacts
{
    private const string Verdict = """
        ```json
        { "ok": true, "findings": [] }
        ```
        """;

    [Fact]
    public async Task VerifyFailsOnFailedSignalInTrace()
    {
        ScriptedAgent.Replies["Verify"] = Verdict;
        await using var fixture = new FlowTypesFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Index(runtime);
        var trace = TraceContext.Begin("person", runtime.Brain.Value, new NeuronId(runtime.Brain, "edge", "unit"));
        using (TraceContext.Enter(trace))
        {
            await runtime.Get<IFlowSignalPump>(runtime.Id(FlowSignalPump.Alias, "pump")).Failed("unit-flow", "the unit broke");
        }

        var inner = new VerifyInner { UnitTraceId = trace.TraceId, McpToolNames = ["neuron_call", "flows_list"] };
        var id = await flows.Start(VerifyFlow.Alias, "Tick poller", null, JsonSerializer.Serialize(inner, VerifyFlow.Json), Ct);

        var view = await Eventually(() => flows.Read(id, Ct), v => v.Status is FlowStatus.Failed or FlowStatus.Completed);
        Assert.Equal(FlowStatus.Failed, view.Status);
        Assert.Equal(["checks"], view.Done);
        Assert.Contains("the unit broke", view.Detail, StringComparison.Ordinal);
        Assert.Contains("neuron_call, flows_list", ScriptedAgent.Prompts[id], StringComparison.Ordinal);
        Assert.Contains(await runtime.Signals<FlowStep>(Id(id), Ct), step => step is { Step: "telemetry", Outcome: StepOutcome.Failed });
    }
}
