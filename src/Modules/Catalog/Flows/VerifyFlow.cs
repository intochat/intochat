using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Kernel;
using DigitalBrain.Operations;
using DigitalBrain.Signals;
using IntoChat.OS.Primitives;

namespace IntoChat.OS.Modules.Apps.Flows;

// A verification's own state: the trace of the unit under check and the MCP tools the OS exposes to the checking agent. Checks fills
// Ok and Findings from the agent's verdict.
public sealed class VerifyInner : AgentInner
{
    public string UnitTraceId { get; set; } = "";

    public string[] McpToolNames { get; set; } = [];

    public bool? Ok { get; set; }

    public string[]? Findings { get; set; }
}

// One verification: checks (the flow agent, told the OS's MCP tools, answers { "ok", "findings" }) and telemetry (the unit's trace
// holds no failed flow or failed operation). Either failing fails the flow.
[DigitalBrain.Neurons.Neuron(Alias)]
public sealed class VerifyFlow(ISignalHistory history, AgentStepOptions? options = null) : AgentFlow<VerifyInner>(options), IFlowStarter
{
    public const string Alias = "flow.verify";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] Names = ["checks", "telemetry"];

    private sealed record Verdict(bool Ok, string[]? Findings);

    protected override IReadOnlyList<string> Steps => Names;

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
        => Start(title, owner, parentFlowId, JsonSerializer.Deserialize<VerifyInner>(innerJson, Json) ?? new VerifyInner());

    public Task<FlowHead> Head() => Task.FromResult(new FlowHead([.. Names], State.Owner, State.TraceId, State.ParentFlowId, State.Started,
        JsonSerializer.Serialize(State.Inner, Json), State.Title, State.Status, [.. State.Done], State.Ended, null));

    protected override async Task<StepResult<VerifyInner>> RunStep(string step, VerifyInner inner, CancellationToken ct)
    {
        switch (step)
        {
            case "checks":
                return await Ask(step, inner, Prompt(inner));
            case "telemetry":
                Judge(inner);
                await Telemetry(inner);
                return inner.Advance();
            default:
                throw new FlowFailed(step, $"Unknown step {step}.");
        }
    }

    private string Prompt(VerifyInner inner) =>
        $$"""
        Verify: {{State.Title}}
        Use these tools of the OS to check that it works: {{(inner.McpToolNames.Length == 0 ? "(none listed)" : string.Join(", ", inner.McpToolNames))}}.
        Reply with only a JSON verdict: { "ok": true or false, "findings": [ "..." ] }.
        """;

    // The agent's verdict, stored on the first telemetry run; a failing verdict fails the checks.
    private static void Judge(VerifyInner inner)
    {
        if (inner.Ok is null)
        {
            var verdict = ParseReply<Verdict>("checks", inner.Reply, Json) ?? throw new FlowFailed("checks", "the agent gave no verdict");
            inner.Ok = verdict.Ok;
            inner.Findings = verdict.Findings ?? [];
        }
        if (inner.Ok == false) { throw new FlowFailed("checks", string.Join("; ", inner.Findings ?? [])); }
    }

    private async Task Telemetry(VerifyInner inner)
    {
        if (string.IsNullOrWhiteSpace(inner.UnitTraceId)) { throw new FlowFailed("telemetry", "no unit trace to check"); }
        // Read page by page from the last sequence seen until a short page.
        var failures = new List<string>();
        var since = 0L;
        while (true)
        {
            var page = await history.ReadTrace(inner.UnitTraceId, since, SignalPage.MaxLimit, CancellationToken.None);
            failures.AddRange(page.Signals.Select(entry => entry.Signal switch
            {
                FlowDone { Outcome: FlowOutcome.Failed } done => $"flow {done.FlowId} failed: {done.Detail}",
                OperationDone { Result: OperationResult.Failed failed } operation => $"operation {operation.Operation.Value} failed: {failed.Message}",
                _ => null,
            }).OfType<string>());
            if (page.Signals.Count < SignalPage.MaxLimit) { break; }
            since = page.Signals[^1].Sequence;
        }
        if (failures.Count > 0) { throw new FlowFailed("telemetry", string.Join("; ", failures)); }
    }
}
