using System.Diagnostics;
using System.Text.Json;
using DigitalBrain.Contracts;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Agents;

// The agent's own spans, named after the OpenTelemetry GenAI conventions: one invoke_agent span per turn and one execute_tool span per
// tool call, so a trace shows what the model asked for and what each tool did. The turn loop runs tools itself (no function-invoking
// client), so without these a tool call was visible only inside the next chat span's messages. Arguments and results are recorded only
// when sensitive telemetry is on (local development); names, ids and outcomes always are.
internal static class AgentTelemetry
{
    internal const string SourceName = "IntoChat.OS.Modules.AI.Agents";
    internal static readonly ActivitySource Source = new(SourceName);
    private const int MaxRecorded = 4_000;

    internal static Activity? Turn(string agent, string conversation, string runId)
    {
        var activity = Source.StartActivity($"invoke_agent {agent}", ActivityKind.Internal);
        activity?.SetTag("gen_ai.operation.name", "invoke_agent");
        activity?.SetTag("gen_ai.agent.name", agent);
        activity?.SetTag("gen_ai.conversation.id", conversation);
        activity?.SetTag("intochat.run.id", runId);
        return activity;
    }

    internal static Activity? Tool(FunctionCallContent call, bool sensitive, ToolSchema? schema = null)
    {
        var activity = Source.StartActivity($"execute_tool {call.Name}", ActivityKind.Internal);
        if (activity is null) { return null; }
        activity.SetTag("gen_ai.operation.name", "execute_tool");
        activity.SetTag("gen_ai.tool.name", call.Name);
        activity.SetTag("gen_ai.tool.call.id", call.CallId);
        // The neuron surface names its target; that is what a reader of the trace needs to know.
        if ((schema?.Contract ?? Text(call, "contract")) is { } contract) { activity.SetTag("intochat.contract", contract); }
        if ((schema?.Method ?? Text(call, "method")) is { } method) { activity.SetTag("intochat.method", method); }
        if (Text(call, "key") is { } key) { activity.SetTag("intochat.key", key); }
        if (sensitive) { activity.SetTag("gen_ai.tool.call.arguments", Clip(JsonSerializer.Serialize(call.Arguments))); }
        return activity;
    }

    internal static void Result(Activity? activity, string serialized, bool sensitive)
    {
        if (activity is null) { return; }
        var failed = serialized.Contains("\"isError\":true", StringComparison.Ordinal);
        activity.SetStatus(failed ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
        if (sensitive) { activity.SetTag("gen_ai.tool.call.result", Clip(serialized)); }
    }

    internal static void Failed(Activity? activity, Exception error)
    {
        activity?.SetStatus(ActivityStatusCode.Error, error.Message);
        activity?.AddException(error);
    }

    internal static void FirstToken(Activity? turn, TimeSpan elapsed)
    {
        if (turn is null) { return; }
        turn.AddEvent(new ActivityEvent("gen_ai.first_token", tags: new ActivityTagsCollection
        {
            ["intochat.first_token_ms"] = elapsed.TotalMilliseconds,
        }));
    }

    private static string? Text(FunctionCallContent call, string name)
        => call.Arguments?.TryGetValue(name, out var value) == true ? value?.ToString() : null;

    private static string Clip(string text) => text.Length <= MaxRecorded ? text : text[..MaxRecorded] + "…";
}
