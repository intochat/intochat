using System.Diagnostics;

namespace DigitalBrain.Capabilities.Compute;

public static class TraceContext
{
    public const string RequestContextKey = "intochatos.trace";

    private static readonly AsyncLocal<Trace?> Ambient = new();
    private static Func<string, object?>? s_requestContextGet;
    private static Action<string, object>? s_requestContextSet;
    private static Func<string, bool>? s_requestContextRemove;

    public static Trace? Current => s_requestContextGet?.Invoke(RequestContextKey) as Trace ?? Ambient.Value;

    /// <summary>Registers a static host request-context bridge (e.g. Orleans <c>RequestContext</c>) without runtime reflection.</summary>
    public static void RegisterRequestContextAccessor(
        Func<string, object?> get,
        Action<string, object> set,
        Func<string, bool> remove)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(remove);
        s_requestContextGet = get;
        s_requestContextSet = set;
        s_requestContextRemove = remove;
    }

    public static IDisposable Enter(Trace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var previousAmbient = Ambient.Value;
        var previousRequest = s_requestContextGet?.Invoke(RequestContextKey);
        Ambient.Value = trace;
        s_requestContextSet?.Invoke(RequestContextKey, trace);
        return new Scope(previousAmbient, previousRequest);
    }

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
            if (previousRequest is null)
            {
                _ = s_requestContextRemove?.Invoke(RequestContextKey);
            }
            else
            {
                s_requestContextSet?.Invoke(RequestContextKey, previousRequest);
            }
        }
    }
}
