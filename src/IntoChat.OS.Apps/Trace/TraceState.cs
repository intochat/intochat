namespace IntoChat.OS.Apps.Trace;

// TraceId is the open trace; Since the journal sequence the hops were read after; Live follows the latest hop;
// SelectedSequence is the hop the replay bar stands on when not live.
public sealed record TraceState(string? TraceId, long Since, bool Live, long? SelectedSequence)
{
    public TraceState() : this(null, 0, true, null) { }
}

// One journaled signal of the trace, folded: Depth is its causation chain length to a root hop.
public sealed record TraceHop(long Sequence, string Publisher, string Alias, string Address, string? Cause, int Depth, DateTimeOffset At, string Payload, string? FlowId);
