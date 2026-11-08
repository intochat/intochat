using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.Trace;

// One journaled signal of a trace, flattened for the wire: a Signal is abstract and does not cross a call.
// Publisher is the NeuronId text Causation.Address uses; Signal is the alias; Payload is the signal's web JSON, trimmed.
public sealed record TraceEntry(string Publisher, long Sequence, string Signal, DateTimeOffset At, string? TraceId, string? CausationId, string Payload);

// A page of a trace, by sequence.
public sealed record TracePage(TraceEntry[] Entries);

// Reads one trace from the brain's journal for the Trace app, which cannot reach the signal history itself.
[NeuronAlias("trace.reader"), DefaultNeuronType("trace.reader")]
public interface ITraceReader
{
    const string DefaultKey = "trace/reader";

    static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), "trace.reader", DefaultKey);

    // The journaled signals carrying traceId after since (exclusive), at most limit (1..1000), by sequence.
    [DigitalBrain.ReadOnlyCall]
    Task<TracePage> ReadTrace(string traceId, long since, int limit);
}
