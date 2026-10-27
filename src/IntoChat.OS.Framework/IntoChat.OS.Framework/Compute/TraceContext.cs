using System.Diagnostics;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Contracts.Compute;

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
