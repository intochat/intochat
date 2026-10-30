using System.Diagnostics;
using DigitalBrain;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework.Compute;

// The trace of the work in progress: RequestContext on grain calls, an AsyncLocal everywhere else.
public static class TraceContext
{
    public const string RequestContextKey = "intochatos.trace";
    private static readonly AsyncLocal<Trace?> Ambient = new();

    public static Trace? Current => RequestContext.Get(RequestContextKey) as Trace ?? Ambient.Value;

    public static IDisposable Enter(Trace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var previousAmbient = Ambient.Value;
        var previousRequest = RequestContext.Get(RequestContextKey);
        Ambient.Value = trace;
        RequestContext.Set(RequestContextKey, trace);
        return new Scope(previousAmbient, previousRequest);
    }

    // An os key used only as a cost origin. "{brain}/edge" is the trusted edge (type edge, key request).
    // Any other "{brain}/…" key parses as brain/type/key, so Origin.ToString() stays the os key when it already has a type segment.
    public static NeuronId OriginOf(string brainId, string osKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(osKey);
        if (osKey == $"{brainId}/edge") { return new NeuronId(new BrainId(brainId), "edge", "request"); }
        if (NeuronId.TryParse(osKey, out var parsed) && parsed.Brain.Value == brainId) { return parsed; }
        var rest = osKey.StartsWith(brainId + "/", StringComparison.Ordinal) ? osKey[(brainId.Length + 1)..] : osKey;
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var type = slash < 0 ? rest : rest[..slash];
        var key = slash < 0 ? "request" : rest[(slash + 1)..];
        return new NeuronId(new BrainId(brainId), type, key);
    }

    public static Trace Begin(string payerAccountId, string brainId, NeuronId origin, long? budgetMicros = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payerAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        var traceId = Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N");
        return new()
        {
            TraceId = traceId,
            PayerAccountId = payerAccountId,
            BrainId = brainId,
            Origin = origin,
            BudgetMicros = budgetMicros,
        };
    }

    private sealed class Scope(Trace? previousAmbient, object? previousRequest) : IDisposable
    {
        public void Dispose()
        {
            Ambient.Value = previousAmbient;
            if (previousRequest is null) { RequestContext.Remove(RequestContextKey); }
            else { RequestContext.Set(RequestContextKey, previousRequest); }
        }
    }
}
