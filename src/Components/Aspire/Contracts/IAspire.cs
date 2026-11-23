using System.ComponentModel;
using DigitalBrain.Contracts;

namespace IntoChat.Aspire;

public sealed record AspireResourceView(
    string Name,
    string State,
    string Endpoint);

public sealed record AspireLogEntry(
    DateTimeOffset Timestamp,
    string Resource,
    string Severity,
    string Message,
    string? Exception = null);

public sealed record AspireTraceSpan(
    string TraceId,
    string SpanId,
    string Resource,
    string Name,
    string Status,
    long DurationMicroseconds,
    string? Error = null);

[DigitalBrain.Signal("aspire.telemetry.changed")]
public sealed record AspireTelemetryChanged(int LogCount, int TraceCount) : DigitalBrain.Signal;

// Not accepted: Reason is "pending" (Confirmation is the plan id; the outcome arrives as a signal), "unavailable", "unknown"
// (Known lists the current names), "self", "declined" or "failed" (Message says why).
public sealed record AspireRestartResult(string Resource, bool Accepted, string? Reason = null, string[]? Known = null, string? Message = null, string? Confirmation = null);

[DigitalBrain.Signal("aspire.resource.restart-requested")]
public sealed record AspireResourceRestartRequested(string Resource, string Confirmation) : DigitalBrain.Signal;

// A restart that waited for the person and did not run: "declined", "failed", "unknown", "self" or "unavailable".
[DigitalBrain.Signal("aspire.resource.restart-refused")]
public sealed record AspireResourceRestartRefused(string Resource, string Reason, string? Message = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("aspire.resource.restarted")]
public sealed record AspireResourceRestarted(string Resource, DateTimeOffset At) : DigitalBrain.Signal;

[NeuronAlias("aspire", Formerly = ["IntoChat.OS.Modules.Microsoft.Aspire.IAspire"]), DefaultNeuronType("aspire")]
[Description("Local and cloud telemetry inspector for resources, console logs, structured logs, and distributed traces.")]
public interface IAspire
{
    const string KeyPattern = "diagnostics/aspire";

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<AspireResourceView[]> GetResourcesAsync();

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<string[]> GetConsoleLogsAsync(string resourceName, int tailLines = 50);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<AspireLogEntry[]> GetStructuredLogsAsync(string? resourceName = null, string? minSeverity = null);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<AspireTraceSpan[]> GetTracesAsync(string? resourceName = null, bool errorsOnly = false);

    // Reads the running app through Aspire's own MCP server (local development): tool is one of list_resources, list_console_logs,
    // list_structured_logs, list_traces or list_trace_structured_logs; argumentsJson is a JSON object such as {"resourceName":"os"}.
    [Description("Read the running app through Aspire's MCP server. tool: list_resources, list_console_logs, list_structured_logs, list_traces or list_trace_structured_logs. argumentsJson: a JSON object, e.g. {\"resourceName\":\"os\",\"search\":\"error\"}.")]
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<string> Ask(string tool, string argumentsJson = "{}");

    [Description("Restart one resource of the running app by its exact name; the kernel itself is refused and a refused result says why. A caller that is not a person gets \"pending\", and the restart runs once the person approves.")]
    Task<AspireRestartResult> Restart(string resourceName);

    Task RecordLogAsync(AspireLogEntry entry);

    Task RecordTraceAsync(AspireTraceSpan span);
}
