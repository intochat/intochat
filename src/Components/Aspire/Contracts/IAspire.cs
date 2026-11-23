using System.ComponentModel;
using DigitalBrain.Contracts;

namespace IntoChat.Aspire;

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

[NeuronAlias("aspire", Formerly = ["IntoChat.OS.Modules.Microsoft.Aspire.IAspire"]), DefaultNeuronType("aspire")]
[Description("Live Aspire runtime topology, console logs, and guarded command execution for the running app.")]
public interface IAspire
{
    const string KeyPattern = "diagnostics/aspire";

    [ReadOnlyCall, Interleaves]
    Task<AspireResources> Resources();

    [ReadOnlyCall, Interleaves]
    Task<string[]> ConsoleLog(string resource, int tail = 50);

    [Description("Run a listed command on a running resource; non-person callers require person approval and the kernel itself is always refused. When refused, the returned result explains why.")]
    Task<AspireCommandResult> Execute(string resource, string command);

    Task RecordLogAsync(AspireLogEntry entry);

    Task RecordTraceAsync(AspireTraceSpan span);
}
